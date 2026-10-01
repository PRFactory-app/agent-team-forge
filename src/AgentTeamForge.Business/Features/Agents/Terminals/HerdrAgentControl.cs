using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Herdr's native agent commands keep input and lifecycle tied to the owned pane.</summary>
internal sealed class HerdrAgentControl(HerdrTerminal terminal, TimeSpan? readinessTimeout = null) : IHerdrAgentControl
{
    // A resumed agent replays its transcript before it is ready, so it gets a longer `agent start` wait.
    internal const int FreshStartTimeoutMs = 15_000;
    internal const int ResumeStartTimeoutMs = 90_000;
    static readonly TimeSpan BusyWaitLimit = TimeSpan.FromHours(1);
    static readonly TimeSpan PaneBusyGrace = TimeSpan.FromSeconds(5);

    // stop_job terminates from another thread while the dispatcher is still polling.
    readonly ConcurrentDictionary<string, (OwnedHerdrSession Session, HerdrTabBinding Binding)> _runs = [];
    // Pi login errors already on screen when the prompt was sent (history of a resumed session).
    readonly ConcurrentDictionary<string, string[]> _piLoginAnchorBeforePrompt = [];

    public async Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
    {
        var bootstrap = Path.GetDirectoryName(launch.BootstrapPath)!;
        Directory.CreateDirectory(bootstrap);
        File.SetUnixFileMode(bootstrap, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using (var file = new FileStream(launch.BootstrapPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(launch.BootstrapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await file.WriteAsync(Encoding.UTF8.GetBytes(launch.AgentName), cancellationToken);
        }

        OwnedHerdrSession session;
        try
        {
            session = launch.HerdrPlacement is { } placement && placement.StartsWith("herdr-session:", StringComparison.Ordinal)
                ? await terminal.SharedSessionAsync(placement[14..], cancellationToken)
                : await terminal.StartSessionAsync(cancellationToken);
        }
        catch (InteractiveTerminalUnavailableException ex)
        {
            // Provider preflight runs before any session or tab creation.
            File.Delete(launch.BootstrapPath);
            throw new BackendNotStartedException(ex.Message, ex);
        }
        // Record immediately: a later tab/start failure is still an owned session.
        try
        {
            if (!session.Shared) { HerdrOwnedSessions.Save(launch, session); }
            var binding = await terminal.OpenAgentTabAsync(session, launch.TabLabel ?? launch.AgentName, launch.WorkingDirectory, launch.BootstrapPath, cancellationToken,
                workspaceTrustEnvironment: InteractiveAgentCommand.WorkspaceTrustEnvironment(launch.Kind),
                onCreated: created => { session = created; HerdrOwnedSessions.Save(launch, created); },
                exclusivePiMcp: launch.Kind == InteractiveAgentKind.Pi && InteractiveAgentCommand.ManagedConfigPath(launch) is { } config
                    && File.Exists(config));
            // The in-memory session carries the shell identity too, so PaneIsGone sees the same proof as the binding.
            session = session with { ShellPid = binding.ShellPid, ShellStartTicks = binding.ShellStartTicks };
            _runs[launch.AgentName] = (session, binding);
            HerdrOwnedSessions.Save(launch, session);
            var startTimeoutMs = launch.ResumeSessionId is null ? FreshStartTimeoutMs : ResumeStartTimeoutMs;
            var args = new List<string> { "agent", "start", launch.AgentName, "--kind", Kind(launch.Kind), "--pane", binding.PaneId, "--timeout", startTimeoutMs.ToString(), "--" };
            args.AddRange(AgentArguments(launch));
            try
            {
                // Herdr samples a pane's foreground lazily: right after the macOS shell proof ran in it, `agent start`
                // can still see a busy pane. It refuses before typing anything, so a short retry is safe.
                var busyUntil = DateTimeOffset.UtcNow + PaneBusyGrace;
                while (true)
                {
                    try
                    {
                        await terminal.RunOwnedAsync(session, TimeSpan.FromMilliseconds(startTimeoutMs) + TimeSpan.FromSeconds(15), cancellationToken, [.. args]);
                        break;
                    }
                    catch (HerdrLaunchException e) when (e.Message.Contains("agent_pane_busy", StringComparison.Ordinal) && DateTimeOffset.UtcNow < busyUntil)
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                }
            }
            catch (HerdrLaunchException) when (launch.ResumeSessionId is not null && !terminal.OwnedPaneIsGone(session))
            {
                // A slow resume can miss the start wait while the agent still comes up; PromptAsync proves readiness.
            }
        }
        catch (Exception ex)
        {
            var cleaned = false;
            try
            {
                if (!session.Shared || session.TabId is not null)
                {
                    await terminal.StopOwnedSessionAsync(session, CancellationToken.None);
                    cleaned = true;
                    HerdrOwnedSessions.Delete(launch);
                }
            }
            catch (HerdrLaunchException) { /* The original fault remains uncertain; never touch another session. */ }
            _runs.TryRemove(launch.AgentName, out _);
            if (cleaned)
            {
                HerdrOwnedSessions.DeleteLaunchFiles(launch.BootstrapPath);
                throw new BackendNotStartedException("interactive launch failed; owned session stopped: " + ex.Message, ex);
            }
            throw;
        }
    }

    internal async Task<bool> RebindAsync(InteractiveLaunch launch, OwnedHerdrSession session, CancellationToken cancellationToken)
    {
        var binding = await terminal.RebindAsync(session, launch.BootstrapPath, cancellationToken);
        if (binding is null) { return false; }
        _runs[launch.AgentName] = (session, binding);
        try
        {
            var status = await StatusAsync(launch, cancellationToken);
            if (status is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Working or InteractiveAgentStatus.Done or InteractiveAgentStatus.Blocked)
            {
                return true;
            }
        }
        catch (HerdrLaunchException) { }
        _runs.TryRemove(launch.AgentName, out _);
        return false;
    }

    internal bool PaneIsGone(OwnedHerdrSession session) => terminal.OwnedPaneIsGone(session);

    public bool PaneIsGone(InteractiveLaunch launch) => !_runs.TryGetValue(launch.AgentName, out var run) || terminal.OwnedPaneIsGone(run.Session);

    internal void TransferOwnership(InteractiveLaunch launch)
    {
        if (!_runs.TryGetValue(launch.AgentName, out var run))
        {
            throw new HerdrLaunchException("retained interactive pane has no owned binding");
        }
        HerdrOwnedSessions.Save(launch, run.Session);
    }

    public async Task PromptAsync(InteractiveLaunch launch, string prompt, CancellationToken cancellationToken)
    {
        var (session, binding) = Binding(launch);
        if (await terminal.VerifyBindingAsync(binding, cancellationToken) is { } problem)
        {
            throw new HerdrLaunchException("refusing prompt: " + problem);
        }
        // Agent detection can precede the first rendered input editor. Require a
        // settled ready state before sending any bytes, including on resumed panes.
        var readiness = readinessTimeout ?? InteractiveStartup.Timeout;
        var deadline = DateTimeOffset.UtcNow.Add(readiness);
        // A resumed agent can be busy with its own wake turn: queue behind it and submit once it is idle.
        // The wait is bounded by BusyWaitLimit, the turn deadline and stop (the cancellation token).
        var busyLimit = DateTimeOffset.UtcNow + BusyWaitLimit;
        var busyLogged = false;
        DateTimeOffset? readySince = null;
        string screen;
        while (true)
        {
            var status = await StatusAsync(launch, cancellationToken);
            screen = await terminal.ReadAgentAsync(session, binding.PaneId, cancellationToken);
            // A retained live pane already passed startup; its scrollback is agent output.
            if (!launch.LiveReuse && StartupBlocker(launch.Kind, screen, launch.ResumeSessionId is not null) is { } blocker) { throw blocker; }
            if (status is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done
                && HasInputEditor(launch.Kind, screen))
            {
                readySince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - readySince >= TimeSpan.FromSeconds(1)) { break; }
            }
            else { readySince = null; }
            if (status is InteractiveAgentStatus.Working && DateTimeOffset.UtcNow < busyLimit)
            {
                deadline = DateTimeOffset.UtcNow.Add(readiness);
                if (!busyLogged)
                {
                    busyLogged = true;
                    Console.Error.WriteLine($"[atf-daemon] prompt deferred: agent busy ({launch.AgentName})");
                }
            }
            if (status is InteractiveAgentStatus.Blocked or InteractiveAgentStatus.Gone || DateTimeOffset.UtcNow >= deadline)
            {
                throw new HerdrLaunchException("interactive agent not ready before prompt delivery");
            }
            await Task.Delay(250, cancellationToken);
        }
        launch.StartupProgress?.Invoke("ready");
        _piLoginAnchorBeforePrompt[launch.AgentName] = PiLoginAnchor(screen);
        try
        {
            launch.StartupProgress?.Invoke("submitted");
            // Herdr requires an observed state change after submission. Without --wait,
            // an immediate response can report success while the TUI has not processed it.
            var response = await terminal.RunOwnedAsync(session, cancellationToken, "--session", session.SessionName,
                "agent", "prompt", binding.PaneId, prompt, "--wait", "--timeout", "15000");
            if (response["result"]?["type"]?.GetValue<string>() != "agent_prompted")
            {
                throw new HerdrLaunchException("Herdr did not confirm agent prompt submission");
            }
        }
        catch (HerdrLaunchException e) when (IsUnsettledPrompt(e))
        {
            // A long turn can outlive the wait, and a fresh TUI can accept input without an
            // observed state change (agent_prompt_stalled). The prompt may already be executing;
            // ReadEvidenceAsync keeps observing this exact bound pane/transcript and never resends.
        }
    }

    public async Task InterruptAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
    {
        var (session, binding) = Binding(launch);
        if (await terminal.VerifyBindingAsync(binding, cancellationToken) is { } problem)
        {
            throw new HerdrLaunchException("refusing interrupt: " + problem);
        }
        await terminal.RunOwnedAsync(session, cancellationToken, "agent", "send-keys", binding.PaneId, "esc");
        await terminal.RunOwnedAsync(session, cancellationToken, "agent", "wait", binding.PaneId,
            "--until", "idle", "--until", "done", "--timeout", "15000");
    }

    public async Task<InteractiveAgentStatus> StatusAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
    {
        var (session, binding) = Binding(launch);
        if (terminal.HasUnverifiedLiveIdentity(binding))
        {
            return InteractiveAgentStatus.Unverified;
        }
        switch ((await terminal.CheckBindingAsync(binding, cancellationToken)).State)
        {
            case HerdrTerminal.BindingState.Gone: return InteractiveAgentStatus.Gone;
            case HerdrTerminal.BindingState.Unverified: return InteractiveAgentStatus.Unverified;
        }
        JsonNode state;
        try { state = await terminal.RunOwnedAsync(session, cancellationToken, "agent", "get", binding.PaneId); }
        catch (HerdrLaunchException e) when (e.Message.Contains("agent_not_found", StringComparison.Ordinal))
        {
            return InteractiveAgentStatus.Gone;
        }
        var text = FindStatus(state);
        return text switch
        {
            "idle" => InteractiveAgentStatus.Idle,
            "working" => InteractiveAgentStatus.Working,
            "done" => InteractiveAgentStatus.Done,
            "blocked" => InteractiveAgentStatus.Blocked,
            _ => InteractiveAgentStatus.Unknown,
        };
    }

    public bool StopJobs(string stateRoot, IReadOnlyList<string> jobIds) =>
        HerdrOwnedSessions.Stop(stateRoot, jobIds, session =>
        {
            var tree = PaneTree(session.ShellPid, session.ShellStartTicks);
            terminal.RecoverOwnedSessionAsync(session, CancellationToken.None).GetAwaiter().GetResult();
            if (HerdrOwnedSessions.ValidAgentName(session.AgentName)) { TerminateLeftovers(session.AgentName!, tree); }
        });

    internal bool IsBound(InteractiveLaunch launch) => _runs.ContainsKey(launch.AgentName);

    public ProcessIdentity? PaneShell(InteractiveLaunch launch) =>
        _runs.TryGetValue(launch.AgentName, out var run) ? new(run.Binding.ShellPid, run.Binding.ShellStartTicks) : null;

    public void StopOwned(InteractiveLaunch launch)
    {
        if (_runs.TryGetValue(launch.AgentName, out var run))
        {
            var tree = PaneTree(run.Binding.ShellPid, run.Binding.ShellStartTicks);
            terminal.StopOwnedSessionAsync(run.Session, CancellationToken.None).GetAwaiter().GetResult();
            TerminateLeftovers(launch.AgentName, tree);
            _runs.TryRemove(launch.AgentName, out _);
            _piLoginAnchorBeforePrompt.TryRemove(launch.AgentName, out _);
            // The bootstrap files proved this pane's ownership; the record goes last, as it fences the job.
            HerdrOwnedSessions.DeleteLaunchFiles(launch.BootstrapPath);
            HerdrOwnedSessions.Delete(launch);
        }
    }

    /// <summary>
    /// Captured before the pane closes: on macOS a tool shell started in its own session survives the pane's hangup,
    /// is reparented to launchd and, as a platform binary, never shows the launch marker.
    /// </summary>
    static IReadOnlyDictionary<int, ulong> PaneTree(int? shellPid, ulong? shellToken) =>
        OperatingSystem.IsMacOS() && shellPid is int pid && shellToken is ulong token
            ? RunProcessSnapshots.Tree(pid, token) : new Dictionary<int, ulong>();

    /// <summary>Codex tool shells carry the launch marker (InteractiveAgentCommand) and outlive the closed pane.</summary>
    static void TerminateLeftovers(string agentName, IReadOnlyDictionary<int, ulong> tree)
    {
        OrphanedBackendProcess.TerminateMarked([agentName]);
        RunProcessSnapshots.Signal(tree);
    }

    internal static bool IsUnsettledPrompt(HerdrLaunchException e) =>
        e.Message.Contains("timeout", StringComparison.Ordinal) || e.Message.Contains("timed out", StringComparison.Ordinal)
        || e.Message.Contains("agent_prompt_stalled", StringComparison.Ordinal);

    /// <summary>
    /// Pi rejects a prompt it has no credential for without recording it, so no transcript ever
    /// acknowledges it; its own error on screen is the only evidence. Only an error line that
    /// appeared after the prompt was sent counts, so a resumed session's history never does. A run
    /// this daemon did not prompt (recovered after a restart) has no record of the screen before its
    /// prompt, so no screen line is evidence for it.
    /// </summary>
    public async Task<AgentStartupBlockedException?> DeliveryBlockerAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
    {
        if (launch.Kind != InteractiveAgentKind.Pi || !_piLoginAnchorBeforePrompt.TryGetValue(launch.AgentName, out var anchor)) { return null; }
        var (session, binding) = Binding(launch);
        var screen = await terminal.ReadAgentAsync(session, binding.PaneId, cancellationToken);
        return DeliveryBlocker(launch.Kind, screen, anchor);
    }

    const int PiLoginAnchorContext = 4;

    /// <summary>The last Pi login line before the prompt with the lines above it; empty when there was none.</summary>
    internal static string[] PiLoginAnchor(string screen)
    {
        if (PiLoginLines(screen) is not [.., var last]) { return []; }
        var lines = screen.Split('\n');
        var first = Math.Max(0, last - PiLoginAnchorContext);
        return [.. lines[first..(last + 1)].Select(line => line.Trim())];
    }

    /// <summary>
    /// A login line below where <paramref name="anchor"/> (the last one before the prompt) now sits. The screen is a
    /// sliding window that only grows at the bottom, so the anchor is its topmost match (its context may be cut off
    /// at the top); once it has scrolled out, every visible login line came after it.
    /// </summary>
    internal static AgentStartupBlockedException? DeliveryBlocker(InteractiveAgentKind kind, string screen, IReadOnlyList<string> anchor)
    {
        if (kind != InteractiveAgentKind.Pi) { return null; }
        var found = PiLoginLines(screen);
        if (found.Count == 0) { return null; }
        var after = anchor.Count == 0 ? -1 : AnchorLine(screen.Split('\n'), anchor, found);
        return found[^1] > after ? PiLoginBlocker(screen, found[^1]) : null;
    }

    /// <summary>The topmost login line whose preceding lines match the anchor; -1 when it is no longer on screen.</summary>
    static int AnchorLine(string[] lines, IReadOnlyList<string> anchor, List<int> loginLines)
    {
        foreach (var index in loginLines)
        {
            var matches = true;
            for (var back = 0; back < anchor.Count && index - back >= 0; back++)
            {
                if (lines[index - back].Trim() != anchor[anchor.Count - 1 - back]) { matches = false; break; }
            }
            if (matches) { return index; }
        }
        return -1;
    }

    /// <summary>Pi's own signed-out lines ("Warning: No models available. Use /login ...", "Error: No API key found ..."), by index.</summary>
    static List<int> PiLoginLines(string screen)
    {
        var lines = screen.Split('\n');
        var found = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("Error: No API key found", StringComparison.Ordinal)
                || line.StartsWith("Warning: No models available", StringComparison.Ordinal))
            {
                found.Add(i);
            }
        }
        return found;
    }

