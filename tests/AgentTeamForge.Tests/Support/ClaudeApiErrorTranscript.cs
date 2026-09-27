using AgentTeamForge.Business.Features.Agents.Terminals;

namespace AgentTeamForge.Tests.Support;

internal sealed class ClaudeApiErrorTranscript : IDisposable
{
    readonly TempStateDir _state = new();
    readonly string _file;

    public ClaudeApiErrorTranscript()
    {
        var cwd = Environment.CurrentDirectory;
        var encoded = new string([.. Path.GetFullPath(cwd).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        var config = Path.Combine(_state.Path, "claude");
        var directory = Directory.CreateDirectory(Path.Combine(config, "projects", encoded));
        _file = Path.Combine(directory.FullName, "session.jsonl");
        Reader = new InteractiveTranscriptReader(name => name switch
        {
            "CLAUDE_CONFIG_DIR" => config,
            "HOME" => _state.Path,
            _ => null,
        });
    }

    public InteractiveTranscriptReader Reader { get; }

    public void Write(string correlation, string error = "authentication_failed", string message = "Not logged in · Please run /login",
        bool endTurn = false)
    {
        var stop = endTurn ? "\"stop_reason\":\"end_turn\"," : "";
        File.WriteAllLines(_file,
        [
            $$$"""{"type":"user","isSidechain":false,"sessionId":"claude-native","message":{"role":"user","content":"atf-corr:{{{correlation}}}"}}""",
            $$$"""{"type":"assistant","isSidechain":false,"sessionId":"claude-native","isApiErrorMessage":true,"error":"{{{error}}}","message":{"role":"assistant","model":"<synthetic>",{{{stop}}}"content":[{"type":"text","text":"{{{message}}}"}]}}""",
        ]);
    }

    public void Dispose() => _state.Dispose();
}
