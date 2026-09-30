using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>A Herdr launch or control step failed; nothing outside proven ownership was cleaned up.</summary>
public sealed class HerdrLaunchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Interactive mode has no visible provider here. There is never a headless fallback.</summary>
public sealed class InteractiveTerminalUnavailableException(string message) : Exception(message);

public sealed record HerdrTerminalOptions
{
    /// <summary>Seed for every Herdr invocation; filtered by the launch allowlist, never inherited implicitly.</summary>
    public required IReadOnlyDictionary<string, string?> Environment { get; init; }

    /// <summary>Comma-separated extra names; agent-session and Herdr caller context stay excluded.</summary>
    public string? ExtraAllowedEnvironment { get; init; }

    public string SessionPrefix { get; init; } = "atf-";

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaxStdoutBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxStderrBytes { get; init; } = 64 * 1024;

    /// <summary>Fallback source for a missing graphical session (the user manager's environment).</summary>
    public Func<IReadOnlyDictionary<string, string>>? SessionEnvironment { get; init; }

    internal Func<string>? NewSessionName { get; init; }
}

/// <summary>Retained handle of a daemon-created Herdr session: the facts teardown and rebinding must prove.</summary>
public sealed record OwnedHerdrSession(string SessionName, string SocketPath, int ServerPid, ulong ServerStartTicks, string OwnerLabel, string WorkspaceId)
{
    public string? JobId { get; init; }
    public string? AgentName { get; init; }
    public bool Shared { get; init; }
    public string? TabId { get; init; }
    public string? PaneId { get; init; }
    public string? TerminalId { get; init; }
    public string? TabLabel { get; init; }
    public int? ShellPid { get; init; }
    public ulong? ShellStartTicks { get; init; }
}

/// <summary>One agent tab; valid only while the same server, pane terminal and shell process still host it.</summary>
public sealed record HerdrTabBinding(OwnedHerdrSession Session, string TabId, string PaneId, string TerminalId, int ShellPid, ulong ShellStartTicks);

/// <summary>
/// Unix interactive terminal provider: binds to an existing session by default and opens one visible
/// tab per agent. Every CLI call is bounded in
/// time and output. Failures never stop, delete or close anything whose ownership is unproven.
/// Starting the agent TUI inside the tab belongs to the backend slices.
/// </summary>
public sealed class HerdrTerminal
{
    /// <summary>Carries only the path of the private bootstrap file into the pane, never its content.</summary>
    public const string BootstrapVariable = "ATF_BOOTSTRAP_FILE";

    HerdrTerminalOptions _options;
    readonly IHerdrProcessRunner _runner;

    public HerdrTerminal(HerdrTerminalOptions options)
        : this(options, new HerdrProcessRunner())
    {
    }

    internal HerdrTerminal(HerdrTerminalOptions options, IHerdrProcessRunner runner)
    {
        if (options.SessionPrefix.Length == 0 || !options.SessionPrefix.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("session prefix must be lowercase letters, digits or '-'", nameof(options));
        }
        var environment = new Dictionary<string, string?>(options.Environment, StringComparer.Ordinal);
        var codexHome = CodexPaths.LaunchHome(name => options.Environment.GetValueOrDefault(name), Environment.CurrentDirectory);
        if (codexHome is null) { environment.Remove("CODEX_HOME"); }
        else { environment["CODEX_HOME"] = codexHome; }
        _options = options with { Environment = environment };
        _runner = runner;
    }

