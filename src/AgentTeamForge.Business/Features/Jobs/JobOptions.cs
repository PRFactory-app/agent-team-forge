namespace AgentTeamForge.Business.Features.Jobs;

public static class JobOptions
{
    public static string? Read(string options, string name) => options.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(pair => pair.Length == 2 && pair[0] == name)
        .Select(pair => pair[1]).FirstOrDefault();
}
