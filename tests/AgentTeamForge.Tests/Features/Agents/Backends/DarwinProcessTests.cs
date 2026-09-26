using System.Buffers.Binary;
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
}
