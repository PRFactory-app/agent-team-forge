using static AgentTeamForge.Tests.Support.ManagedDemo.ClaudeHookFixture;

namespace AgentTeamForge.Tests.Features.Agents.Claude;

/// <summary>
/// Negative capability characterization of the reviewed legacy Claude hook parser (8a5e385).
/// These cases are fixtures for D11; they do not claim Claude cancellation or origin support.
/// </summary>
public class ClaudeHookCharacterizationTests
{
    const string Sid = "sess-1";

    [Fact]
    public void HookRecord_WrongSessionRejected()
    {
        var view = Fold(
        [
            HookLine("SessionStart", "sess-other"),
            HookLine("UserPromptSubmit", Sid, "p1", "[atf-job:j1] do it"),
            HookLine("Stop", "sess-other", "p1", lastMessage: "foreign result"),
            HookLine("StopFailure", "sess-other", "p1"),
            HookLine("SessionEnd", "sess-other"),
            """{"event":"Stop","prompt_id":"p1","last_assistant_message":"no session"}""",
        ], Sid);

        var turn = Assert.Single(view.Turns);
        Assert.Equal(TurnStatus.InProgress, turn.Status);
        Assert.Null(turn.Result);
        Assert.True(view.IsBusy);
        Assert.False(view.SessionEnded);
        Assert.Null(view.ClaudePid);

        var other = Fold([HookLine("UserPromptSubmit", Sid, "p1", "x")], "sess-other");
        Assert.Empty(other.Turns);
    }

    [Fact]
    public void TruncatedHookLog_IsUnknown()
    {
        var full = HookLine("Stop", Sid, "p1", lastMessage: "OK");
        var torn = full[..(full.Length / 2)];

        var view = Fold([HookLine("UserPromptSubmit", Sid, "p1", "[atf-job:j1] do it"), torn], Sid);

        var turn = Assert.Single(view.Turns);
        Assert.Equal(TurnStatus.InProgress, turn.Status);
        Assert.Null(turn.Result);
        Assert.Equal(1, view.MalformedLines);

        var observed = new ObservedTurn("j1", turn.PromptId, turn.Status, turn.Result, "att1");
        var outcome = NonAuthoritative(Reconcile("j1", JobState.Dispatching, [observed], "att1"));
        Assert.Equal(JobState.NeedsReconciliation, outcome.State);
        Assert.Null(outcome.ResultText);
        Assert.False(IsTerminal(outcome.State));
    }

    [Fact]
    public void StopHook_AloneDoesNotProveJobOrigin()
    {
        // A Stop with no matching prompt lifecycle is not a completion at all.
        var orphan = Fold([HookLine("Stop", Sid, "p1", lastMessage: "OK")], Sid);
        Assert.Empty(orphan.Turns);
        Assert.False(orphan.IsBusy);
        var noEvidence = NonAuthoritative(Reconcile("j1", JobState.Dispatching, []));
        Assert.Equal(JobState.NeedsReconciliation, noEvidence.State);
        Assert.Null(noEvidence.TurnKey);

        // A human typing the exact tagged text yields the same evidence as the harness would.
        const string sent = "[atf-job:j1] do it";
        var harness = HookLine("UserPromptSubmit", Sid, "p1", sent, ts: 101);
        var human = HookLine("UserPromptSubmit", Sid, "p1", sent, ts: 101);
        Assert.Equal(harness, human);

        // Even with the most optimistic job binding, the Stop-completed turn is not authoritative.
        var view = Fold([harness, HookLine("Stop", Sid, "p1", lastMessage: "OK")], Sid);
        var turn = Assert.Single(view.Turns);
        Assert.Equal((TurnStatus.Completed, "OK"), (turn.Status, turn.Result));
        Assert.Equal(["j1"], turn.Tags);

        var raw = Reconcile("j1", JobState.Dispatching, [new ObservedTurn("j1", turn.PromptId, turn.Status, turn.Result, "att1")], "att1");
        Assert.Equal(JobState.Completed, raw.State);

        var gated = NonAuthoritative(raw);
        Assert.Equal(JobState.NeedsReconciliation, gated.State);
        Assert.Null(gated.ResultText);
        Assert.Equal("p1", gated.TurnKey);
        Assert.False(IsTerminal(gated.State));
    }
}
