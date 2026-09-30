using AgentTeamForge.Host.Features.PRFactory;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class ReadSecretTests
{
    static Func<ConsoleKeyInfo> Keys(params ConsoleKeyInfo[] keys)
    {
        var queue = new Queue<ConsoleKeyInfo>(keys);
        return queue.Dequeue;
    }

    static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.A, false, false, false);
    static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);
    static readonly ConsoleKeyInfo Back = new('\b', ConsoleKey.Backspace, false, false, false);

    [Fact]
    public void Backspace_removes_last_char_and_enter_returns_value() =>
        Assert.Equal("ab", PRFactoryConnection.ReadSecret(Keys(Ch('a'), Ch('x'), Back, Ch('b'), Enter)));

    [Fact]
    public void Backspace_on_empty_buffer_is_ignored_and_empty_enter_is_empty() =>
        Assert.Equal("", PRFactoryConnection.ReadSecret(Keys(Back, Enter)));

    [Fact]
    public void Ctrl_c_aborts() =>
        Assert.Null(PRFactoryConnection.ReadSecret(Keys(Ch('a'), new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true))));
}
