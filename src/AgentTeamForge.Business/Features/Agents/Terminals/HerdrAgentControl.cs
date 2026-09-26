using System.Text.Json.Nodes;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Herdr's native agent commands keep input and lifecycle tied to the owned pane.</summary>
internal sealed class HerdrAgentControl(HerdrTerminal terminal) : IHerdrAgentControl
{
    readonly Dictionary<string, (OwnedHerdrSession Session, HerdrTabBinding Binding)> _runs = [];

    public async Task StartAsync(InteractiveLaunch launch, CancellationToken cancellationToken)
    {
        var bootstrap = Path.GetDirectoryName(launch.BootstrapPath)!;
        Directory.CreateDirectory(bootstrap);
        File.SetUnixFileMode(bootstrap, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(launch.BootstrapPath, launch.AgentName, cancellationToken);
        File.SetUnixFileMode(launch.BootstrapPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var session = await terminal.StartSessionAsync(cancellationToken);
        // Record immediately: a later tab/start failure is still an owned session.
        try
        {
            var binding = await terminal.OpenAgentTabAsync(session, launch.AgentName, launch.WorkingDirectory, launch.BootstrapPath, cancellationToken);
            _runs.Add(launch.AgentName, (session, binding));
            var args = new List<string> { "agent", "start", launch.AgentName, "--kind", Kind(launch.Kind), "--pane", binding.PaneId, "--timeout", "15000", "--" };
            args.AddRange(AgentArguments(launch));
            await terminal.RunOwnedAsync(session, cancellationToken, [.. args]);
        }
        catch
        {
            try { await terminal.StopOwnedSessionAsync(session, CancellationToken.None); }
            catch (HerdrLaunchException) { /* The original fault remains uncertain; never touch another session. */ }
            _runs.Remove(launch.AgentName);
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
        try
        {
            // Herdr requires an observed state change after submission. Without --wait,
            // an immediate response can report success while the TUI has not processed it.
            var response = await terminal.RunOwnedAsync(session, cancellationToken, "--session", session.SessionName,
                "agent", "prompt", binding.PaneId, prompt, "--wait", "--timeout", "15000");
            if (response["result"]?["type"]?.GetValue<string>() != "agent_prompted")
            {
                throw new HerdrLaunchException("Herdr did not confirm agent prompt submission");
            }
        }
        catch (HerdrLaunchException e) when (e.Message.Contains("timeout", StringComparison.Ordinal))
        {
            // A long turn can outlive the wait. The prompt may already be executing;
            // ReadEvidenceAsync continues observing this exact bound pane/transcript.
        }
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

    public void StopOwned(InteractiveLaunch launch)
    {
        if (_runs.TryGetValue(launch.AgentName, out var run))
        {
            terminal.StopOwnedSessionAsync(run.Session, CancellationToken.None).GetAwaiter().GetResult();
            _runs.Remove(launch.AgentName);
        }
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

    internal static IReadOnlyList<string> AgentArguments(InteractiveLaunch launch)
    {
        var args = new List<string>();
        switch (launch.Kind)
        {
            case InteractiveAgentKind.Claude:
                args.AddRange(["--permission-mode", "bypassPermissions"]);
                break;
            case InteractiveAgentKind.Codex:
                args.AddRange(["--dangerously-bypass-approvals-and-sandbox", "-C", launch.WorkingDirectory]);
                break;
            case InteractiveAgentKind.Pi:
                args.AddRange(["--session-dir", launch.PiSessionDirectory!, "--exclude-tools", "ask_user,ask_question,ask_human,request_input"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(launch));
        }
        if (launch.ResumeSessionId is { } id)
        {
            if (launch.Kind == InteractiveAgentKind.Claude)
            {
                args.AddRange(["--resume", id]);
            }
            else if (launch.Kind == InteractiveAgentKind.Codex)
            {
                args.AddRange(["resume", id]);
            }
            else
            {
                args.Add("--continue");
            }
        }
        return args;
    }

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
