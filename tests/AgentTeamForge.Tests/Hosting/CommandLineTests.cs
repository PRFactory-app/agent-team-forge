using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Hosting;

public sealed class CommandLineTests
{
    static (int? Exit, string Out, string Err) Run(string line)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var exit = CommandLine.Check(line.Split(' '), o, e);
        return (exit, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("worktrees prune --help")]
    [InlineData("worktrees prune -h")]
    [InlineData("worktrees prune --job J --help")]
    [InlineData("worktrees --help")]
    [InlineData("prune --help")]
    [InlineData("uninstall --help")]
    [InlineData("prfactory disconnect -h")]
    [InlineData("setup --help")]
    [InlineData("stop --state-dir x --help")]
    [InlineData("daemon --state-dir x --help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help_prints_usage_and_exits_zero(string line)
    {
        var (exit, stdout, stderr) = Run(line);
        Assert.Equal(0, exit);
        Assert.NotEmpty(stdout);
        Assert.Empty(stderr);
    }

    [Theory]
    [InlineData("worktrees prune --bogus", "--bogus")]
    [InlineData("worktrees prune extra", "extra")]
    [InlineData("prune -n", "-n")]
    [InlineData("prune foo", "foo")]
    [InlineData("setup --dry-run", "--dry-run")]
    [InlineData("uninstall --purge -f", "-f")]
    [InlineData("prfactory disconnect --yes", "--yes")]
    public void Unknown_arguments_fail_with_64_naming_the_token(string line, string token)
    {
        var (exit, stdout, stderr) = Run(line);
        Assert.Equal(64, exit);
        Assert.Contains(token, stderr);
        Assert.Empty(stdout);
    }

    [Theory]
    [InlineData("worktrees prune --job J1 --force")]
    [InlineData("worktrees prune --dry-run --state-dir /x")]
    [InlineData("prune --older-than=7d")]
    [InlineData("prune --older-than 7d --dry-run")]
    [InlineData("prfactory connect --url https://x/ --repo a=b --repo c=d --external a:m")]
    [InlineData("uninstall --teardown-only --state-dir s --force")]
    [InlineData("setup --autostart=off")]
    [InlineData("setup --mode herdr --apply")]
    [InlineData("web --port 1")]
    [InlineData("client submit --anything v")]
    [InlineData("daemon --state-dir s --test-crash-at x")]
    [InlineData("init --state-dir s --test-profile")]
    [InlineData("bogus-command")]
    public void Valid_invocations_dispatch(string line)
    {
        var (exit, stdout, stderr) = Run(line);
        Assert.Null(exit);
        Assert.Empty(stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task Worktrees_prune_help_has_no_side_effects()
    {
        using var rig = new SpikeRig();
        await rig.InitAsync();

        var (exit, stdout, _) = await rig.RunToExitAsync(["worktrees", "prune", "--help", "--state-dir", rig.StateDir]);
        Assert.Equal(0, exit);
        Assert.Contains("worktrees prune", stdout);

        var (badExit, _, stderr) = await rig.RunToExitAsync(["worktrees", "prune", "--bogus", "--state-dir", rig.StateDir]);
        Assert.Equal(64, badExit);
        Assert.Contains("--bogus", stderr);
    }
}