    public async Task<OwnedHerdrSession> StartSessionAsync(CancellationToken cancellationToken)
    {
        await RequireVisibleProviderAsync(cancellationToken);
        var name = _options.NewSessionName?.Invoke() ?? HerdrOwnership.NewSessionName(_options.SessionPrefix);
        var label = HerdrOwnership.NewOwnerLabel();

        ProcessStartInfo server;
        try
        {
            server = HerdrCommands.OwnedServerStartInfo(await GlobalAsync(cancellationToken, "session", "list", "--json"), name, _options.Environment, _options.ExtraAllowedEnvironment);
        }
        catch (InvalidOperationException e)
        {
            throw new HerdrLaunchException(e.Message, e);
        }

        try
        {
            await _runner.StartDetachedAsync(server, _options.CommandTimeout, cancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException)
        {
            throw new HerdrLaunchException($"herdr server launch for {name} failed; nothing was stopped or deleted", e);
        }

        var (socket, identity) = await WaitForServerAsync(name, cancellationToken);
        var created = await OwnedAsync(socket, cancellationToken, "workspace", "create", "--cwd", Home(), "--label", label, "--no-focus");
        return new(name, socket, identity.Pid, identity.StartTicks, label, Str(created, "result", "workspace", "workspace_id"));
    }

    async Task<(string Socket, ProcessIdentity Identity)> WaitForServerAsync(string name, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var listed = HerdrOwnership.Find(await GlobalAsync(cancellationToken, "session", "list", "--json"), name);
            if (listed?["running"] is JsonValue r && r.TryGetValue<bool>(out var running) && running &&
                listed["socket_path"] is JsonValue s && s.TryGetValue<string>(out var socket) && Path.IsPathRooted(socket) &&
                _runner.FindServers(name, socket) is [var identity])
            {
                return (socket, identity);
            }
            if (clock.Elapsed >= _options.StartupTimeout)
            {
                throw new HerdrLaunchException($"herdr session {name} did not start as one identifiable server within {_options.StartupTimeout}; " +
                    "nothing was stopped or deleted");
            }
            await Task.Delay(_options.PollInterval, cancellationToken);
        }
    }

    /// <summary>Bind to a running session without acquiring ownership of its session or workspace.</summary>
    public async Task<OwnedHerdrSession> ExistingSessionAsync(string name, CancellationToken cancellationToken)
    {
        await RequireVisibleProviderAsync(cancellationToken);
        var listed = HerdrOwnership.Find(await GlobalAsync(cancellationToken, "session", "list", "--json"), name);
        if (listed?["running"] is not JsonValue running || !running.TryGetValue<bool>(out var isRunning) || !isRunning
            || listed["socket_path"] is not JsonValue socketValue || !socketValue.TryGetValue<string>(out var socket)
            || !Path.IsPathRooted(socket) || _runner.FindServers(name, socket) is not [var server])
        {
            throw new HerdrLaunchException($"Herdr session '{name}' is not running as one identifiable server");
        }
        return new(name, socket, server.Pid, server.StartTicks, "", "") { Shared = true };
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> StartGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Bind to the named shared session, starting its server first when Herdr lists it as stopped with no server.
    /// Nothing is created inside it and no ownership is recorded: it stays the owner's session. An absent name,
    /// an ambiguous server set or a half-alive session fails instead of starting a second server.
    /// </summary>
    public async Task<OwnedHerdrSession> SharedSessionAsync(string name, CancellationToken cancellationToken)
    {
        await RequireVisibleProviderAsync(cancellationToken);
        // Inspect launch state only under the per-name gate: while another submit is starting a stopped
        // server, the session can list as not running with one server, which is not a half-alive session.
        var gate = StartGates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await SharedStateAsync(name, cancellationToken) is not SharedState.Stopped)
            {
                return await ExistingSessionAsync(name, cancellationToken);
            }
            try
            {
                await _runner.StartDetachedAsync(HerdrCommands.SharedServerStartInfo(name, _options.Environment, _options.ExtraAllowedEnvironment, Home()),
                    _options.CommandTimeout, cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException)
            {
                throw new HerdrLaunchException($"herdr server launch for stopped session '{name}' failed; nothing was stopped or deleted", e);
            }
            var (socket, server) = await WaitForServerAsync(name, cancellationToken);
            return new(name, socket, server.Pid, server.StartTicks, "", "") { Shared = true };
        }
        finally { gate.Release(); }
    }

    enum SharedState { Running, Stopped }

    internal enum BindingState { Intact, Unverified, Gone }

    /// <summary>Running and identifiable, or stopped and startable; every other shape throws.</summary>
    async Task<SharedState> SharedStateAsync(string name, CancellationToken cancellationToken)
    {
        var listed = HerdrOwnership.Find(await GlobalAsync(cancellationToken, "session", "list", "--json"), name)
            ?? throw new HerdrLaunchException($"Herdr session '{name}' does not exist");
        var servers = _runner.FindServers(name, SocketOf(listed));
        if (servers.Count > 1)
        {
            throw new HerdrLaunchException($"Herdr session '{name}' has {servers.Count} servers claiming it; stop the extra server(s) - ATF will not choose one");
        }
        var isRunning = listed["running"] is JsonValue running && running.TryGetValue<bool>(out var value) && value;
        return !isRunning && servers.Count == 0 ? SharedState.Stopped : SharedState.Running;
    }

