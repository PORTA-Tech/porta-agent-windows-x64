using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Step 3 — merging goals and tasks. The plumbing (LocalApi round-trips) lives in the host; the
// part that can be silently wrong lives here: how many tasks get created, whether re-confirming
// yesterday's unfinished work duplicates it, and what sentence daily_goals ends up holding.
public sealed class UserCheckInTaskPlannerTests
{
    private static readonly string[] NothingOpen = Array.Empty<string>();

    [Fact]
    // Chat 7 (2026-08-24) — the ceiling was three and is now six. Written against the CONSTANT
    // rather than a literal, so the next change to it does not need this test rewritten again;
    // what is actually guarded is that the ceiling holds and that the overflow is REFUSED WITH A
    // REASON rather than dropped silently.
    public void Plan_KeepsUpToTheCeiling_AndRefusesTheNextWithAReason()
    {
        var entries = Enumerable
            .Range(1, UserCheckInTaskPlanner.MaxMorningTasks + 1)
            .Select(i => new UserCheckInTaskEntry($"Task {i}", 3600))
            .ToArray();

        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(entries, NothingOpen);

        Assert.Equal(UserCheckInTaskPlanner.MaxMorningTasks, plan.Accepted.Count);
        SkippedMorningTask skipped = Assert.Single(plan.Skipped);
        Assert.Equal($"Task {UserCheckInTaskPlanner.MaxMorningTasks + 1}", skipped.Title);
        Assert.Equal("over_max_tasks", skipped.ReasonCode);
    }

    // Six, not three. Asserted directly so raising it again is a deliberate act with a failing
    // test attached, rather than something that drifts.
    [Fact]
    public void TheMorningCeilingIsSix()
        => Assert.Equal(6, UserCheckInTaskPlanner.MaxMorningTasks);

    [Fact]
    public void Plan_PreservesTheOrderTyped_SoTheFirstEntryIsTheOneAutoStarted()
    {
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[]
            {
                new UserCheckInTaskEntry("Pricing page", 7200),
                new UserCheckInTaskEntry("Client revisions", 3600),
            },
            NothingOpen);

        Assert.Equal("Pricing page", plan.Accepted[0].Title);
        Assert.Equal(7200, plan.Accepted[0].PlannedSeconds);
        Assert.Equal("Client revisions", plan.Accepted[1].Title);
    }

    [Fact]
    public void Plan_SkipsEmptyRowsSilently_BecauseAnUnfilledRowIsNotAnError()
    {
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[]
            {
                new UserCheckInTaskEntry("Pricing page", 3600),
                new UserCheckInTaskEntry("   ", 3600),
                new UserCheckInTaskEntry(string.Empty, 3600),
            },
            NothingOpen);

        Assert.Single(plan.Accepted);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_MarksWorkThatIsAlreadyOpen_SoCarryForwardDoesNotDuplicateIt()
    {
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[]
            {
                new UserCheckInTaskEntry("Pricing page", 3600),
                new UserCheckInTaskEntry("Client revisions", 3600),
            },
            new[] { "pricing page" });   // case-insensitive: the store keeps whatever was typed

        Assert.True(plan.Accepted[0].AlreadyOpen);
        Assert.False(plan.Accepted[1].AlreadyOpen);
        // Still part of today's plan — it is re-confirmed work, not a refusal.
        Assert.Equal(2, plan.Accepted.Count);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Plan_RefusesTheSameTitleTwiceInOneList()
    {
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[]
            {
                new UserCheckInTaskEntry("Pricing page", 3600),
                new UserCheckInTaskEntry("  Pricing Page  ", 7200),
            },
            NothingOpen);

        Assert.Single(plan.Accepted);
        Assert.Equal("duplicate_in_list", Assert.Single(plan.Skipped).ReasonCode);
    }

    // CreateTask returns null under 60s or over 8h (SqliteBrainStateStore.cs:2154). The chips can
    // only produce legal values, so this guards a malformed caller, not the UI.
    [Theory]
    [InlineData(0, UserCheckInTaskPlanner.DefaultPlannedSeconds)]
    [InlineData(59, UserCheckInTaskPlanner.DefaultPlannedSeconds)]
    [InlineData(-1, UserCheckInTaskPlanner.DefaultPlannedSeconds)]
    [InlineData(60, 60)]
    [InlineData(9 * 3600, UserCheckInTaskPlanner.MaxPlannedSeconds)]
    public void Plan_ClampsDurationsIntoTheRangeTheStoreAccepts(int given, int expected)
    {
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[] { new UserCheckInTaskEntry("Pricing page", given) }, NothingOpen);

        Assert.Equal(expected, plan.Accepted[0].PlannedSeconds);
    }

    [Fact]
    public void Plan_GoalTextIsTheProjectionOfTheAcceptedTitles()
    {
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[]
            {
                new UserCheckInTaskEntry("Pricing page", 3600),
                new UserCheckInTaskEntry("Client revisions", 3600),
            },
            NothingOpen);

        // This is the sentence daily_goals stores and the narrative fact sheet, daily-stats upload
        // and digest goal line all read. It must be derived from the tasks, never authored apart
        // from them — that separation is the whole defect step 3 exists to remove.
        Assert.Equal("Pricing page; Client revisions", plan.GoalText);
    }

    [Fact]
    public void Plan_GoalTextExcludesRefusedEntries()
    {
        // Chat 7 — one past the ceiling, whatever the ceiling is. The goal sentence must carry
        // exactly what was accepted and nothing that was refused.
        var entries = Enumerable
            .Range(1, UserCheckInTaskPlanner.MaxMorningTasks + 1)
            .Select(i => new UserCheckInTaskEntry($"T{i}", 3600))
            .ToArray();

        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(entries, NothingOpen);

        string expected = string.Join("; ",
            Enumerable.Range(1, UserCheckInTaskPlanner.MaxMorningTasks).Select(i => $"T{i}"));
        Assert.Equal(expected, plan.GoalText);
    }

    [Fact]
    public void Plan_NoEntries_AcceptsNothing_SoTheHostCanTreatItAsAnEmptyAnswer()
    {
        Assert.Empty(UserCheckInTaskPlanner.Plan(Array.Empty<UserCheckInTaskEntry>(), NothingOpen).Accepted);
        Assert.Empty(UserCheckInTaskPlanner.Plan(null, null).Accepted);
    }

    // A response that carries no task entries at all is the pre-step-3 shape. It must still record
    // the day rather than being dropped.
    [Fact]
    public void SingleEntryFrom_TurnsAPlainGoalSentenceIntoOneDefaultLengthTask()
    {
        IReadOnlyList<UserCheckInTaskEntry> entries = UserCheckInTaskPlanner.SingleEntryFrom("  Pricing page  ");

        UserCheckInTaskEntry entry = Assert.Single(entries);
        Assert.Equal("Pricing page", entry.Title);
        Assert.Equal(UserCheckInTaskPlanner.DefaultPlannedSeconds, entry.PlannedSeconds);
    }

    [Fact]
    public void SingleEntryFrom_BlankGoal_ProducesNothing()
    {
        Assert.Empty(UserCheckInTaskPlanner.SingleEntryFrom("   "));
        Assert.Empty(UserCheckInTaskPlanner.SingleEntryFrom(null));
    }
}
