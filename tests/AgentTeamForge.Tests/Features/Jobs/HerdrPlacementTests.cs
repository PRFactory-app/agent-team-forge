using AgentTeamForge.Business.Features.Jobs;
using AgentTeamForge.Tests.Support;

namespace AgentTeamForge.Tests.Features.Jobs;

public sealed class HerdrPlacementTests
{
    [Fact]
    public void PersistsPrivateDefaultAndResolvesOverride()
    {
        using var state = new TempStateDir();
        var settings = new HerdrPlacement(state.Path);
        Assert.Equal("own-session", settings.Resolve(null));
        settings.Change("herdr-session:default");
        Assert.Equal("herdr-session:default", new HerdrPlacement(state.Path).Resolve(null));
        Assert.Equal("own-session", settings.Resolve("own-session"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state.File("herdr-placement.json")));
    }

    [Fact]
    public void CorruptFileFallsBackAndInvalidNamesAreRejected()
    {
        using var state = new TempStateDir();
        File.WriteAllText(state.File("herdr-placement.json"), "broken");
        var settings = new HerdrPlacement(state.Path);
        Assert.Equal("own-session", settings.Default);
        Assert.Throws<ArgumentException>(() => settings.Change("herdr-session:bad;name"));
        Assert.Throws<ArgumentException>(() => settings.Resolve("herdr-session:"));
    }
}
