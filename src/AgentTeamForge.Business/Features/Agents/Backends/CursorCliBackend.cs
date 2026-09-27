namespace AgentTeamForge.Business.Features.Agents.Backends;

/// <summary>Cursor print mode with a durable native chat id for follow-ups.</summary>
public sealed class CursorCliBackend(string executable = "cursor-agent") : IJobBackend
{
    public IBackendRun Start(BackendRequest request) =>
        JsonCliBackend.Start(executable, "cursor-agent", request, BuildArguments(request));

    internal static List<string> BuildArguments(BackendRequest request)
    {
        List<string> args = ["-p", "--output-format", "json", "--force", "--sandbox", "disabled", "--trust", "--approve-mcps"];
        if (request.ResumeSessionId is { } id) { args.AddRange(["--resume", id]); }
        foreach (var part in request.Options.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Split('=', 2) is ["model", { Length: > 0 } model]) { args.AddRange(["--model", model]); }
        }
        return args;
    }
}
