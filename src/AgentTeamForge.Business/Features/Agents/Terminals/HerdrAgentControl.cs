using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Herdr's native agent commands keep input and lifecycle tied to the owned pane.</summary>
internal sealed class HerdrAgentControl(HerdrTerminal terminal, TimeSpan? readinessTimeout = null) : IHerdrAgentControl
{
    // stop_job terminates from another thread while the dispatcher is still polling.
    readonly ConcurrentDictionary<string, (OwnedHerdrSession Session, HerdrTabBinding Binding)> _runs = [];

    public async Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
    {
        var bootstrap = Path.GetDirectoryName(launch.BootstrapPath)!;
        Directory.CreateDirectory(bootstrap);
        File.SetUnixFileMode(bootstrap, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(launch.BootstrapPath, launch.AgentName, cancellationToken);
        File.SetUnixFileMode(launch.BootstrapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        OwnedHerdrSession session;
        try
        {
            session = launch.HerdrPlacement is { } placement && placement.StartsWith("herdr-session:", StringComparison.Ordinal)
                ? await terminal.ExistingSessionAsync(placement[14..], cancellationToken)
                : await terminal.StartSessionAsync(cancellationToken);
        }
        catch (InteractiveTerminalUnavailableException ex)
        {
            // Provider preflight runs before any session or tab creation.
            throw new BackendNotStartedException(ex.Message, ex);
        }
        // Record immediately: a later tab/start failure is still an owned session.
        try
        {
            if (!session.Shared) { HerdrOwnedSessions.Save(launch, session); }
            var binding = await terminal.OpenAgentTabAsync(session, launch.AgentName, launch.WorkingDirectory, launch.BootstrapPath, cancellationToken,
                workspaceTrustEnvironment: InteractiveAgentCommand.WorkspaceTrustEnvironment(launch.Kind),
                onCreated: created => { session = created; HerdrOwnedSessions.Save(launch, created); },
                exclusivePiMcp: launch.Kind == InteractiveAgentKind.Pi && InteractiveAgentCommand.ManagedConfigPath(launch) is { } config
                    && File.Exists(config));
            _runs[launch.AgentName] = (session, binding);
            var args = new List<string> { "agent", "start", launch.AgentName, "--kind", Kind(launch.Kind), "--pane", binding.PaneId, "--timeout", "15000", "--" };
            args.AddRange(AgentArguments(launch));
            await terminal.RunOwnedAsync(session, cancellationToken, [.. args]);
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
            if (cleaned) { throw new BackendNotStartedException("interactive launch failed; owned session stopped: " + ex.Message, ex); }
            throw;
        }
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
        var deadline = DateTimeOffset.UtcNow.Add(readinessTimeout ?? InteractiveStartup.Timeout);
        DateTimeOffset? readySince = null;
        while (true)
        {
            var status = await StatusAsync(launch, cancellationToken);
            var screen = await terminal.ReadAgentAsync(session, binding.PaneId, cancellationToken);
            // A retained live pane already passed startup; its scrollback is agent output.
            if (!launch.LiveReuse && StartupBlocker(launch.Kind, screen, launch.ResumeSessionId is not null) is { } blocker) { throw blocker; }
            if (status is InteractiveAgentStatus.Idle or InteractiveAgentStatus.Done
                && HasInputEditor(launch.Kind, screen))
            {
                readySince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - readySince >= TimeSpan.FromSeconds(1)) { break; }
            }
            else { readySince = null; }
            if (status is InteractiveAgentStatus.Blocked or InteractiveAgentStatus.Gone || DateTimeOffset.UtcNow >= deadline)
            {
                throw new HerdrLaunchException("interactive agent not ready before prompt delivery");
            }
            await Task.Delay(250, cancellationToken);
        }
        launch.StartupProgress?.Invoke("ready");
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
        if (await terminal.VerifyBindingAsync(binding, cancellationToken) is not null)
        {
            return InteractiveAgentStatus.Gone;
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
            terminal.RecoverOwnedSessionAsync(session, CancellationToken.None).GetAwaiter().GetResult());

    public void StopOwned(InteractiveLaunch launch)
    {
        if (_runs.TryGetValue(launch.AgentName, out var run))
        {
            terminal.StopOwnedSessionAsync(run.Session, CancellationToken.None).GetAwaiter().GetResult();
            _runs.TryRemove(launch.AgentName, out _);
            HerdrOwnedSessions.Delete(launch);
        }
    }

    internal static bool IsUnsettledPrompt(HerdrLaunchException e) =>
        e.Message.Contains("timeout", StringComparison.Ordinal) || e.Message.Contains("timed out", StringComparison.Ordinal)
        || e.Message.Contains("agent_prompt_stalled", StringComparison.Ordinal);

    internal static AgentStartupBlockedException? StartupBlocker(InteractiveAgentKind kind, string screen, bool resumed = false)
    {
        bool Has(string text) => screen.Contains(text, StringComparison.OrdinalIgnoreCase);
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
