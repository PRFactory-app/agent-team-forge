using System.Diagnostics;
using AgentTeamForge.Business.Features.Agents.Backends;
using AgentTeamForge.Business.Features.Processes;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>One child process call. A null environment value removes the variable.</summary>
public sealed record ProcessSpec(string File, string[] Args, IReadOnlyDictionary<string, string?>? Env = null, string? Stdin = null, TimeSpan? Timeout = null);
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// pull-request-v1: opens the pull request for a branch this machine published, on GitHub with the account
/// chosen at connect time, or on Bitbucket Cloud with the stored git credential. The token is read per call,
/// kept in memory and never logged.
/// </summary>
public sealed partial class PRFactoryPullRequests(
    string server, IReadOnlyList<RepositoryMapping> repositories, string? gitHubUser, PRFactoryClient client,
    PRFactoryPublicationStore publications, Action<string>? log = null, Func<ProcessSpec, CancellationToken, Task<ProcessResult>>? run = null,
    HttpMessageHandler? http = null)
{
    sealed class Failure(string message, string details = "") : Exception(message)
    {
        public string Details { get; } = details;
    }

    readonly Func<ProcessSpec, CancellationToken, Task<ProcessResult>> runner = run ?? RunProcessAsync;
    // Tests replace the REST transport of the hosts that are not driven through a CLI.
    readonly HttpMessageHandler? httpHandler = http;
    // The default runner resolves the real gh once; an injected runner keeps the plain name.
    Task<string> GhPath() => run is null ? Task.Run(() => ToolExecutable.Resolve("gh")) : Task.FromResult("gh");

    static Failure TimedOut(string what, TimeSpan limit) =>
        new($"{what} did not finish within {limit.TotalSeconds:0} s; the gh on the daemon PATH may be a looping wrapper.");

    public static bool IsGitHubLogin(string value) => LoginPattern().IsMatch(value);

    public async Task HandleAsync(PRFactoryWorkItem item, CancellationToken ct)
    {
        string? token = null;
        try
        {
            var request = Parse(item);
            var remote = await Authorize(request, ct);
            IPullRequestHost host;
            switch (remote.Host)
            {
                case "github.com":
                    var login = gitHubUser ?? remote.User
                        ?? throw new Failure("No GitHub account configured for this connector; reconnect with --github-user.");
                    token = await TokenAsync(login, ct);
                    host = new GitHub(this, token, remote.Owner, remote.Repo);
                    break;
                case "bitbucket.org":
                    token = await BitbucketTokenAsync(remote.User, ct);
                    host = new Bitbucket(this, token, remote.Owner, remote.Repo);
                    break;
                default:
                    throw new Failure($"Worker-opened pull requests are not supported for {remote.Host}.");
            }
            var head = await host.HeadShaAsync(request.HeadBranch, ct);
            if (!SameCommit(head, request.HeadSha))
            {
                throw new Failure($"Remote branch {request.HeadBranch} moved; republish it before opening the pull request.");
            }
            var created = false;
            var existing = await host.FindOpenAsync(request, ct);
            if (existing is null)
            {
                existing = await host.CreateAsync(request, ct) ?? await host.FindOpenAsync(request, ct)
                    ?? throw new Failure($"{host.Name} did not list the pull request after creating it.");
                created = true;
            }
            if (!SameCommit(existing.HeadSha, request.HeadSha))
            {
                throw new Failure($"The open pull request for {request.HeadBranch} points at a different commit; update or close it.");
            }
            var result = new PRFactoryPullRequestResult("pull-request-result", 1, existing.Number, existing.Url, request.HeadSha, created);
            var cleanupWip = await client.CompleteAsync(item.Id, item.LeaseToken, JsonSerializer.Serialize(result, PRFactoryWorkItemJson.Default.PRFactoryPullRequestResult),
                ct, request.HeadBranch, request.HeadSha);
            if (cleanupWip is not null)
            {
                await PRFactoryWorkItems.CleanupWipAsync(item.Id, repositories.First(r => r.Id == request.RepositoryId).Directory,
                    remote.Url, cleanupWip, log, ct);
            }
        }
        catch (Failure failure)
        {
            await FailAsync(item, failure.Message, failure.Details, token, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or PRFactoryLeaseLostException
            or WorkerTokenRejectedException or HttpRequestException))
        {
            await FailAsync(item, "Could not open the pull request: " + ex.GetType().Name, "", token, ct);
        }
    }

    /// <summary>An open pull request; HeadSha may be abbreviated (Bitbucket returns 12 characters).</summary>
    sealed record OpenPullRequest(int Number, string Url, string HeadSha);

    /// <summary>One git host's pull request API. Each call throws Failure with a scrubbable message.</summary>
    interface IPullRequestHost
    {
        string Name { get; }
        Task<string> HeadShaAsync(string branch, CancellationToken ct);
        Task<OpenPullRequest?> FindOpenAsync(PRFactoryPullRequestRequest request, CancellationToken ct);
        /// <summary>Returns the created pull request, or null when the host must be listed again.</summary>
        Task<OpenPullRequest?> CreateAsync(PRFactoryPullRequestRequest request, CancellationToken ct);
    }

    // A full 40-character sha against a host that may abbreviate it.
    static bool SameCommit(string remote, string full) =>
        remote.Length >= 7 && full.StartsWith(remote, StringComparison.OrdinalIgnoreCase);

    sealed class GitHub(PRFactoryPullRequests owner, string token, string ownerName, string repo) : IPullRequestHost
    {
        readonly Dictionary<string, string?> env = new()
        {
            ["GH_TOKEN"] = token,
            ["GH_HOST"] = "github.com",
            ["MISE_QUIET"] = "1",
            ["GH_PROMPT_DISABLED"] = "1",
            ["GITHUB_TOKEN"] = null,
            ["GH_ENTERPRISE_TOKEN"] = null
        };

        public string Name => "GitHub";

        public async Task<string> HeadShaAsync(string branch, CancellationToken ct) =>
            (await owner.Gh(env, token, ["api", $"repos/{ownerName}/{repo}/branches/{branch}", "--jq", ".commit.sha"], null, ct)).Trim();

        public Task<OpenPullRequest?> FindOpenAsync(PRFactoryPullRequestRequest request, CancellationToken ct) =>
            owner.OpenAsync(env, token, ownerName, repo, request, ct);

        public async Task<OpenPullRequest?> CreateAsync(PRFactoryPullRequestRequest request, CancellationToken ct)
        {
            await owner.Gh(env, token, ["pr", "create", "--repo", $"{ownerName}/{repo}", "--head", request.HeadBranch, "--base", request.BaseBranch,
                "--title", request.Title, "--body-file", "-"], request.Body, ct);
            return null;
        }
    }

    async Task FailAsync(PRFactoryWorkItem item, string message, string details, string? token, CancellationToken ct)
    {
        message = Scrub(message, token);
        log?.Invoke($"PRFactory work item {item.Id:D} pull request failed: {message}");
        await client.FailAsync(item.Id, item.LeaseToken, message, ct, shouldRetry: false, details: Scrub(details, token));
    }

    static PRFactoryPullRequestRequest Parse(PRFactoryWorkItem item)
    {
        PRFactoryPullRequestRequest? request = null;
        try
        {
            request = item.ContextJson is { Length: > 0 } json
                ? JsonSerializer.Deserialize(json, PRFactoryWorkItemJson.Default.PRFactoryPullRequestRequest) : null;
        }
        catch (JsonException) { }
        // Names reach argv and a URL path, so a leading dash or unusual character is refused outright.
        if (request is not { Kind: "pull-request-create", Version: 1 } || !BranchName(request.HeadBranch) || !BranchName(request.BaseBranch)
            || !Sha().IsMatch(request.HeadSha) || string.IsNullOrWhiteSpace(request.Title))
        {
            throw new Failure("The pull request request from PRFactory is not valid.");
        }
        return request;
    }

    async Task<RemoteRepository> Authorize(PRFactoryPullRequestRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (repositories.All(r => r.Id != request.RepositoryId))
        {
            throw new Failure("This machine has no checkout mapped for the repository.");
        }
        var receipt = publications.VerifiedFor(server, request.SourceWorkItemId, request.RepositoryId).FirstOrDefault(p =>
            p.PublishBranch == request.HeadBranch && string.Equals(p.HeadSha, request.HeadSha, StringComparison.OrdinalIgnoreCase))
            ?? throw new Failure($"This machine did not publish {request.HeadBranch}@{request.HeadSha}.");
        return await Task.FromResult(ParseRemote(receipt.Remote) with { Url = receipt.Remote });
    }

    internal sealed record RemoteRepository(string Host, string Owner, string Repo, string? User, string Url = "");

    /// <summary>
    /// Splits an https, ssh:// or scp-style remote into host and repository. Add a host here and in HandleAsync.
    /// Only the username of an https remote counts; any password or token in the URL is ignored.
    /// </summary>
    internal static RemoteRepository ParseRemote(string remote)
    {
        var unsupported = new Failure("Only github.com and bitbucket.org remotes are supported for worker-opened pull requests.");
        string host, path;
        string? user = null;
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "ssh"))
        {
            host = uri.Host;
            path = uri.AbsolutePath;
            if (uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length > 0) { user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]); }
        }
        else if (ScpRemote().Match(remote) is { Success: true } scp)
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else { throw unsupported; }
        host = host.ToLowerInvariant();
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) { path = path[..^4]; }
        var parts = path.Split('/');
        if (parts.Length != 2 || !RepoPart().IsMatch(parts[0]) || !RepoPart().IsMatch(parts[1])) { throw unsupported; }
        return host switch
        {
            "github.com" => new(host, parts[0], parts[1], user is not null && IsGitHubLogin(user) ? user : null),
            "bitbucket.org" => new(host, parts[0], parts[1], user is not null && CredentialUser().IsMatch(user) ? user : null),
            _ => throw unsupported
        };
    }

    // Always the named account; gh's active account is never consulted.
    async Task<string> TokenAsync(string login, CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["GH_TOKEN"] = null, ["GITHUB_TOKEN"] = null, ["GH_HOST"] = "github.com", ["MISE_QUIET"] = "1" };
        ProcessResult result;
        try { result = await runner(new ProcessSpec(await GhPath(), ["auth", "token", "--user", login], env, null, TokenTimeout), ct); }
        catch (TimeoutException) { throw TimedOut("gh auth token", TokenTimeout); }
        catch (System.ComponentModel.Win32Exception) { throw new Failure("gh is not installed or not on PATH on this machine."); }
        // A gh shim (e.g. mise) may print progress lines before the token; the token is the last non-empty stdout line.
        var token = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        if (result.ExitCode != 0 || token.Length == 0)
        {
            throw new Failure($"gh has no login for {login}; run gh auth login on this machine.");
        }
        // Never let anything but a plain token become a header value.
        if (!WholeToken().IsMatch(token))
        {
            throw new Failure($"gh auth token for {login} did not end with a plain token on stdout (unexpected output from gh or a gh shim); run gh directly or fix the shim.");
        }
        return token;
    }

    // Only a same-repository PR for the published repository, branch and base qualifies; forks share branch names.
    async Task<OpenPullRequest?> OpenAsync(IReadOnlyDictionary<string, string?> env, string token, string owner, string repo,
        PRFactoryPullRequestRequest request, CancellationToken ct)
    {
        var json = await Gh(env, token, ["pr", "list", "--repo", $"{owner}/{repo}", "--head", request.HeadBranch, "--base", request.BaseBranch,
            "--state", "open", "--json", "number,url,headRefOid,headRefName,baseRefName,isCrossRepository,headRepository,headRepositoryOwner"], null, ct);
        try
        {
            using var document = JsonDocument.Parse(json);
            var eligible = new List<OpenPullRequest>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var url = entry.GetProperty("url").GetString();
                if (entry.GetProperty("isCrossRepository").GetBoolean()
                    || entry.GetProperty("headRefName").GetString() != request.HeadBranch
                    || entry.GetProperty("baseRefName").GetString() != request.BaseBranch
                    || !string.Equals(entry.GetProperty("headRepositoryOwner").GetProperty("login").GetString(), owner, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(entry.GetProperty("headRepository").GetProperty("name").GetString(), repo, StringComparison.OrdinalIgnoreCase)
                    || url is null || !url.StartsWith("https://", StringComparison.Ordinal))
                {
                    continue;
                }
                eligible.Add(new(entry.GetProperty("number").GetInt32(), url, entry.GetProperty("headRefOid").GetString() ?? ""));
            }
            return eligible.FirstOrDefault(p => string.Equals(p.HeadSha, request.HeadSha, StringComparison.OrdinalIgnoreCase))
                ?? eligible.FirstOrDefault();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new Failure("GitHub returned an unreadable pull request list.");
        }
    }

    async Task<string> Gh(IReadOnlyDictionary<string, string?> env, string token, string[] args, string? stdin, CancellationToken ct)
    {
        ProcessResult result;
        try { result = await runner(new ProcessSpec(await GhPath(), args, env, stdin, CallTimeout), ct); }
        catch (TimeoutException) { throw TimedOut("gh", CallTimeout); }
        catch (System.ComponentModel.Win32Exception) { throw new Failure("gh is not installed or not on PATH on this machine."); }
        if (result.ExitCode != 0)
        {
            var tail = Scrub(result.Stderr.Trim(), token);
            var first = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? $"gh exited {result.ExitCode}";
            throw new Failure($"GitHub refused: {first[..Math.Min(first.Length, 300)]}", tail[Math.Max(0, tail.Length - 2000)..]);
        }
        return result.Stdout;
    }

    static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(20), CallTimeout = TimeSpan.FromMinutes(2);

    static string Scrub(string text, string? token)
    {
        if (token is { Length: > 0 }) { text = text.Replace(token, "***", StringComparison.Ordinal); }
        return TokenPattern().Replace(text, "***");
    }

    static bool BranchName(string name) => BranchPattern().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    static async Task<ProcessResult> RunProcessAsync(ProcessSpec spec, CancellationToken ct)
    {
        var info = new ProcessStartInfo(spec.File)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.Stdin is not null,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in spec.Args) { info.ArgumentList.Add(arg); }
        foreach (var (name, value) in spec.Env ?? new Dictionary<string, string?>())
        {
            if (value is null) { info.Environment.Remove(name); } else { info.Environment[name] = value; }
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("process did not start");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (spec.Stdin is not null)
        {
            await process.StandardInput.WriteAsync(spec.Stdin);
            process.StandardInput.Close();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limit = spec.Timeout ?? TimeSpan.FromMinutes(2);
        timeout.CancelAfter(limit);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            OwnedProcessTermination.Kill(process);
            if (ct.IsCancellationRequested) { throw; }
            throw new TimeoutException($"{spec.File} did not finish within {limit.TotalSeconds:0} s");
        }
        return new(process.ExitCode, await output, await error);
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")] private static partial Regex LoginPattern();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")] private static partial Regex RepoPart();
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._/-]{0,199}$")] private static partial Regex BranchPattern();
    [GeneratedRegex("^[0-9a-fA-F]{40}$")] private static partial Regex Sha();
    [GeneratedRegex("^(?:gh[opsur]_|github_pat_)[A-Za-z0-9_]+$")] private static partial Regex WholeToken();
    // GitHub tokens, and Atlassian access tokens (ATCTT3…), API tokens (ATATT3…) and app passwords (ATBB…).
    [GeneratedRegex("(gh[opsu]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|AT(?:CTT|ATT)3[A-Za-z0-9_=-]{16,}|ATBB[A-Za-z0-9]{16,})")] private static partial Regex TokenPattern();
    [GeneratedRegex("^[A-Za-z0-9._-]+@(?<host>[A-Za-z0-9.-]+):(?<path>[^:]+)$")] private static partial Regex ScpRemote();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")] private static partial Regex CredentialUser();
}