    static string? SocketOf(JsonNode listed) =>
        listed["socket_path"] is JsonValue s && s.TryGetValue<string>(out var socket) && Path.IsPathRooted(socket) ? socket : null;

    /// <summary>Admission check: null when the session is running as one server or is stopped and will be started at launch.</summary>
    public string? CheckSharedSession(string name)
    {
        // A start in progress holds the gate; its half-started server is not an error, and the launch re-checks under the gate.
        if (StartGates.TryGetValue(name, out var gate) && gate.CurrentCount == 0) { return null; }
        try
        {
            RequireVisibleProviderAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (SharedStateAsync(name, CancellationToken.None).GetAwaiter().GetResult() is SharedState.Stopped) { return null; }
            _ = ExistingSessionAsync(name, CancellationToken.None).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception e) when (e is HerdrLaunchException or InteractiveTerminalUnavailableException)
        { return e.Message; }
    }

    /// <summary>
    /// Opens one visible, unfocused tab in the owned workspace and proves that its shell is a child of
    /// the recorded server carrying exactly this bootstrap path. The tab is never closed on failure.
    /// </summary>
    public async Task<HerdrTabBinding> OpenAgentTabAsync(OwnedHerdrSession session, string label, string cwd, string bootstrapFile, CancellationToken cancellationToken,
        (string Name, string Value)? workspaceTrustEnvironment = null, Action<OwnedHerdrSession>? onCreated = null, bool exclusivePiMcp = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentException.ThrowIfNullOrEmpty(cwd);
        if (!Path.IsPathRooted(bootstrapFile) || bootstrapFile.Any(c => c is '\0' or '\n' or '\r'))
        {
            throw new ArgumentException("bootstrap file must be an absolute single-line path", nameof(bootstrapFile));
        }
        if (await OwnershipProblemAsync(session, cancellationToken) is { } problem)
        {
            throw new HerdrLaunchException("refusing to open a tab: " + problem);
        }

        var args = new List<string> { "--cwd", cwd, "--label", label,
            "--env", BootstrapVariable + "=" + bootstrapFile, "--no-focus" };
        // Shared Herdr sessions may have been started with another CODEX_HOME.
        if (Env("CODEX_HOME") is { } codexHome) { args.AddRange(["--env", "CODEX_HOME=" + codexHome]); }
        if (exclusivePiMcp) { args.AddRange(["--env", "PI_MCP_CONFIG_MODE=exclusive"]); }
        if (workspaceTrustEnvironment is { } trust)
        {
            // Claude's per-process trust latch. Inject it only into this owned launch;
            // Inherited session identity remains excluded by LaunchEnvironment.
            // In Claude Code 2.1.x it only marks the workspace trusted without writing
            // ~/.claude.json (so project settings' permission rules apply); it does not
            // enable a sandbox or touch telemetry. The bypass warning is skipped via --settings.
            args.AddRange(["--env", trust.Name + "=" + trust.Value]);
        }
        JsonNode created;
        if (session.Shared)
        {
            (session, created) = await CreateSharedTabAsync(session, cwd, label, args, cancellationToken);
        }
        else
        {
            created = await OwnedAsync(session.SocketPath, cancellationToken, ["tab", "create", "--workspace", session.WorkspaceId, .. args]);
        }
        var tab = Str(created, "result", "root_pane", "tab_id");
        var pane = Str(created, "result", "root_pane", "pane_id");
        var terminal = Str(created, "result", "root_pane", "terminal_id");
        onCreated?.Invoke(session with { TabId = tab, PaneId = pane, TerminalId = terminal, TabLabel = label });

        var clock = Stopwatch.StartNew();
        while (true)
        {
            var info = await OwnedAsync(session.SocketPath, cancellationToken, "pane", "process-info", "--pane", pane);
            if (info["result"]?["process_info"]?["shell_pid"] is JsonValue v && v.TryGetValue<int>(out var shellPid) && _runner.Identity(shellPid) is { } shell)
            {
                if (_runner.ParentOf(shellPid) != session.ServerPid || _runner.Identity(session.ServerPid)?.StartTicks != session.ServerStartTicks)
                {
                    throw new HerdrLaunchException($"bootstrap proof failed for tab {tab}: its shell is not a child of the recorded server; the tab is left open");
                }
                if (_runner.EnvironmentValue(shellPid, BootstrapVariable) != bootstrapFile)
                {
                    throw new HerdrLaunchException($"bootstrap proof failed for tab {tab}: its shell does not carry this launch's bootstrap; the tab is left open");
                }
                return new(session, tab, pane, terminal, shell.Pid, shell.StartTicks);
            }
            if (clock.Elapsed >= _options.StartupTimeout)
            {
                throw new HerdrLaunchException($"bootstrap proof for tab {tab} timed out; the tab is left open");
            }
            await Task.Delay(_options.PollInterval, cancellationToken);
        }
    }