    // The same mapping headless Pi uses, over the error line and its "Use /login" continuation.
    static AgentStartupBlockedException? PiLoginBlocker(string screen, int line) =>
        BackendLoginErrors.Inspect("pi", string.Join('\n', screen.Split('\n').Skip(line).Take(3))) is { } login
            ? new(login.Code, login.Details) : null;

    internal static AgentStartupBlockedException? StartupBlocker(InteractiveAgentKind kind, string screen, bool resumed = false)
    {
        bool Has(string text) => screen.Contains(text, StringComparison.OrdinalIgnoreCase);
        // A fresh Pi renders its editor even without any credential and warns above it.
        if (kind == InteractiveAgentKind.Pi && !resumed && PiLoginLines(screen) is [var first, ..])
        {
            return PiLoginBlocker(screen, first);
        }
        // A ready editor (or a resumed transcript) can contain historical output quoting
        // setup screens. Only Claude's logged-out status in the footer below the editor
        // is a blocker then.
        if (HasInputEditor(kind, screen) || resumed)
        {
            return kind == InteractiveAgentKind.Claude && ClaudeFooterLoggedOut(screen)
                ? new("agent_login_required", "Claude may require login; run `claude` once in a terminal to log in.")
                : null;
        }
        var command = kind == InteractiveAgentKind.Claude ? "claude" : "codex";
        if (kind == InteractiveAgentKind.Claude)
        {
            if (Has("Please run /login") && Has("Not logged in") || Has("Select login method")
                || Has("Claude account with subscription") && Has("Anthropic Console account"))
            {
                return new("agent_login_required", "Claude may require login; run `claude` once in a terminal to log in.");
            }
            if (Has("Choose the text style") || Has("Choose the theme") && Has("Dark mode")
                || Has("Try the new full-screen renderer?")
                || Has("Try the new fullscreen renderer?"))
            {
                return new("agent_first_run_required", "Claude may require first-run setup; run `claude` once in a terminal to finish setup.");
            }
        }
        if (kind == InteractiveAgentKind.Codex && (Has("Sign in with ChatGPT") && Has("API key")
            || Has("Welcome to Codex") && Has("Sign in")))
        {
            return new("agent_login_required", "Codex may require login; run `codex` once in a terminal to log in.");
        }
        if (kind is InteractiveAgentKind.Claude or InteractiveAgentKind.Codex
            && (Has("Do you trust the files in this folder") || Has("Yes, I trust this folder")
                || Has("Do you trust the contents of this directory")))
        {
            return new("agent_workspace_trust_required", $"Workspace trust may require attention; run `{command}` once in a terminal in this workspace.");
        }
        return null;
    }

