using System.Text.Json;
using Porta.Agent.Core;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

// 2026-08-10 — the narrative used to be written from the morning sentence alone, which is never
// revised. So a task the user abandoned still read as today's plan, and Ari opened the next morning
// by proposing "writting" back — dropped the previous afternoon after 20 seconds of a planned 3h.
//
// These pin the outcomes onto the payload. The point of the last one is blunt: if a future change
// drops the task facts again, the suite fails rather than the user finding out from Ari.
public sealed class NarrativeTaskFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 7, 39, 0, TimeSpan.FromHours(4));

    [Fact]
    public void ProjectTasks_CarriesWhatHappened_NotJustTheTitle()
    {
        IReadOnlyList<NarrativeTaskPayload> facts = NarrativeGateway.ProjectTasks(
            new[] { Task(6, "writting", "abandoned", planned: 10800, active: 20d, ended: Now.AddHours(-18)) },
            NarrativeGateway.MaxNarrativeTasks)!;

        NarrativeTaskPayload only = Assert.Single(facts);
        Assert.Equal("writting", only.Title);
        Assert.Equal("abandoned", only.State);   // the field whose absence caused the defect
        Assert.Equal(10800, only.PlannedSeconds);
        Assert.Equal(20d, only.ActualSeconds);
    }

    [Fact]
    public void ProjectTasks_PutsOpenWorkFirst_ThenTheMostRecentlyClosed()
    {
        IReadOnlyList<NarrativeTaskPayload> facts = NarrativeGateway.ProjectTasks(
            new[]
            {
                Task(5, "keep improving the system", "done", ended: Now.AddHours(-18)),
                Task(7, "working on apollo", "abandoned", ended: Now.AddHours(-21)),
                Task(8, "continue to improve the system", "active"),
            },
            NarrativeGateway.MaxNarrativeTasks)!;

        Assert.Equal(
            new[] { "continue to improve the system", "keep improving the system", "working on apollo" },
            facts.Select(f => f.Title));
    }

    [Fact]
    public void ProjectTasks_IsCapped_SoTheUsersOwnWritingCannotRideUnbounded()
    {
        IReadOnlyList<NarrativeTaskPayload> facts = NarrativeGateway.ProjectTasks(
            Enumerable.Range(1, 40).Select(i => Task(i, $"task {i}", "done", ended: Now.AddMinutes(-i))).ToList(),
            NarrativeGateway.MaxNarrativeTasks)!;

        Assert.Equal(NarrativeGateway.MaxNarrativeTasks, facts.Count);
        // Truncation must lose the stalest facts, never the freshest.
        Assert.Equal("task 1", facts[0].Title);
    }

    [Fact]
    public void ProjectTasks_NothingToReport_IsNull_SoTheFactSheetOmitsTheSection()
    {
        Assert.Null(NarrativeGateway.ProjectTasks(null, NarrativeGateway.MaxNarrativeTasks));
        Assert.Null(NarrativeGateway.ProjectTasks(Array.Empty<AgentTask>(), NarrativeGateway.MaxNarrativeTasks));
        Assert.Null(NarrativeGateway.ProjectTasks(new[] { Task(1, "   ", "done") }, NarrativeGateway.MaxNarrativeTasks));
    }

    [Fact]
    public void ProjectTasks_ZeroCap_SendsNothing()
        => Assert.Null(NarrativeGateway.ProjectTasks(new[] { Task(1, "writting", "abandoned") }, 0));

    // The regression guard with teeth: the outcomes must survive serialization onto the wire under
    // the exact names the control plane reads. A rename or a dropped field fails here.
    [Fact]
    public void SituationPayload_SerializesTheTaskOutcomes_UnderTheAgreedNames()
    {
        var situation = new NarrativeSituationPayload(
            GoalToday: "continue to improve the system; continue writting",
            CarriedPlan: null,
            MiddayAnswer: null,
            TrackedSeconds: 0, FocusSeconds: 0, FocusQualityPct: 0,
            DistractionSeconds: 0, AwaySeconds: 0, LongestStreakSeconds: 0,
            TopApps: null, LocalTimeHHmm: "07:39", UserText: null,
            Tasks: NarrativeGateway.ProjectTasks(
                new[] { Task(6, "writting", "abandoned", planned: 10800, active: 20d, ended: Now.AddHours(-18)) },
                NarrativeGateway.MaxNarrativeTasks));

        string json = JsonSerializer.Serialize(situation);

        Assert.Contains("\"tasks\":", json, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"writting\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"abandoned\"", json, StringComparison.Ordinal);
        Assert.Contains("\"plannedSeconds\":10800", json, StringComparison.Ordinal);
        Assert.Contains("\"actualSeconds\":20", json, StringComparison.Ordinal);
        // Agreed with Yadin 2026-08-10: off-task seconds do NOT ride this path. The line needs to
        // know a task was dropped, not how its minutes were split.
        Assert.DoesNotContain("offtask", json, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentTask Task(
        long id,
        string title,
        string state,
        int planned = 3600,
        double active = 0d,
        DateTimeOffset? ended = null)
        => new(
            Id: id,
            Title: title,
            PlannedSeconds: planned,
            State: state,
            OrderIndex: (int)id,
            CreatedAtUtc: Now.AddDays(-1),
            StartedAtUtc: null,
            EndedAtUtc: ended,
            ActiveSeconds: active,
            FocusedSeconds: active,
            OfftaskSeconds: 0d,
            ExtendedSeconds: 0);
}
