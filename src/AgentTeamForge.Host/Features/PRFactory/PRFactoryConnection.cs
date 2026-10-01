using AgentTeamForge.DAL.Files;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.DAL.Features.Jobs;
using AgentTeamForge.DAL.Sqlite;
using AgentTeamForge.DAL.Features.External;

namespace AgentTeamForge.Host.Features.PRFactory;

// Remote/BaseBranch pin the approved origin URL and base; when absent they are read from the checkout once and recorded.
public sealed record RepositoryMapping(Guid Id, string Directory, string[]? ExternalMembers = null,
    string? Remote = null, string? BaseBranch = null);
public sealed record PRFactorySettings(string Url, RepositoryMapping[] Repositories,
    bool TenantWideToken = false, bool RepoLess = true, string? CaFile = null, string? GitHubUser = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PRFactorySettings))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(PRFactoryServerLimit))]
internal sealed partial class PRFactorySettingsJson : JsonSerializerContext;

/// <summary>Owner-private, opt-in connector settings. The token is kept apart from printable settings.</summary>
public static class PRFactoryConnection
{
    const string SettingsName = "prfactory.json";
    const string TokenName = "prfactory.token";
    const string RejectedName = "prfactory.rejected";
    const string JoinsName = "prfactory-joins.json";
    const string LimitName = "prfactory-limit.json";