    async Task<(OwnedHerdrSession Session, JsonNode Created)> CreateSharedTabAsync(OwnedHerdrSession session, string cwd, string tabLabel,
        List<string> args, CancellationToken cancellationToken)
    {
        var label = WorkspaceLabel(cwd);
        // Use the reference lock name and flock so ATF and win-agent-teams serialize workspace creation.
        var config = Env("HERDR_CONFIG_PATH");
        var root = !string.IsNullOrEmpty(config) ? Path.GetDirectoryName(Path.GetFullPath(config))! : Path.Combine(Home(), ".config", "herdr");
        Directory.CreateDirectory(root);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(label)))[..16];
        var lockPath = Path.Combine(root, $"win-agent-teams-{session.SessionName}.ws-{digest}.lock");
        // O_RDWR | O_CREAT | O_CLOEXEC: herdr children spawned while the lock is held must not inherit it.
        var descriptor = OpenLockFile(lockPath, 2 | (OperatingSystem.IsMacOS() ? 0x200 | 0x1000000 : 0x40 | 0x80000), 0x180);
        if (descriptor < 0) { throw new HerdrLaunchException($"could not open Herdr workspace lock: errno {Marshal.GetLastPInvokeError()}"); }
        using var guard = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var lockWait = Stopwatch.StartNew();
        while (Flock(descriptor, 2 | 4) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != (OperatingSystem.IsMacOS() ? 35 : 11) || lockWait.Elapsed >= _options.CommandTimeout)
            {
                throw new HerdrLaunchException($"could not lock Herdr workspace {label}: errno {error}");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken);
        }
        try
        {
            var workspace = ResolveWorkspace(await OwnedAsync(session.SocketPath, cancellationToken, "workspace", "list"), label);
            if (workspace is not null)
            {
                var tab = await OwnedAsync(session.SocketPath, cancellationToken, ["tab", "create", "--workspace", workspace, .. args]);
                return (session with { WorkspaceId = workspace }, tab);
            }
            var createArgs = new List<string>(args);
            createArgs[createArgs.IndexOf("--label") + 1] = label;
            var created = await OwnedAsync(session.SocketPath, cancellationToken, ["workspace", "create", .. createArgs]);
            var id = Str(created, "result", "workspace", "workspace_id");
            // A newly created workspace's root tab is this agent's tab.
            var tabId = Text(created["result"]?["tab"]?["tab_id"]);
            if (tabId is not null) { await OwnedAsync(session.SocketPath, cancellationToken, "tab", "rename", tabId, tabLabel); }
            return (session with { WorkspaceId = id }, created);
        }
        finally { _ = Flock(descriptor, 8); }
    }

    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern int OpenLockFile(string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    static extern int Flock(int descriptor, int operation);

    internal static string? ResolveWorkspace(JsonNode listing, string label)
    {
        if (listing["result"]?["workspaces"] is not JsonArray workspaces)
        {
            throw new HerdrLaunchException("Herdr workspace listing is unreadable");
        }
        var matches = new List<(int Number, string Id)>();
        foreach (var node in workspaces)
        {
            if (node is not JsonObject workspace || Text(workspace["label"]) is not { } listedLabel)
            {
                throw new HerdrLaunchException("Herdr workspace listing is unreadable");
            }
            if (listedLabel != label) { continue; }
            var id = Text(workspace["workspace_id"]);
            if (string.IsNullOrEmpty(id)) { throw new HerdrLaunchException("Herdr workspace listing is unreadable"); }
            var number = workspace["number"] is JsonValue value && value.TryGetValue<int>(out var n) ? n : int.MaxValue;
            matches.Add((number, id));
        }
        return matches.OrderBy(m => m.Number).ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault().Id;
    }

    internal static string WorkspaceLabel(string cwd)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd));
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-C", cwd, "rev-parse", "--path-format=absolute", "--git-common-dir" }) { psi.ArgumentList.Add(arg); }
            using var git = Process.Start(psi);
            if (git is not null && !git.WaitForExit(3000)) { git.Kill(); }
            else if (git is not null && git.ExitCode == 0)
            {
                var common = git.StandardOutput.ReadToEnd().Trim();
                var directory = Path.GetFileName(Path.TrimEndingDirectorySeparator(common));
                name = directory == ".git" ? Path.GetFileName(Path.GetDirectoryName(common)) ?? name
                    : directory.EndsWith(".git", StringComparison.Ordinal) ? directory[..^4] : directory;
            }
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException) { }
        var clean = new string([.. name.Where(c => !char.IsControl(c))]).Trim().TrimStart('-').Trim();
        return clean.Length == 0 ? "agents" : clean[..Math.Min(clean.Length, 128)];
    }

    /// <summary>Null when the binding still holds; otherwise why it is invalid (replaced, closed, restarted or unreadable).</summary>
    public async Task<string?> VerifyBindingAsync(HerdrTabBinding binding, CancellationToken cancellationToken) =>
        (await CheckBindingAsync(binding, cancellationToken)).Problem;

    /// <summary>
    /// Gone only with proof (recorded server or shell process gone or replaced, pane not found, terminal or tab
    /// mismatch); a probe that merely failed while the recorded identities are intact is Unverified.
    /// </summary>
    internal async Task<(BindingState State, string? Problem)> CheckBindingAsync(HerdrTabBinding binding, CancellationToken cancellationToken)
    {
        // Independent process proof first: a failed probe below must never mask a dead or replaced server or shell.
        if (ServerIdentityLost(binding.Session))
        {
            return (BindingState.Gone, ServerProblem(binding.Session) ?? "the recorded herdr server exited or was replaced");
        }
        if (ProcessReplaced(binding.ShellPid, binding.ShellStartTicks))
        {
            return (BindingState.Gone, "the bound pane shell process was replaced or exited");
        }
        if (ServerProblem(binding.Session) is { } server) { return (BindingState.Unverified, server); }
        JsonNode pane;
        try
        {
            pane = await OwnedAsync(binding.Session.SocketPath, cancellationToken, "pane", "get", binding.PaneId);
        }
        catch (HerdrLaunchException e)
        {
            var notFound = e.Message.Contains("not_found", StringComparison.Ordinal);
            return (notFound ? BindingState.Gone : BindingState.Unverified, $"pane {binding.PaneId} cannot be read: {e.Message}");
        }
        var facts = pane["result"]?["pane"];
        if (Text(facts?["terminal_id"]) != binding.TerminalId || Text(facts?["tab_id"]) != binding.TabId)
        {
            return (BindingState.Gone, $"pane {binding.PaneId} no longer hosts the bound terminal");
        }
        if (_runner.Identity(binding.ShellPid)?.StartTicks == binding.ShellStartTicks) { return (BindingState.Intact, null); }
        return (BindingState.Unverified, "the bound pane shell identity cannot be read");
    }

    bool ServerIdentityLost(OwnedHerdrSession session) => ProcessReplaced(session.ServerPid, session.ServerStartTicks);

    bool ProcessReplaced(int pid, ulong startTicks) =>
        _runner.Identity(pid) is { } identity ? identity.StartTicks != startTicks : !PidMayBeAlive(pid);

    /// <summary>Rebuild a binding only from the same server, pane, terminal and shell identity.</summary>
    internal async Task<HerdrTabBinding?> RebindAsync(OwnedHerdrSession session, string bootstrapFile, CancellationToken cancellationToken)
    {
        if (session.TabId is null || session.PaneId is null || session.TerminalId is null
            || session.ShellPid is null || session.ShellStartTicks is null
            || await OwnershipProblemAsync(session, cancellationToken) is not null)
        {
            return null;
        }
        var binding = new HerdrTabBinding(session, session.TabId, session.PaneId, session.TerminalId,
            session.ShellPid.Value, session.ShellStartTicks.Value);
        if (await VerifyBindingAsync(binding, cancellationToken) is not null
            || _runner.ParentOf(binding.ShellPid) != session.ServerPid
            || _runner.EnvironmentValue(binding.ShellPid, BootstrapVariable) != bootstrapFile)
        {
            return null;
        }
        return binding;
    }

    /// <summary>True only with process proof that the recorded server or pane shell no longer exists.</summary>
    internal bool OwnedPaneIsGone(OwnedHerdrSession session) =>
        ServerIdentityLost(session)
        || session.ShellPid is int shell && session.ShellStartTicks is ulong ticks && ProcessReplaced(shell, ticks);

    internal bool HasUnverifiedLiveIdentity(HerdrTabBinding binding)
    {
        // Proof that either recorded process is gone or replaced wins over an unreadable identity of the other.
        if (ServerIdentityLost(binding.Session) || ProcessReplaced(binding.ShellPid, binding.ShellStartTicks)) { return false; }
        return _runner.Identity(binding.Session.ServerPid) is null || _runner.Identity(binding.ShellPid) is null;
    }

    static bool PidMayBeAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (Win32Exception) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>Stops and deletes the session only after proving ownership; refuses otherwise.</summary>
    public async Task StopOwnedSessionAsync(OwnedHerdrSession session, CancellationToken cancellationToken)
    {
        if (session.Shared)
        {
            await StopSharedTabAsync(session, cancellationToken);
            return;
        }
        var sessions = await GlobalAsync(cancellationToken, "session", "list", "--json");
        var identityMatches = ServerProblem(session) is null;
        var workspaces = identityMatches ? await OwnedAsync(session.SocketPath, cancellationToken, "workspace", "list") : null;
        var decision = HerdrOwnership.Teardown(session.OwnerLabel, session.SessionName, sessions, identityMatches, workspaces);
        if (decision.Problem is { } problem)
        {
            throw new HerdrLaunchException("teardown refused: " + problem);
        }
        if (decision.Stop)
        {
            await GlobalAsync(cancellationToken, "session", "stop", session.SessionName, "--json");
            await GlobalAsync(cancellationToken, "session", "delete", session.SessionName, "--json");
        }
    }

    /// <summary>Explicit cleanup: a recorded session already absent or stopped needs no teardown.</summary>
    public async Task RecoverOwnedSessionAsync(OwnedHerdrSession session, CancellationToken cancellationToken)
    {
        var listed = HerdrOwnership.Find(await GlobalAsync(cancellationToken, "session", "list", "--json"), session.SessionName);
        if (listed?["running"] is not JsonValue running || !running.TryGetValue<bool>(out var isRunning) || !isRunning)
        {
            return;
        }
        await StopOwnedSessionAsync(session, cancellationToken);
    }

    /// <summary>
    /// Herdr resumes agents in restored panes as bare CLIs. Closes a recorded root pane only when its server is gone
    /// and the live session lists an agent with the recorded name on the recorded pane and tab.
    /// </summary>
    public async Task<int> CloseRestoredPanesAsync(string sessionName, IReadOnlyList<OwnedHerdrSession> records, CancellationToken cancellationToken, Action<string>? log = null)
    {
        var candidates = records.Where(r => r.SessionName == sessionName && r.Shared && HerdrOwnedSessions.ValidAgentName(r.AgentName)
            && r.PaneId is not null && r.TabId is not null
            && _runner.Identity(r.ServerPid)?.StartTicks != r.ServerStartTicks).ToList();
        if (candidates.Count == 0) { return 0; }
        OwnedHerdrSession live;
        try { live = await ExistingSessionAsync(sessionName, cancellationToken); }
        catch (Exception e) when (e is HerdrLaunchException or InteractiveTerminalUnavailableException) { return 0; }
        var listed = await RunOwnedAsync(live, cancellationToken, "agent", "list");
        if (listed["result"]?["agents"] is not JsonArray agents) { return 0; }
        var closed = 0;
        foreach (var record in candidates)
        {
            var proven = agents.Any(a => a is JsonObject && Text(a["name"]) is { } name && name == record.AgentName
                && Text(a["pane_id"]) == record.PaneId && Text(a["tab_id"]) == record.TabId);
            if (!proven) { continue; }
            await RunOwnedAsync(live, cancellationToken, "pane", "close", record.PaneId!);
            closed++;
            log?.Invoke($"recovery: closed restored Herdr pane {record.PaneId} ({record.AgentName}, job {record.JobId}) in {sessionName}");
        }
        return closed;
    }

    async Task StopSharedTabAsync(OwnedHerdrSession session, CancellationToken cancellationToken)
    {
        if (session.TabId is null || session.PaneId is null || session.TerminalId is null)
        {
            throw new HerdrLaunchException("shared tab ownership record is incomplete");
        }
        if (ServerProblem(session) is { } problem)
        {
            // Recorded server gone (restart/reboot): its tab ids are stale, so only a restored pane proven by
            // its recorded agent name may be closed. A live server with a changed identity is refused.
            if (_runner.Identity(session.ServerPid)?.StartTicks != session.ServerStartTicks)
            {
                try { await CloseRestoredPanesAsync(session.SessionName, [session], cancellationToken); }
                catch (Exception e) when (e is HerdrLaunchException or InteractiveTerminalUnavailableException or OperationCanceledException) { }
                return;
            }
            throw new HerdrLaunchException("teardown refused: " + problem);
        }
        JsonNode tab;
        JsonNode pane;
        try
        {
            tab = await OwnedAsync(session.SocketPath, cancellationToken, "tab", "get", session.TabId);
            pane = await OwnedAsync(session.SocketPath, cancellationToken, "pane", "get", session.PaneId);
        }
        catch (HerdrLaunchException e) when (e.Message.Contains("not_found", StringComparison.Ordinal)) { return; }
        var tabFacts = tab["result"]?["tab"];
        var paneFacts = pane["result"]?["pane"];
        if (Text(tabFacts?["tab_id"]) != session.TabId || Text(tabFacts?["workspace_id"]) != session.WorkspaceId
            || Text(paneFacts?["pane_id"]) != session.PaneId || Text(paneFacts?["tab_id"]) != session.TabId
            || Text(paneFacts?["terminal_id"]) != session.TerminalId)
        {
            throw new HerdrLaunchException("teardown refused: recorded shared tab or pane identity changed");
        }
        // Closing a tab could also close panes another client added later. Target only our recorded root pane.
        await OwnedAsync(session.SocketPath, cancellationToken, "pane", "close", session.PaneId);
    }

    /// <summary>A raw command against the owned server, after re-proving its identity.</summary>
    internal async Task<JsonNode> RunOwnedAsync(OwnedHerdrSession session, CancellationToken cancellationToken, params string[] args) =>
        await RunOwnedAsync(session, null, cancellationToken, args);

    internal async Task<JsonNode> RunOwnedAsync(OwnedHerdrSession session, TimeSpan? commandTimeout, CancellationToken cancellationToken, params string[] args) =>
        ServerProblem(session) is { } problem
            ? throw new HerdrLaunchException(problem)
            : await RunAsync(session.SocketPath, args, cancellationToken, commandTimeout: commandTimeout);

    internal async Task<string> ReadAgentAsync(OwnedHerdrSession session, string pane, CancellationToken cancellationToken) =>
        ServerProblem(session) is { } problem
            ? throw new HerdrLaunchException(problem)
            : (await RunAsync(session.SocketPath, ["agent", "read", pane, "--source", "detection", "--lines", "100"], cancellationToken, rawText: true)).GetValue<string>();

    bool NoDisplay() => !OperatingSystem.IsMacOS() && string.IsNullOrEmpty(Env("WAYLAND_DISPLAY")) && string.IsNullOrEmpty(Env("DISPLAY"));

    async Task RequireVisibleProviderAsync(CancellationToken cancellationToken)
    {
        if (NoDisplay() && _options.SessionEnvironment?.Invoke() is { } session)
        {
            var merged = new Dictionary<string, string?>(_options.Environment, StringComparer.Ordinal);
            foreach (var (key, value) in session)
            {
                if (string.IsNullOrEmpty(Env(key))) { merged[key] = value; }
            }
            _options = _options with { Environment = merged };
        }
        if (NoDisplay())
        {
            throw new InteractiveTerminalUnavailableException("interactive Herdr launch needs a graphical desktop session (WAYLAND_DISPLAY or DISPLAY); " +
                "headless execution is available only as an explicit launch-mode choice");
        }
        try
        {
            await GlobalAsync(cancellationToken, "--version");
        }
        catch (HerdrLaunchException e)
        {
            throw new InteractiveTerminalUnavailableException("herdr is not usable: " + e.Message + "; headless execution is available only as an explicit launch-mode choice");
        }
    }

    async Task<string?> OwnershipProblemAsync(OwnedHerdrSession session, CancellationToken cancellationToken) =>
        ServerProblem(session) ?? (session.Shared || HerdrOwnership.HasLabel(await OwnedAsync(session.SocketPath, cancellationToken, "workspace", "list"), session.OwnerLabel)
            ? null
            : "owner label workspace missing from the running server");

    // A bound session only needs its recorded server to be among the name matches; uniqueness is for adoption.
    string? ServerProblem(OwnedHerdrSession session) =>
        _runner.FindServers(session.SessionName, session.SocketPath).Any(live => live.Pid == session.ServerPid && live.StartTicks == session.ServerStartTicks)
            ? null
            : $"herdr server for {session.SessionName} is not the recorded process (PID + start time)";

    Task<JsonNode> GlobalAsync(CancellationToken cancellationToken, params string[] args) => RunAsync(null, args, cancellationToken);

    Task<JsonNode> OwnedAsync(string socketPath, CancellationToken cancellationToken, params string[] args) => RunAsync(socketPath, args, cancellationToken);

    async Task<JsonNode> RunAsync(string? socketPath, string[] args, CancellationToken cancellationToken, bool rawText = false, TimeSpan? commandTimeout = null)
    {
        var timeout = commandTimeout ?? _options.CommandTimeout;
        var what = "herdr " + string.Join(' ', args.Take(2));
        var psi = HerdrCommands.CommandStartInfo(_options.Environment, _options.ExtraAllowedEnvironment, socketPath, args);
        CapturedProcess r;
        try
        {
            r = await _runner.CaptureAsync(psi, timeout, _options.MaxStdoutBytes, _options.MaxStderrBytes, cancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException)
        {
            throw new HerdrLaunchException($"could not start {what}: {e.Message}", e);
        }
        if (r.TimedOut)
        {
            throw new HerdrLaunchException($"{what} timed out after {timeout}");
        }
        if (r.StdoutTruncated)
        {
            throw new HerdrLaunchException($"{what} output exceeded {_options.MaxStdoutBytes} bytes");
        }
        if (r.ExitCode != 0)
        {
            throw new HerdrLaunchException($"{what} exited {r.ExitCode}: {ErrorCode(r)}");
        }
        if (args is ["--version"])
        {
            return new JsonObject();
        }
        if (rawText) { return JsonValue.Create(r.Stdout); }
        try
        {
            return r.Stdout.Trim() is { Length: > 0 } text ? JsonNode.Parse(text) ?? new JsonObject() : new JsonObject();
        }
        catch (JsonException e)
        {
            throw new HerdrLaunchException($"{what} returned malformed JSON", e);
        }
    }

    static string ErrorCode(CapturedProcess r)
    {
        try
        {
            if (JsonNode.Parse(r.Stdout)?["error"]?["code"] is JsonValue code && code.TryGetValue<string>(out var text))
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }
        var err = r.Stderr.Trim();
        return err.Length > 300 ? err[..300] : err;
    }

    /// <summary>A variable of the environment Herdr panes are launched with.</summary>
    internal string? Env(string name) => _options.Environment.TryGetValue(name, out var v) ? v : null;

    string Home() => Env("HOME") is { Length: > 0 } home && Path.IsPathRooted(home) ? home : "/";

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string Str(JsonNode? node, params string[] path)
    {
        foreach (var p in path)
        {
            node = node?[p];
        }
        return Text(node) ?? throw new HerdrLaunchException($"missing {string.Join('.', path)} in herdr response");
    }
}
