using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Step 3 — merging goals and tasks. The plumbing (LocalApi round-trips) lives in the host; the
// part that can be silently wrong lives here: how many tasks get created, whether re-confirming
// yesterday's unfinished work duplicates it, and what sentence daily_goals ends up holding.
public sealed class UserCheckInTaskPlannerTests
{
    private static readonly string[] NothingOpen = Array.Empty<string>();

    [Fact]
    public void Plan_KeepsUpToThree_AndRefusesTheFourthWithAReason()
    {
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[]
            {
                new UserCheckInTaskEntry("Pricing page", 3600),
                new UserCheckInTaskEntry("Client revisions", 7200),
                new UserCheckInTaskEntry("Invoices", 3600),
                new UserCheckInTaskEntry("Inbox", 3600),
            },
            NothingOpen);

        Assert.Equal(3, plan.Accepted.Count);
        SkippedMorningTask skipped = Assert.Single(plan.Skipped);
        Assert.Equal("Inbox", skipped.Title);
        Assert.Equal("over_max_tasks", skipped.ReasonCode);
    }

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
        MorningTaskPlan plan = UserCheckInTaskPlanner.Plan(
            new[]
            {
                new UserCheckInTaskEntry("A", 3600),
                new UserCheckInTaskEntry("B", 3600),
                new UserCheckInTaskEntry("C", 3600),
                new UserCheckInTaskEntry("D", 3600),
            },
            NothingOpen);

        Assert.Equal("A; B; C", plan.GoalText);
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