    static bool ClaudeFooterLoggedOut(string screen)
    {
        var lines = screen.Split('\n').Select(line => line.Trim()).ToArray();
        var editor = Array.FindLastIndex(lines, IsClaudeEditorLine);
        return editor >= 0 && lines.Skip(editor + 1).Any(line =>
            line.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) && line.Contains("Please run /login", StringComparison.OrdinalIgnoreCase));
    }

    static bool IsClaudeEditorLine(string line) => line == "❯" || line.Length > 2 && line[0] == '❯' && char.IsWhiteSpace(line[1]) && !char.IsDigit(line[2]);

    internal static bool HasInputEditor(InteractiveAgentKind kind, string screen)
    {
        // Herdr's known-agent idle fallback also matches startup/login screens.
        // Require the rendered editor and its footer, not merely a detected process.
        var lines = screen.Split('\n').Select(line => line.Trim()).ToArray();
        return kind switch
        {
            InteractiveAgentKind.Codex => lines.Any(line => line.StartsWith('›'))
                && (screen.Contains("? for shortcuts", StringComparison.Ordinal) || screen.Contains("context left", StringComparison.Ordinal))
                && !lines.Any(line => line.StartsWith("│ model:", StringComparison.Ordinal) && line.Contains("loading", StringComparison.OrdinalIgnoreCase)),
            InteractiveAgentKind.Claude => lines.Any(IsClaudeEditorLine)
                && screen.Contains("bypass permissions", StringComparison.OrdinalIgnoreCase),
            InteractiveAgentKind.Pi => lines.Count(line => line.Length > 5 && line.All(c => c == '─')) >= 2
                && screen.Contains('/'),
            _ => false,
        };
    }

    (OwnedHerdrSession Session, HerdrTabBinding Binding) Binding(InteractiveLaunch launch) =>
        _runs.TryGetValue(launch.AgentName, out var binding) ? binding : throw new HerdrLaunchException("interactive run is not bound to an owned tab");

    static string Kind(InteractiveAgentKind kind) => kind switch
    {
        InteractiveAgentKind.Claude => "claude",
        InteractiveAgentKind.Codex => "codex",
        InteractiveAgentKind.Pi => "pi",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static IReadOnlyList<string> AgentArguments(InteractiveLaunch launch) => InteractiveAgentCommand.ManagedArguments(launch);

    static string? FindStatus(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["agent_status"] is JsonValue agentStatus && agentStatus.TryGetValue<string>(out var agentText) && IsAgentStatus(agentText))
            {
                return agentText;
            }
            if (obj["status"] is JsonValue status && status.TryGetValue<string>(out var text) && IsAgentStatus(text))
            {
                return text;
            }
            if (obj["state"] is JsonValue state && state.TryGetValue<string>(out text) && IsAgentStatus(text))
            {
                return text;
            }
            foreach (var (_, child) in obj)
            {
                if (FindStatus(child) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    static bool IsAgentStatus(string text) => text is "idle" or "working" or "done" or "blocked" or "unknown";

}