    public static int Run(StateDirectory state, string action, IReadOnlyDictionary<string, string> options, IReadOnlyList<string> args, TextReader tokenInput)
    {
        switch (action)
        {
            case "connect":
                return Connect(state, options, args, tokenInput);
            case "disconnect":
                File.Delete(Path.Combine(state.Path, SettingsName));
                File.Delete(Path.Combine(state.Path, TokenName));
                File.Delete(Path.Combine(state.Path, RejectedName));
                File.Delete(Path.Combine(state.Path, JoinsName));
                File.Delete(Path.Combine(state.Path, LimitName));
                Console.WriteLine("PRFactory disconnected.");
                return 0;
            case "release":
                return Release(state, args);
            case "status":
                var settings = LoadSettings(state);
                if (settings is null)
                {
                    Console.WriteLine("PRFactory disabled.");
                    return 0;
                }
                Console.WriteLine($"PRFactory enabled: {settings.Url}");
                Console.WriteLine($"Repo-less intake: {(settings.TenantWideToken && settings.RepoLess ? "enabled (declared tenant-wide token)" : "disabled")}");
                if (settings.CaFile is not null)
                {
                    Console.WriteLine($"Extra CA file: {settings.CaFile}");
                }
                if (settings.GitHubUser is not null)
                {
                    Console.WriteLine($"GitHub user for pull requests: {settings.GitHubUser}");
                }
                foreach (var repository in settings.Repositories)
                {
                    Console.WriteLine($"{repository.Id:D}={repository.Directory}");
                    if (repository.ExternalMembers is { Length: > 0 })
                    {
                        Console.WriteLine($"  external members: {string.Join(", ", repository.ExternalMembers)}");
                    }
                }
                var limitPath = Path.Combine(state.Path, LimitName);
                if (File.Exists(limitPath)
                    && JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(limitPath), PRFactorySettingsJson.Default.PRFactoryServerLimit) is { } limit)
                {
                    Console.WriteLine($"Server limit: {limit.Active}/{limit.Max} active work items"
                        + (limit.AtCap ? " (intake paused)" : "") + $", seen {limit.At:u}");
                }
                var joinsPath = Path.Combine(state.Path, JoinsName);
                if (File.Exists(joinsPath))
                {
                    var prompts = JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(joinsPath), PRFactorySettingsJson.Default.StringArray) ?? [];
                    foreach (var prompt in prompts)
                    {
                        Console.WriteLine(prompt);
                    }
                }
                if (File.Exists(Path.Combine(state.Path, RejectedName)))
                {
                    Console.WriteLine("Worker token rejected; reconnect with a valid token.");
                }
                return 0;
            default:
                Console.Error.WriteLine("usage: atf prfactory connect|disconnect|status|release [options]");
                return 64;
        }
    }

    /// <summary>Reads a line without echo; Ctrl+C aborts (null). Keys come from <paramref name="readKey"/> so tests can fake them.</summary>
    internal static string? ReadSecret(Func<ConsoleKeyInfo> readKey)
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = readKey();
            if (key.Key == ConsoleKey.Enter)
            {
                return buffer.ToString();
            }
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                return null;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }

    static int Connect(StateDirectory state, IReadOnlyDictionary<string, string> options, IReadOnlyList<string> args, TextReader tokenInput)
    {
        if (options.ContainsKey("token"))
        {
            // Command-line arguments are world-readable through ps and /proc.
            Console.Error.WriteLine("error: --token is not accepted; pipe the worker token on stdin");
            return 64;
        }
        if (!options.TryGetValue("url", out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/")
        {
            Console.Error.WriteLine("error: require an HTTPS --url");
            return 64;
        }
        string? caFile = null;
        if (options.TryGetValue("ca-file", out var caPath))
        {
            try
            {
                caFile = Path.GetFullPath(caPath);
                _ = PRFactoryTrust.LoadCa(caFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
            {
                Console.Error.WriteLine("error: --ca-file must be a readable PEM file with at least one certificate");
                return 64;
            }
        }
        string? gitHubUser = null;
        if (options.TryGetValue("github-user", out var login))
        {
            if (!PRFactoryPullRequests.IsGitHubLogin(login))
            {
                Console.Error.WriteLine("error: --github-user must be a GitHub login");
                return 64;
            }
            gitHubUser = login;
        }
        string? token;
        if (ReferenceEquals(tokenInput, Console.In) && !Console.IsInputRedirected)
        {
            Console.Error.Write("PRFactory worker token: ");
            token = ReadSecret(() => Console.ReadKey(intercept: true));
            Console.Error.WriteLine();
        }
        else
        {
            token = tokenInput.ReadLine();
        }
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Any(char.IsControl))
        {
            Console.Error.WriteLine("error: enter a nonempty worker token (prompt, or pipe it on stdin)");
            return 64;
        }

        var specs = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--repo" && i + 1 < args.Count)
            {
                specs.Add(args[++i]);
            }
        }
        var mappings = new List<RepositoryMapping>();
        foreach (var spec in specs)
        {
            var split = spec.IndexOf('=');
            if (split < 1 || !Guid.TryParse(spec[..split], out var id) || id == Guid.Empty || split == spec.Length - 1)
            {
                return InvalidMapping();
            }
            var path = Path.GetFullPath(spec[(split + 1)..]);
            if (!System.IO.Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null || mappings.Any(m => m.Id == id))
            {
                return InvalidMapping();
            }
            mappings.Add(new RepositoryMapping(id, path));
        }
        var tenantWide = options.GetValueOrDefault("token-scope") == "tenant-wide";
        var repoLess = options.GetValueOrDefault("repo-less") != "false";
        if (options.TryGetValue("token-scope", out var scope) && scope is not ("tenant-wide" or "repository")
            || options.TryGetValue("repo-less", out var enabled) && enabled is not ("true" or "false"))
        {
            Console.Error.WriteLine("error: --token-scope tenant-wide|repository and --repo-less true|false");
            return 64;
        }
        if (mappings.Count == 0 && !(tenantWide && repoLess))
        {
            return InvalidMapping();
        }

        var externalSpecs = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--external" && i + 1 < args.Count)
            {
                externalSpecs.Add(args[++i]);
            }
        }

        foreach (var spec in externalSpecs)
        {
            var split = spec.IndexOf(':');
            if (split < 1 || !Guid.TryParse(spec[..split], out var id) || split == spec.Length - 1
                || !System.Text.RegularExpressions.Regex.IsMatch(spec[(split + 1)..], "^[A-Za-z0-9_-]{1,64}$"))
            {
                return InvalidMapping();
            }

            var index = mappings.FindIndex(m => m.Id == id);
            if (index < 0 || mappings[index].ExternalMembers?.Contains(spec[(split + 1)..], StringComparer.Ordinal) == true)
            {
                return InvalidMapping();
            }

            mappings[index] = mappings[index] with { ExternalMembers = [.. mappings[index].ExternalMembers ?? [], spec[(split + 1)..]] };
        }

        var settings = new PRFactorySettings(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), [.. mappings], tenantWide, repoLess, caFile, gitHubUser);
        // Publish the token first; a daemon racing this write sees either the old
        // configuration or the new token, and never a partial private file.
        WritePrivate(Path.Combine(state.Path, TokenName), Encoding.UTF8.GetBytes(token));
        WritePrivate(Path.Combine(state.Path, SettingsName), JsonSerializer.SerializeToUtf8Bytes(settings, PRFactorySettingsJson.Default.PRFactorySettings));
        File.Delete(Path.Combine(state.Path, RejectedName));
        File.Delete(Path.Combine(state.Path, JoinsName));
        Console.WriteLine("PRFactory enabled. The daemon will register this machine and poll for work items.");
        return 0;
    }

    /// <summary>Forgets a fenced team (atomic in the database, safe while the daemon runs) so the next poll re-claims the item.</summary>
    static int Release(StateDirectory state, IReadOnlyList<string> args)
    {
        var raw = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && Guid.TryParse(a, out _));
        if (raw is null)
        {
            Console.Error.WriteLine("usage: atf prfactory release <work-item-id> [--state-dir DIR]");
            return 64;
        }
        var id = Guid.Parse(raw);
        var settings = LoadSettings(state);
        if (settings is null)
        {
            Console.Error.WriteLine("error: PRFactory is not connected");
            return 1;
        }
        if (!File.Exists(state.Database))
        {
            Console.Error.WriteLine("error: no ATF database in this state directory");
            return 1;
        }
        var result = new PRFactoryTeamStore(JobDatabase.Open(state.Database, TimeSpan.FromSeconds(10))).ReleaseFenced(settings.Url, id);
        switch (result)
        {
            case PRFactoryReleaseResult.Released:
                Console.WriteLine($"Released work item {id:D}: the fence and ownership rows are gone; local worktrees, artefacts, jobs and logs are kept.");
                Console.WriteLine($"Next: release the acceptance on the PRFactory side too (POST /api/work-items/{id:D}/atf-acceptance/release), "
                    + "then the next poll claims the item afresh. This command does not call PRFactory.");
                return 0;
            case PRFactoryReleaseResult.NotFound:
                Console.Error.WriteLine($"error: no ATF team for work item {id:D} on {settings.Url}");
                return 1;
            case PRFactoryReleaseResult.StillRunning:
                Console.Error.WriteLine($"error: work item {id:D} is fenced but its execution is still being stopped; retry in a moment");
                return 1;
            default:
                Console.Error.WriteLine($"error: work item {id:D} is not fenced (reconciliation needed); only a fenced team can be released");
                return 1;
        }
    }

    static int InvalidMapping()
    {
        Console.Error.WriteLine("error: require unique --repo <repository-guid>=<existing-local-directory> and optional --external <repository-guid>:<recipe-member>");
        return 64;
    }

    public static PRFactorySettings? LoadSettings(StateDirectory state)
    {
        var path = Path.Combine(state.Path, SettingsName);
        if (!File.Exists(path))
        {
            return null;
        }
        var settings = JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(path), PRFactorySettingsJson.Default.PRFactorySettings);
        if (settings is null || !Uri.TryCreate(settings.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.AbsolutePath != "/" || settings.Repositories is null
            || settings.Repositories.Length == 0 && !(settings.TenantWideToken && settings.RepoLess)
            || settings.Repositories.Any(r => r.Id == Guid.Empty || !System.IO.Directory.Exists(r.Directory)
                || r.ExternalMembers is { } names && names.Any(n => !System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z0-9_-]{1,64}$"))))
        {
            throw new StateDirectoryException("prfactory_settings_invalid");
        }
        return settings;
    }

    public static string ReadToken(StateDirectory state) => Encoding.UTF8.GetString(StateDirectory.ReadPrivateFile(Path.Combine(state.Path, TokenName)));

    public static void MarkRejected(StateDirectory state) => WritePrivate(Path.Combine(state.Path, RejectedName), "rejected"u8.ToArray());

    /// <summary>Persist the server's advertised cap for `atf prfactory status`; unchanged values are not rewritten.</summary>
    public static void PublishLimit(StateDirectory state, PRFactoryServerLimit? limit)
    {
        var path = Path.Combine(state.Path, LimitName);
        if (limit is null)
        {
            File.Delete(path);
            return;
        }
        if (File.Exists(path)
            && JsonSerializer.Deserialize(StateDirectory.ReadPrivateFile(path), PRFactorySettingsJson.Default.PRFactoryServerLimit) is { } last
            && last.Max == limit.Max && last.Active == limit.Active)
        {
            return;
        }
        WritePrivate(path, JsonSerializer.SerializeToUtf8Bytes(limit, PRFactorySettingsJson.Default.PRFactoryServerLimit));
    }

    public static bool IsRejected(StateDirectory state) => File.Exists(Path.Combine(state.Path, RejectedName));

    /// <summary>Publish a private, read-only CLI snapshot after a daemon connector tick.</summary>
    public static void PublishJoinTickets(StateDirectory state, PRFactoryTeamStore teams, string server)
    {
        var prompts = teams.Pending(server)
            .Where(team => team.AcceptanceState != "reconciliation_needed") // A fenced team takes no new joiners.
            .SelectMany(team => teams.ExternalMembers(server, team.WorkItemId)
                .Where(member => !member.Closed && member.TicketExpires > DateTimeOffset.UtcNow)
                .Select(member => $"Work item {team.WorkItemId:D}, external member {member.Member}: "
                    + new JoinTicket(member.TeamId, member.ActualName, member.TicketToken, member.TicketExpires).JoinPrompt))
            .Concat(teams.ReconciliationNeeded(server)
                .Select(team => $"Work item {team.WorkItemId:D}: reconciliation needed; local results retained, remote publication fenced. Release with: atf prfactory release {team.WorkItemId:D}"))
            .ToArray();
        WritePrivate(Path.Combine(state.Path, JoinsName), JsonSerializer.SerializeToUtf8Bytes(prompts, PRFactorySettingsJson.Default.StringArray));
    }

    public static void WritePrivate(string path, byte[] content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, PrivateFiles.Options(FileMode.CreateNew, FileAccess.Write)))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
