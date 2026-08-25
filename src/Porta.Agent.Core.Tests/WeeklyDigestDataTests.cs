using System;
using System.Collections.Generic;
using System.Linq;
using Porta.Agent.Core;
using Xunit;

namespace Porta.Agent.Core.Tests;

// The rules behind the weekly digest's task blocks. SqliteBrainStateStore.Assemble is pure, so
// every rule here is tested without a database — which is the point of it being pure.
//
// The week under test is Mon 17 – Sun 23 August 2026, Dubai (+4).
public sealed class WeeklyDigestDataTests
{
    private const string From = "2026-08-17";
    private const string To = "2026-08-23";
    private static readonly DateTime WeekEnd = new(2026, 8, 23);
    private static readonly TimeSpan Dubai = TimeSpan.FromHours(4);

    /// <summary>A UTC stamp that lands on the given Dubai date at midday — safely inside the goal day.</summary>
    private static string Midday(int month, int day)
        => new DateTimeOffset(new DateTime(2026, month, day, 12, 0, 0), Dubai).ToUniversalTime().ToString("O");

    private static SqliteBrainStateStore.WeeklyTaskFacts Task(
        long id, string title, string state, string created,
        string completed = "", string ended = "", string project = "")
        => new(id, title, state, created, completed, ended, project);

    private static AgentWeeklyDigestData Run(
        IEnumerable<SqliteBrainStateStore.WeeklyTaskFacts> tasks,
        Dictionary<long, double>? seconds = null)
        => SqliteBrainStateStore.Assemble(
            tasks.ToList(), seconds ?? new Dictionary<long, double>(), From, To, WeekEnd, Dubai);

    [Fact]
    public void ScoresPlannedAndFinishedOverTheWholeWeek()
    {
        AgentWeeklyDigestData data = Run(
            new[]
            {
                Task(1, "Closed it", "done", Midday(8, 18), completed: Midday(8, 20), project: "PORTA"),
                Task(2, "Still going", "pending", Midday(8, 18), project: "PORTA"),
                Task(3, "Also closed", "done", Midday(8, 19), completed: Midday(8, 21), project: "Admin"),
            },
            new Dictionary<long, double> { [1] = 3600, [2] = 1800, [3] = 900 });

        Assert.Equal(3, data.PlannedCount);
        Assert.Equal(2, data.FinishedCount);
        Assert.Equal(2, data.Finished.Count);
        Assert.Single(data.Carried);
    }

    [Fact]
    public void OnlyStateDoneCountsAsFinished()
    {
        // The same rule as UserCheckInTaskBoard.SummarizeDay and the daily digest. "abandoned" is
        // closed, but it is not finished, and a weekly that counted it would flatter the week.
        AgentWeeklyDigestData data = Run(
            new[]
            {
                Task(1, "Gave up on it", "abandoned", Midday(8, 18), completed: Midday(8, 20), project: "PORTA"),
            },
            new Dictionary<long, double> { [1] = 600 });

        Assert.Equal(1, data.PlannedCount);
        Assert.Equal(0, data.FinishedCount);
        Assert.Empty(data.Finished);
    }

    [Fact]
    public void ATaskTheWeekNeverTouchedIsNotOnTheWeeksBoard()
    {
        // Created in May, never worked, still open. Counting it would make "planned" grow forever
        // and the score mean nothing.
        AgentWeeklyDigestData data = Run(new[] { Task(1, "Someday", "pending", Midday(5, 1), project: "PORTA") });

        Assert.Equal(0, data.PlannedCount);
        Assert.Empty(data.Carried);
    }

    [Fact]
    public void ATaskClosedBeforeTheWeekIsNotThisWeeksWork()
    {
        AgentWeeklyDigestData data = Run(
            new[] { Task(1, "Last week's win", "done", Midday(8, 5), completed: Midday(8, 10), project: "PORTA") });

        Assert.Equal(0, data.PlannedCount);
        Assert.Equal(0, data.FinishedCount);
        Assert.Empty(data.Finished);
    }

    [Fact]
    public void AnOldTaskWorkedThisWeekIsOnTheBoard()
    {
        AgentWeeklyDigestData data = Run(
            new[] { Task(1, "Long runner", "pending", Midday(5, 1), project: "PORTA") },
            new Dictionary<long, double> { [1] = 7200 });

        Assert.Equal(1, data.PlannedCount);
        Assert.Single(data.Carried);
    }

    [Fact]
    public void DaysOpenCountsFromTheDayItWasAdded_NotFromMonday()
    {
        // This is the number that separates stuck from slow, so it must not reset every Monday.
        AgentWeeklyDigestData data = Run(
            new[] { Task(1, "Dragging", "pending", Midday(8, 3), project: "PORTA") },
            new Dictionary<long, double> { [1] = 600 });

        // 3 Aug to 23 Aug inclusive.
        Assert.Equal(21, data.Carried.Single().DaysOpen);
    }

