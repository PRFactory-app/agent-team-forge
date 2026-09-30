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
        Assert.Equal("herdr-session:default", settings.Resolve(null));
        settings.Change("herdr-session:default");
        Assert.Equal("herdr-session:default", new HerdrPlacement(state.Path).Resolve(null));
        Assert.Equal("herdr-session:other", settings.Resolve("herdr-session:other"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(state.File("herdr-placement.json")));
    }

    [Fact]
    public void CorruptFileFallsBackAndInvalidNamesAreRejected()
    {
        using var state = new TempStateDir();
        File.WriteAllText(state.File("herdr-placement.json"), "broken");
        var settings = new HerdrPlacement(state.Path);
        Assert.Equal("herdr-session:default", settings.Default);
        Assert.Throws<ArgumentException>(() => settings.Change("herdr-session:bad;name"));
        Assert.Throws<ArgumentException>(() => settings.Resolve("herdr-session:"));
    }

    [Fact]
    public void OwnSessionIsRejectedAndAStoredOwnSessionFallsBackToTheSharedDefault()
    {
        using var state = new TempStateDir();
        var settings = new HerdrPlacement(state.Path);
        Assert.Throws<ArgumentException>(() => settings.Change("own-session"));
        Assert.Throws<ArgumentException>(() => settings.Resolve("own-session"));

        File.WriteAllText(state.File("herdr-placement.json"), """{"Placement":"own-session"}""");
        Assert.Equal("herdr-session:default", new HerdrPlacement(state.Path).Default);
    }
}
