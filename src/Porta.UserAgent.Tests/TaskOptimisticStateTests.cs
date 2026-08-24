using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Workstream A2 — the task action buttons work server-side, but tasks only re-poll every 10s
// so the row showed no change and users re-clicked (the second click failed as an illegal
// transition — "play does nothing"). The widget now reflects the transition immediately via
// this pure function; these lock its behaviour, including the one-active invariant.
public sealed class TaskOptimisticStateTests
{
    private static UserDesktopWidgetTaskInfo Task(long id, string state, int extended = 0)
        => new(id, $"task {id}", PlannedSeconds: 1800, State: state,
            ActiveSeconds: 0, FocusedSeconds: 0, OfftaskSeconds: 0, ExtendedSeconds: extended);

    [Fact]
    public void Start_MakesTaskActive_AndPausesAnyOtherActive()
    {
        var tasks = new[] { Task(1, "active"), Task(2, "pending") };

        var next = UserDesktopWidgetWindow.ComputeOptimisticTaskState("start", 2, tasks);

        Assert.Equal("pending", next[0].State); // one-active invariant: task 1 paused
        Assert.Equal("active", next[1].State);
    }

    [Theory]
    [InlineData("done", "done")]
    [InlineData("abandon", "abandoned")]
    public void TerminalActions_SetTerminalState(string kind, string expectedState)
    {
        var tasks = new[] { Task(7, "active") };

        var next = UserDesktopWidgetWindow.ComputeOptimisticTaskState(kind, 7, tasks);

        Assert.Equal(expectedState, next[0].State);
    }

    // Chat 7 (2026-08-24) — the "extend" arm is gone with the +15 that fed it, removed on
    // 17 August. What replaces this test is the guarantee that the verb is now INERT rather than
    // half-wired: an unknown verb must leave the list alone, which is what "extend" now is.
    [Fact]
    public void Extend_IsNoLongerAVerb_AndLeavesTheListUnchanged()
    {
        var tasks = new[] { Task(3, "active", extended: 0) };

        var next = UserDesktopWidgetWindow.ComputeOptimisticTaskState("extend", 3, tasks);

        Assert.Equal("active", next[0].State);
        Assert.Equal(0, next[0].ExtendedSeconds);
    }

    [Fact]
    public void UnknownIdOrEmpty_LeavesListUnchanged()
    {
        var tasks = new[] { Task(1, "pending") };
        Assert.Same(System.Array.Empty<UserDesktopWidgetTaskInfo>(),
            UserDesktopWidgetWindow.ComputeOptimisticTaskState("start", 1,
                System.Array.Empty<UserDesktopWidgetTaskInfo>()));

        var next = UserDesktopWidgetWindow.ComputeOptimisticTaskState("start", 999, tasks);
        Assert.Equal("pending", next[0].State);
    }
}
