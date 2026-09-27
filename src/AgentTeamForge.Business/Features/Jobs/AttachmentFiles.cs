namespace AgentTeamForge.Business.Features.Jobs;

public static class AttachmentFiles
{
    public static string? MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".patch" or ".diff" => "text/x-diff",
        _ => null
    };
}
