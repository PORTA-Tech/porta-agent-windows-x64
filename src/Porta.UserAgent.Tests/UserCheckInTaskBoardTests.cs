using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Steps 4-7 — the rules behind proposing from unfinished work, one-tap done, carry-forward and the
// evening wrap. All pure, so the parts that are easy to get quietly wrong (which day a task belongs
// to, what the day added up to) are tested without a clock or a card.
public sealed class UserCheckInTaskBoardTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.FromHours(4));

    [Fact]
    public void Propose_OffersOpenWork_OldestFirst()
    {
        IReadOnlyList<TaskBoardLine> proposal = UserCheckInTaskBoard.Propose(
            new[]
            {
                Task(1, "Invoices", "pending", created: Noon.AddDays(-1)),
                Task(2, "Pricing page", "pending", created: Noon.AddDays(-4)),
                Task(3, "Client revisions", "pending", created: Noon),
            },
            Noon);

        // The thing that has been waiting longest is the thing to ask about first.
        Assert.Equal(new[] { "Pricing page", "Invoices", "Client revisions" }, proposal.Select(p => p.Title));
    }

    [Fact]
    public void Propose_SkipsFinishedWork()
    {
        IReadOnlyList<TaskBoardLine> proposal = UserCheckInTaskBoard.Propose(
            new[]
            {
                Task(1, "Pricing page", "done"),
                Task(2, "Invoices", "abandoned"),
                Task(3, "Client revisions", "active"),
            },
            Noon);

        Assert.Equal("Client revisions", Assert.Single(proposal).Title);
    }

    [Fact]
    public void Propose_NeverOffersMoreThanTheMorningCardCanHold()
    {
        IReadOnlyList<TaskBoardLine> proposal = UserCheckInTaskBoard.Propose(
            Enumerable.Range(1, 8).Select(i => Task(i, $"Task {i}", "pending")).ToList(),
            Noon);

        Assert.Equal(UserCheckInTaskBoard.MaxProposed, proposal.Count);
    }

    [Fact]
    public void Propose_NothingOpen_ProposesNothing_SoTheMorningFallsBackToAsking()
    {
        Assert.Empty(UserCheckInTaskBoard.Propose(new[] { Task(1, "Pricing page", "done") }, Noon));
        Assert.Empty(UserCheckInTaskBoard.Propose(null, Noon));
    }

    // The 05:00 goal-day boundary, not 24-hour blocks. A task created late last night and still
    // open this morning has carried one day, not zero.
    [Theory]
    [InlineData(-1, 0, 1)]    // yesterday noon -> today noon
    [InlineData(0, 0, 0)]     // created today
    [InlineData(-3, 0, 3)]
    [InlineData(0, -13, 1)]   // created 23:00 last night, now 12:00 -> one goal day
    public void DaysCarried_CountsGoalDays(int dayOffset, int hourOffset, int expected)
    {
        DateTimeOffset created = Noon.AddDays(dayOffset).AddHours(hourOffset);

        Assert.Equal(expected, UserCheckInTaskBoard.DaysCarried(created, Noon));
    }

    [Fact]
    public void DaysCarried_NeverNegative_ForAClockThatWentBackwards()
        => Assert.Equal(0, UserCheckInTaskBoard.DaysCarried(Noon.AddDays(2), Noon));

    [Theory]
    [InlineData(3, 2, "You planned three things. You finished two.")]
    [InlineData(3, 0, "You planned three things. You finished none of them.")]
    [InlineData(3, 3, "You planned three things. You finished them all.")]
    [InlineData(1, 1, "You planned one thing. You finished it.")]
    [InlineData(1, 0, "You planned one thing. You didn't get to it.")]
    [InlineData(2, 1, "You planned two things. You finished one.")]
    public void SummarizeDay_ScoresTheDayInPlainWords(int planned, int finished, string expected)
    {
        var board = new List<TaskBoardLine>();
        for (int i = 0; i < planned; i++)
        {
            board.Add(Line(i + 1, $"Task {i}", i < finished ? "done" : "pending"));
        }

        Assert.Equal(expected, UserCheckInTaskBoard.SummarizeDay(board));
    }

    [Fact]
    public void SummarizeDay_NothingPlanned_InventsNoSummary()
        => Assert.Equal(string.Empty, UserCheckInTaskBoard.SummarizeDay(Array.Empty<TaskBoardLine>()));

    // Step 5 — the midday answer is derived from what was ticked, not asked as a second question,
    // so daily_goals stays a view over the tasks and the existing ground-truth label keeps working.
    [Theory]
    [InlineData(new[] { "done", "done" }, "yes")]
    [InlineData(new[] { "done", "not_yet" }, "partly")]
    [InlineData(new[] { "not_yet", "not_yet" }, "not_yet")]
    [InlineData(new[] { "done" }, "yes")]
    public void MiddayAnswerFor_DerivesTheAnswerFromTheTicks(string[] actions, string expected)
    {
        IReadOnlyList<UserCheckInTaskOutcome> outcomes = actions
            .Select((a, i) => new UserCheckInTaskOutcome(i + 1, a))
            .ToList();

        Assert.Equal(expected, UserCheckInTaskBoard.MiddayAnswerFor(outcomes));
    }

    [Fact]
    public void MiddayAnswerFor_NoOutcomes_IsEmpty_SoTheCallerCanSkipTheWrite()
    {
        Assert.Equal(string.Empty, UserCheckInTaskBoard.MiddayAnswerFor(null));
        Assert.Equal(string.Empty, UserCheckInTaskBoard.MiddayAnswerFor(Array.Empty<UserCheckInTaskOutcome>()));
    }

    // Step 6 — said once, about the worst offender, plainly. Not a nag.
    [Fact]
    public void CarriedNotice_SpeaksOnlyAboutWorkThatHasCarriedLongEnough()
    {
        string notice = UserCheckInTaskBoard.CarriedNotice(new[]
        {
            Line(1, "Invoices", "pending", daysCarried: 1),
            Line(2, "Pricing page", "pending", daysCarried: 4),
            Line(3, "Client revisions", "pending", daysCarried: 3),
        });

        Assert.Equal("“Pricing page” has moved forward 4 days running.", notice);
    }

    [Fact]
    public void CarriedNotice_BelowTheThreshold_SaysNothing()
    {
        Assert.Equal(string.Empty, UserCheckInTaskBoard.CarriedNotice(new[]
        {
            Line(1, "Invoices", "pending", daysCarried: 2),
        }));
    }

    [Fact]
    public void CarriedNotice_IgnoresFinishedWork_HoweverOldItIs()
    {
        Assert.Equal(string.Empty, UserCheckInTaskBoard.CarriedNotice(new[]
        {
            Line(1, "Pricing page", "done", daysCarried: 9),
        }));
    }

    [Fact]
    public void BuildBoard_CarriesTheMeasuredTime_NotTheWallClock()
    {
        TaskBoardLine line = Assert.Single(UserCheckInTaskBoard.BuildBoard(
            new[] { Task(1, "Pricing page", "active", activeSeconds: 4320d) }, Noon));

        Assert.Equal(4320d, line.MeasuredSeconds);
        Assert.Equal("1h12m", line.MeasuredLabel);   // the repo's existing duration format
    }

    [Fact]
    public void BuildBoard_DropsBlankTitles_RatherThanRenderingAnEmptyRow()
        => Assert.Empty(UserCheckInTaskBoard.BuildBoard(new[] { Task(1, "   ", "pending") }, Noon));

    private static LocalApiTask Task(
        long id,
        string title,
        string state,
        DateTimeOffset? created = null,
        double activeSeconds = 0d)
        => new(
            Id: id,
            Title: title,
            PlannedSeconds: 3600,
            State: state,
            ActiveSeconds: activeSeconds,
            FocusedSeconds: activeSeconds,
            OfftaskSeconds: 0d,
            ExtendedSeconds: 0,
            CreatedAtUtc: (created ?? Noon).ToUniversalTime(),
            StartedAtUtc: null,
            EndedAtUtc: null);

    private static TaskBoardLine Line(long id, string title, string state, int daysCarried = 0)
        => new(id, title, PlannedSeconds: 3600, MeasuredSeconds: 0d, State: state, DaysCarried: daysCarried);
}