    [Fact]
    public void ATaskAddedOnTheLastDayHasBeenOpenOneDay_NotZero()
    {
        AgentWeeklyDigestData data = Run(
            new[] { Task(1, "Just added", "pending", Midday(8, 23), project: "PORTA") });

        Assert.Equal(1, data.Carried.Single().DaysOpen);
    }

    [Fact]
    public void UntaggedWorkIsShownOnItsLine_ButIsNeverAProjectRow()
    {
        AgentWeeklyDigestData data = Run(
            new[]
            {
                Task(1, "Loose end", "pending", Midday(8, 18), project: ""),
                Task(2, "Real work", "pending", Midday(8, 18), project: "PORTA"),
            },
            new Dictionary<long, double> { [1] = 3600, [2] = 7200 });

        Assert.Contains(data.Carried, l => l.Title == "Loose end" && l.ProjectName.Length == 0);
        Assert.Single(data.Projects);
        Assert.Equal("PORTA", data.Projects.Single().Name);
        Assert.DoesNotContain(data.Projects, p => p.Name is "Untagged" or "Other" or "");
    }

    [Fact]
    public void ProjectTimeIsSummedAcrossTasksAndOrderedLongestFirst()
    {
        AgentWeeklyDigestData data = Run(
            new[]
            {
                Task(1, "A", "pending", Midday(8, 18), project: "Admin"),
                Task(2, "B", "pending", Midday(8, 18), project: "PORTA"),
                Task(3, "C", "pending", Midday(8, 18), project: "PORTA"),
            },
            new Dictionary<long, double> { [1] = 3600, [2] = 5400, [3] = 1800 });

        Assert.Equal(2, data.Projects.Count);
        Assert.Equal("PORTA", data.Projects[0].Name);
        Assert.Equal(7200, data.Projects[0].Seconds);
        Assert.Equal("Admin", data.Projects[1].Name);
    }

    [Fact]
    public void AProjectWithNoMeasuredTimeThisWeekIsNotABarOfZero()
    {
        AgentWeeklyDigestData data = Run(
            new[] { Task(1, "Opened, never started", "pending", Midday(8, 18), project: "PORTA") });

        Assert.Empty(data.Projects);
    }

    [Fact]
    public void TheCountIsTheWholeWeekEvenWhenTheListIsCapped()
    {
        // The resume is the score; the list is what fits on a page. They are allowed to differ, and
        // the count must never shrink to the length of the list.
        var tasks = Enumerable.Range(1, 12)
            .Select(i => Task(i, "Task " + i, "done", Midday(8, 18), completed: Midday(8, 20), project: "PORTA"))
            .ToList();
        var seconds = tasks.ToDictionary(t => t.Id, t => (double)(t.Id * 600));

        AgentWeeklyDigestData data = Run(tasks, seconds);

        Assert.Equal(12, data.PlannedCount);
        Assert.Equal(12, data.FinishedCount);
        Assert.Equal(8, data.Finished.Count);
        // longest first
        Assert.Equal("Task 12", data.Finished[0].Title);
    }

    [Fact]
    public void FallsBackToEndedAtWhenCompletedAtWasNeverWritten()
    {
        // completed_at_utc is what the Tasks page writes; ended_at_utc is what the older close path
        // wrote. A task closed by the old path must not silently drop out of the week.
        AgentWeeklyDigestData data = Run(
            new[] { Task(1, "Closed the old way", "done", Midday(8, 18), completed: "", ended: Midday(8, 20), project: "PORTA") },
            new Dictionary<long, double> { [1] = 600 });

        Assert.Equal(1, data.FinishedCount);
        Assert.Single(data.Finished);
    }

    [Fact]
    public void SecondsAreTheWeeksOwn_NeverTheTasksLifetime()
    {
        // The dictionary IS the week's slice, read from task_day_hours over the seven days. A task
        // that has run for a month must not arrive claiming a month of hours against seven days.
        AgentWeeklyDigestData data = Run(
            new[] { Task(1, "Long runner", "pending", Midday(5, 1), project: "PORTA") },
            new Dictionary<long, double> { [1] = 5400 });

        Assert.Equal(5400, data.Carried.Single().Seconds);
    }

    [Fact]
    public void AnEmptyWeekIsEmpty_NotAZeroedOne()
    {
        AgentWeeklyDigestData data = Run(Array.Empty<SqliteBrainStateStore.WeeklyTaskFacts>());

        Assert.Equal(0, data.PlannedCount);
        Assert.Empty(data.Finished);
        Assert.Empty(data.Carried);
        Assert.Empty(data.Projects);
    }
}
