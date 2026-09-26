using System.Text;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Business.Features.Agents.Terminals;

/// <summary>Arguments and per-process trust environment shared by interactive terminal launchers.</summary>
internal static class InteractiveAgentCommand
{
    internal static (string Name, string Value)? WorkspaceTrustEnvironment(InteractiveAgentKind kind) =>
        kind == InteractiveAgentKind.Claude ? ("CLAUDE_CODE_SANDBOXED", "1") : null;

    internal static IReadOnlyList<string> Arguments(InteractiveLaunch launch, bool piShortApprove = false)
    {
        var args = new List<string>();
        switch (launch.Kind)
        {
            case InteractiveAgentKind.Claude:
                args.AddRange(["--permission-mode", "bypassPermissions", "--settings", "{\"skipDangerousModePermissionPrompt\":true}"]);
                break;
            case InteractiveAgentKind.Codex:
                // The cwd key belongs in the TOML value: Codex splits CLI override paths on dots.
                // Escape backslashes in Windows paths as TOML basic-string characters.
                args.AddRange(["--dangerously-bypass-approvals-and-sandbox", "-C", launch.WorkingDirectory,
                    "-c", "projects={" + TomlKey(launch.WorkingDirectory) + "={trust_level=\"trusted\"}}"]);
                break;
            case InteractiveAgentKind.Pi:
                args.AddRange([piShortApprove ? "-a" : "--approve", "--session-dir", launch.PiSessionDirectory!,
                    "--exclude-tools", "ask_user,ask_question,ask_human,request_input"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(launch));
        }
        if (launch.Model is { Length: > 0 } model)
        {
            args.AddRange(launch.Kind switch
            {
                InteractiveAgentKind.Codex => ["-m", model],
                InteractiveAgentKind.Pi => ["--model", model.Contains('/') ? model : "openai-codex/" + model],
                _ => ["--model", model],
            });
        }
        if (launch.Effort is { Length: > 0 } effort)
        {
            switch (launch.Kind)
            {
                case InteractiveAgentKind.Claude: args.AddRange(["--effort", effort]); break;
                case InteractiveAgentKind.Codex: args.AddRange(["-c", "model_reasoning_effort=\"" + effort + "\""]); break;
                case InteractiveAgentKind.Pi when PiThinking.Valid(effort): args.AddRange(["--thinking", effort]); break;
            }
        }
        if (launch.ResumeSessionId is { } id)
        {
            if (launch.Kind == InteractiveAgentKind.Claude) { args.AddRange(["--resume", id]); }
            else if (launch.Kind == InteractiveAgentKind.Codex) { args.AddRange(["resume", id]); }
            else { args.Add("--continue"); }
        }
        return args;
    }

    static string TomlKey(string value)
    {
        var key = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '\\' or '"') { key.Append('\\').Append(c); }
            else if (c is < ' ' or '\u007f') { key.Append("\\u").Append(((int)c).ToString("X4")); }
            else { key.Append(c); }
        }
        return key.Append('"').ToString();
    }
}
