using Porta.Agent.LocalApi;
using Porta.ControlCenter;

namespace Porta.ControlCenter.Tests;

// Chat 4 (2026-08-24) — the Tasks page tabs.
//
// The bug: moving a task from Upcoming to In progress put it in NEITHER tab. It could not enter
// In progress, which gated on measured hours an upcoming task does not have, and it could not
// leave Upcoming, whose predicate looked only at dates. Moves to done and blocked landed fine,
// which is exactly why the move itself looked like it worked.
public sealed class TaskFilterRulesTests
{
    private static readonly DateTime Today = new(2026, 08, 24);

    private static LocalApiTaskRow Row(
        string status,
        double hours = 0d,
        string expectedFinishDay = "",
        string deferredUntilDay = "",
        string lastWorkedDay = "")
        => new(
            TaskId: 1,
            Label: "landing page copy",
            Status: status,
            ProjectName: "Northbridge",
            IsUntagged: false,
            Dates: string.Empty,
            Hours: hours,
            ColourSlot: 1,
            ResultLine: string.Empty,
            LastWorkedDay: lastWorkedDay,
            ExpectedFinishDay: expectedFinishDay,
            DeferredUntilDay: deferredUntilDay);

    // ── the move that used to vanish ──────────────────────────────────────────────────

    // An upcoming task, moved to In progress. It has no hours yet — that is the whole point.
    [Fact]
    public void AMovedTaskLandsInInProgress_EvenWithNoHoursYet()
        => Assert.True(TaskFilterRules.Matches(
            TaskFilterRules.InProgress,
            Row("in_progress", hours: 0d, expectedFinishDay: "2026-08-30"),
            Today));

    [Fact]
    public void AMovedTaskLeavesUpcoming()
        => Assert.False(TaskFilterRules.Matches(
            TaskFilterRules.Upcoming,
            Row("in_progress", hours: 0d, expectedFinishDay: "2026-08-30"),
            Today));

    // Before the fix this task was in Upcoming AND absent from In progress. Now it is in exactly
    // one tab, which is the assertion that matters.
    [Fact]
    public void AMovedTaskIsInExactlyOneOfTheTwoTabs()
    {
        LocalApiTaskRow moved = Row("in_progress", hours: 0d, expectedFinishDay: "2026-08-30");

        bool inProgress = TaskFilterRules.Matches(TaskFilterRules.InProgress, moved, Today);
        bool upcoming = TaskFilterRules.Matches(TaskFilterRules.Upcoming, moved, Today);

        Assert.True(inProgress ^ upcoming, "the task must appear in one tab, not both and not neither");
        Assert.True(inProgress);
    }

    // ── upcoming ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlannedTaskWithAFutureDateIsUpcoming()
        => Assert.True(TaskFilterRules.Matches(
            TaskFilterRules.Upcoming, Row("planned", expectedFinishDay: "2026-08-30"), Today));

    [Fact]
    public void APostponedTaskIsUpcoming()
        => Assert.True(TaskFilterRules.Matches(
            TaskFilterRules.Upcoming, Row("planned", deferredUntilDay: "2026-08-25"), Today));

    [Fact]
    public void ABlockedTaskIsNotUpcoming()
        => Assert.False(TaskFilterRules.Matches(
            TaskFilterRules.Upcoming, Row("blocked", expectedFinishDay: "2026-08-30"), Today));

    [Fact]
    public void ADoneTaskIsNotUpcoming()
        => Assert.False(TaskFilterRules.Matches(
            TaskFilterRules.Upcoming, Row("done", expectedFinishDay: "2026-08-30"), Today));

    // ── in progress ───────────────────────────────────────────────────────────────────

    [Fact]
    public void APlannedTaskIsNotInProgress_HoursOrNot()
    {
        Assert.False(TaskFilterRules.Matches(TaskFilterRules.InProgress, Row("planned"), Today));
        Assert.False(TaskFilterRules.Matches(
            TaskFilterRules.InProgress, Row("planned", hours: 3d), Today));
    }

    [Fact]
    public void ADoneTaskIsNeverInProgress()
        => Assert.False(TaskFilterRules.Matches(
            TaskFilterRules.InProgress, Row("done", hours: 5d), Today));

    [Fact]
    public void ABlockedTaskIsNeverInProgress()
        => Assert.False(TaskFilterRules.Matches(
            TaskFilterRules.InProgress, Row("blocked", hours: 5d), Today));

    // The fallback: rows written before the status column existed still show up on their hours.
    [Fact]
    public void ALegacyRowWithNoStatusButRealHoursStillShows()
    {
        Assert.True(TaskFilterRules.Matches(TaskFilterRules.InProgress, Row("", hours: 2d), Today));
        Assert.False(TaskFilterRules.Matches(TaskFilterRules.InProgress, Row("", hours: 0d), Today));
    }

    // ── the tabs that were already right, kept right ──────────────────────────────────

    [Fact]
    public void BlockedAndDoneAreUnchanged()
    {
        Assert.True(TaskFilterRules.Matches(TaskFilterRules.Blocked, Row("blocked"), Today));
        Assert.False(TaskFilterRules.Matches(TaskFilterRules.Blocked, Row("planned"), Today));
        Assert.True(TaskFilterRules.Matches(TaskFilterRules.Done, Row("done"), Today));
        Assert.False(TaskFilterRules.Matches(TaskFilterRules.Done, Row("in_progress"), Today));
    }

    [Fact]
    public void AllShowsEverything()
    {
        foreach (string status in new[] { "planned", "in_progress", "blocked", "done", "" })
        {
            Assert.True(TaskFilterRules.Matches(TaskFilterRules.All, Row(status), Today));
        }
    }

    [Fact]
    public void ThisWeekCoversWorkInTheLastSevenDaysAndAnythingStillOpen()
    {
        Assert.True(TaskFilterRules.Matches(
            TaskFilterRules.Week, Row("done", lastWorkedDay: "2026-08-20"), Today));
        Assert.False(TaskFilterRules.Matches(
            TaskFilterRules.Week, Row("done", lastWorkedDay: "2026-08-01"), Today));

        // Still open counts even with no work recorded against it yet.
        Assert.True(TaskFilterRules.Matches(TaskFilterRules.Week, Row("planned"), Today));
    }

    // A postponed task is not "still open" today — it is parked for a later day.
    [Fact]
    public void APostponedTaskIsNotCountedAsStillOpen()
        => Assert.False(TaskFilterRules.IsOpen(
            Row("planned", deferredUntilDay: "2026-08-30"), Today));
}
