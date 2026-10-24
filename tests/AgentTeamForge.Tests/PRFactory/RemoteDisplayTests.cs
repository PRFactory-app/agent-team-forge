using AgentTeamForge.Business.Features.Jobs;

namespace AgentTeamForge.Tests.PRFactory;

public sealed class RemoteDisplayTests
{
    [Theory]
    [InlineData("https::https://oauth2:SECRET@h/r.git", "https::https://h/r.git")]
    [InlineData("git+ssh://u:SECRET@h/r.git", "git+ssh://h/r.git")]
    [InlineData("ssh+git://u:%53%45%43%52%45%54@h/r.git", "ssh+git://h/r.git")]
    [InlineData("https://u:prefix@SECRET:tail@h/r.git", "https://h/r.git")]
    [InlineData("A1+.-://u:SECRET@h/r.git?next=ssh://u:SECRET@other/r#end", "A1+.-://h/r.git?next=ssh://other/r#end")]
    [InlineData("git@host:team/repo.git", "git@host:team/repo.git")]
    [InlineData("ssh://u:SECRET@h?email=a@b#end", "ssh://h?email=a@b#end")]
    [InlineData("ssh://u:SECRET@h#email=a@b", "ssh://h#email=a@b")]
    public void Redaction_preserves_spelling_outside_URI_userinfo(string remote, string expected)
    {
        var display = RemoteDisplay.Redact(remote);
        Assert.Equal(expected, display);
        Assert.DoesNotContain("SECRET", display);
        Assert.DoesNotContain("%53%45%43%52%45%54", display, StringComparison.OrdinalIgnoreCase);
    }
}
