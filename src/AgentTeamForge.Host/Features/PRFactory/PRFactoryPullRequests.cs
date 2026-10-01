using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTeamForge.DAL.Features.Jobs;

namespace AgentTeamForge.Host.Features.PRFactory;

/// <summary>One child process call. A null environment value removes the variable.</summary>
public sealed record ProcessSpec(string File, string[] Args, IReadOnlyDictionary<string, string?>? Env = null, string? Stdin = null);
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// pull-request-v1: opens the pull request for a branch this machine published, using the GitHub
/// account chosen at connect time. The token is read per call, kept in memory and never logged.
/// </summary>
public sealed partial class PRFactoryPullRequests(
    string server, IReadOnlyList<RepositoryMapping> repositories, string? gitHubUser, PRFactoryClient client,
    PRFactoryPublicationStore publications, Action<string>? log = null, Func<ProcessSpec, CancellationToken, Task<ProcessResult>>? run = null)
{
    sealed class Failure(string message, string details = "") : Exception(message)
    {
        public string Details { get; } = details;
    }

    readonly Func<ProcessSpec, CancellationToken, Task<ProcessResult>> runner = run ?? RunProcessAsync;

    public static bool IsGitHubLogin(string value) => LoginPattern().IsMatch(value);

    public async Task HandleAsync(PRFactoryWorkItem item, CancellationToken ct)
    {
        string? token = null;
        try
        {
            var request = Parse(item);
            var (owner, repo, remoteUser) = await Authorize(request, ct);
            var login = gitHubUser ?? remoteUser
                ?? throw new Failure("No GitHub account configured for this connector; reconnect with --github-user.");
            token = await TokenAsync(login, ct);
            var env = new Dictionary<string, string?>
            {
                ["GH_TOKEN"] = token,
                ["GH_HOST"] = "github.com",
                ["GH_PROMPT_DISABLED"] = "1",
                ["GITHUB_TOKEN"] = null,
                ["GH_ENTERPRISE_TOKEN"] = null
            };
            var slug = $"{owner}/{repo}";
            var head = (await Gh(env, token, ["api", $"repos/{slug}/branches/{request.HeadBranch}", "--jq", ".commit.sha"], null, ct)).Trim();
            if (!string.Equals(head, request.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new Failure($"Remote branch {request.HeadBranch} moved; republish it before opening the pull request.");
            }
            var created = false;
            var existing = await OpenAsync(env, token, slug, request, ct);
            if (existing is null)
            {
                await Gh(env, token, ["pr", "create", "--repo", slug, "--head", request.HeadBranch, "--base", request.BaseBranch,
                    "--title", request.Title, "--body-file", "-"], request.Body, ct);
                existing = await OpenAsync(env, token, slug, request, ct)
                    ?? throw new Failure("GitHub did not list the pull request after creating it.");
                created = true;
            }
            if (!string.Equals(existing.HeadRefOid, request.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new Failure($"The open pull request for {request.HeadBranch} points at a different commit; update or close it.");
            }
            var result = new PRFactoryPullRequestResult("pull-request-result", 1, existing.Number, existing.Url, request.HeadSha, created);
            await client.CompleteAsync(item.Id, item.LeaseToken, JsonSerializer.Serialize(result, PRFactoryWorkItemJson.Default.PRFactoryPullRequestResult),
                ct, request.HeadBranch, request.HeadSha);
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

    async Task<(string Owner, string Repo, string? User)> Authorize(PRFactoryPullRequestRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (repositories.All(r => r.Id != request.RepositoryId))
        {
            throw new Failure("This machine has no checkout mapped for the repository.");
        }
        var receipt = publications.VerifiedFor(server, request.SourceWorkItemId, request.RepositoryId).FirstOrDefault(p =>
            p.PublishBranch == request.HeadBranch && string.Equals(p.HeadSha, request.HeadSha, StringComparison.OrdinalIgnoreCase))
            ?? throw new Failure($"This machine did not publish {request.HeadBranch}@{request.HeadSha}.");
        return await Task.FromResult(ParseRemote(receipt.Remote));
    }

    internal static (string Owner, string Repo, string? User) ParseRemote(string remote)
    {
        var unsupported = new Failure("Only github.com remotes are supported for worker-opened pull requests.");
        string path;
        string? user = null;
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) { throw unsupported; }
            path = uri.AbsolutePath.Trim('/');
            // Only the username counts; any password or token in the URL is ignored.
            user = uri.UserInfo.Length == 0 ? null : uri.UserInfo.Split(':')[0];
            user = user is not null && IsGitHubLogin(user) ? user : null;
        }
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var ssh) && ssh.Scheme == "ssh")
        {
            if (!ssh.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) { throw unsupported; }
            path = ssh.AbsolutePath.Trim('/');
        }
        else if (remote.StartsWith("git@github.com:", StringComparison.Ordinal))
        {
            path = remote["git@github.com:".Length..].Trim('/');
        }
        else { throw unsupported; }
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) { path = path[..^4]; }
        var parts = path.Split('/');
        if (parts.Length != 2 || !RepoPart().IsMatch(parts[0]) || !RepoPart().IsMatch(parts[1])) { throw unsupported; }
        return (parts[0], parts[1], user);
    }

    // Always the named account; gh's active account is never consulted.
    async Task<string> TokenAsync(string login, CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["GH_TOKEN"] = null, ["GITHUB_TOKEN"] = null, ["GH_HOST"] = "github.com" };
        ProcessResult result;
        try { result = await runner(new ProcessSpec("gh", ["auth", "token", "--user", login], env), ct); }
        catch (System.ComponentModel.Win32Exception) { throw new Failure("gh is not installed or not on PATH on this machine."); }
        var token = result.Stdout.Trim();
        if (result.ExitCode != 0 || token.Length == 0)
        {
            throw new Failure($"gh has no login for {login}; run gh auth login on this machine.");
        }
        return token;
    }

    sealed record Listed(int Number, string Url, string HeadRefOid);

    async Task<Listed?> OpenAsync(IReadOnlyDictionary<string, string?> env, string token, string slug,
        PRFactoryPullRequestRequest request, CancellationToken ct)
    {
        var json = await Gh(env, token, ["pr", "list", "--repo", slug, "--head", request.HeadBranch, "--base", request.BaseBranch,
            "--state", "open", "--json", "number,url,headRefOid"], null, ct);
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var url = entry.GetProperty("url").GetString();
                if (url is not null && url.StartsWith("https://", StringComparison.Ordinal))
                {
                    return new(entry.GetProperty("number").GetInt32(), url, entry.GetProperty("headRefOid").GetString() ?? "");
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new Failure("GitHub returned an unreadable pull request list.");
        }
    }

    async Task<string> Gh(IReadOnlyDictionary<string, string?> env, string token, string[] args, string? stdin, CancellationToken ct)
    {
        ProcessResult result;
        try { result = await runner(new ProcessSpec("gh", args, env, stdin), ct); }
        catch (System.ComponentModel.Win32Exception) { throw new Failure("gh is not installed or not on PATH on this machine."); }
        if (result.ExitCode != 0)
        {
            var tail = Scrub(result.Stderr.Trim(), token);
            var first = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? $"gh exited {result.ExitCode}";
            throw new Failure($"GitHub refused: {first[..Math.Min(first.Length, 300)]}", tail[Math.Max(0, tail.Length - 2000)..]);
        }
        return result.Stdout;
    }

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
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return new(process.ExitCode, await output, await error);
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")] private static partial Regex LoginPattern();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")] private static partial Regex RepoPart();
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._/-]{0,199}$")] private static partial Regex BranchPattern();
    [GeneratedRegex("^[0-9a-fA-F]{40}$")] private static partial Regex Sha();
    [GeneratedRegex("(gh[opsu]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,})")] private static partial Regex TokenPattern();
}
