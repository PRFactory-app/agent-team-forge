using System.Text;

namespace AgentTeamForge.DAL.Files;

/// <summary>
/// Reads files another live process may hold open for writing (agent transcripts, daemon.log).
/// File.ReadLines shares only Read, which Windows rejects while a writer has the file open.
/// </summary>
public static class LiveFiles
{
    public static IEnumerable<string> ReadLines(string path)
    {
        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
