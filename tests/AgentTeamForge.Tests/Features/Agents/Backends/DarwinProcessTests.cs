using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AgentTeamForge.Business.Features.Agents.Backends;

namespace AgentTeamForge.Tests.Features.Agents.Backends;

public sealed class DarwinProcessTests
{
    [Fact]
    public void KinfoStartTimeUsesValidatedSecondsAndMicroseconds()
    {
        var raw = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(raw, 1_700_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), 123_456);
        Assert.Equal(((ulong)1_700_000_000 << 20) | 123_456UL, DarwinProcess.ParseKinfoStartTime(raw));
        Assert.Null(DarwinProcess.ParseKinfoStartTime(raw.AsSpan(0, 11)));
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), 1_000_000);
        Assert.Null(DarwinProcess.ParseKinfoStartTime(raw));
    }

    [Fact]
    public void DescriptorTableRetriesAReadThatFillsItsBuffer()
    {
        // The table grows past the probe's headroom before the first read, then settles at 100 descriptors.
        var reads = 0;
        var table = DarwinProcess.DescriptorTable(1, (buffer, size) =>
        {
            if (buffer is null) { return 2 * 8; }
            reads++;
            return size < 100 * 8 ? size : 100 * 8;
        });

        Assert.Equal(100 * 8, table.Length);
        Assert.True(reads > 1);
    }

    [Fact]
    public void DescriptorTableThatKeepsFillingItsBufferIsNeverReturned()
    {
        Assert.Throws<IOException>(() => DarwinProcess.DescriptorTable(1, (buffer, size) => buffer is null ? 8 : size));
        Assert.Throws<IOException>(() => DarwinProcess.DescriptorTable(1, (buffer, _) => buffer is null ? 8 : 0));
        // The fake list makes no P/Invoke, so clear the thread's last error left by earlier native calls.
        Marshal.SetLastPInvokeError(0);
        Assert.Empty(DarwinProcess.DescriptorTable(1, (_, _) => 0));
    }

    [Fact]
    public void OpenFilesListsEveryDescriptorBeyondTheProbeHeadroom()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        var files = Enumerable.Range(0, 64).Select(i => File.Open(state.File($"f{i}"), FileMode.Create)).ToList();
        try
        {
            var open = DarwinProcess.OpenFiles(Environment.ProcessId);
            Assert.All(files, file => Assert.Contains(open, path => path.EndsWith("/" + Path.GetFileName(file.Name), StringComparison.Ordinal)));
        }
        finally { files.ForEach(file => file.Dispose()); }
    }

    [Fact]
    public async Task OpenFilesSkipsDescriptorsClosedWhileTheyAreListed()
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        using var kept = File.Open(state.File("kept"), FileMode.Create);
        using var stop = new CancellationTokenSource();
        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var _ = File.Open(state.File("churn"), FileMode.OpenOrCreate);
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            for (var i = 0; i < 200; i++)
            {
                Assert.Contains(DarwinProcess.OpenFiles(Environment.ProcessId), path => path.EndsWith("/kept", StringComparison.Ordinal));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await churn;
        }
    }

    [Fact]
    public void WorkingDirectoryIsAnotherProcesssPhysicalCwd()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Null(DarwinProcess.WorkingDirectory(Environment.ProcessId));
            return;
        }
        using var state = new AgentTeamForge.Tests.Support.TempStateDir();
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sleep", "30")
        { UseShellExecute = false, WorkingDirectory = state.Path })!;
        try
        {
            // getcwd semantics: /tmp/... is reported as /private/tmp/....
            Assert.Equal(AgentTeamForge.Business.Features.Agents.Terminals.PhysicalPath.Resolve(state.Path), DarwinProcess.WorkingDirectory(child.Id));
            Assert.Null(DarwinProcess.WorkingDirectory(int.MaxValue));
        }
        finally { child.Kill(); }
    }

    [Fact]
    public void KinfoRowCarriesPidParentAndZombieState()
    {
        var raw = new byte[648];
        BinaryPrimitives.WriteInt64LittleEndian(raw, 1_700_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(40), 4242);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(560), 77);
        raw[36] = 5;

        Assert.Equal(new DarwinProcess.Entry(4242, 77, (ulong)1_700_000_000 << 20, true), DarwinProcess.ParseKinfo(raw));
        Assert.Null(DarwinProcess.ParseKinfo(raw.AsSpan(0, 647)));
    }

    [Fact]
    public void ProcessTableListsThisProcessUnderItsParent()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Darwin sysctl only");
        var self = Assert.Single(DarwinProcess.Table(), e => e.Pid == Environment.ProcessId);

        Assert.Equal(DarwinProcess.Info(Environment.ProcessId), self);
        Assert.True(self.ParentPid > 0);
        Assert.False(self.IsZombie);
        Assert.NotNull(self.Token);
    }
}
