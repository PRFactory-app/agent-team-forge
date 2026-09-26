using System.Text;
using AgentTeamForge.DAL.Files;

namespace AgentTeamForge.Tests.Hosting;

public sealed class LiveFilesTests
{
    [Fact]
    public void ReadsLinesWhileAnotherHandleHoldsTheFileOpenForWriting()
    {
        var path = Path.Combine(Path.GetTempPath(), "atf-live-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            // Like a live Codex/Claude TUI appending to its rollout.
            using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("{\"a\":1}");
            writer.WriteLine("Hej från");

            Assert.Equal(["{\"a\":1}", "Hej från"], [.. LiveFiles.ReadLines(path)]);
        }
        finally { File.Delete(path); }
    }
}
