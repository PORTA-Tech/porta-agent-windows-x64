using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Phase 5 (2026-08-24) - on the evening card the RESUME sentence is the message, and it outranks
// the LLM line.
//
// Everywhere else the written line is the better copy. Here the card is about to ask the user to
// confirm a whole day, and the sentence they need first is the one that can be checked against the
// board underneath it. Reflective prose in its place reads well and says nothing.
public sealed class UserCheckInEveningResumeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 17, 30, 0, TimeSpan.FromHours(4));

    private static TaskBoardLine Line(long id, string title, string state, double measuredSeconds)
        => new(id, title, 3600, measuredSeconds, state, 0, Now);

    // Two done, one still open - the day the mockup shows.
    private static IReadOnlyList<TaskBoardLine> MixedDay =>
    [
        Line(1, "Rewrite the onboarding email", "done", 5100d),
        Line(2, "Send the March invoices", "done", 2400d),
        Line(3, "Landing page copy", "pending", 7800d),
    ];

    // HasProcessedTimeline false = no measured line, which keeps these tests about the message.
    private static LocalTodaySummaryMetrics NoMetrics => new(
        HasProcessedTimeline: false,
        FocusedTodaySeconds: 0d,
        FocusQualityPercent: 0d,
        DistractionTodaySeconds: 0d,
        AwayTodaySeconds: 0d,
        ContextExcursionTodaySeconds: 0d,
        TrackedTodaySeconds: 0d,
        DisplayedTrackedTodaySeconds: 0d,
        MostDistractedHourLabel: string.Empty);

    private static UserCheckInPrompt Evening(string? narrativeLine, IReadOnlyList<TaskBoardLine>? board)
        => UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay,
            goal: null,
            metrics: NoMetrics,
            carriedPlan: null,
            narrativeLine: narrativeLine,
            middaySkipped: false,
            awayGap: false,
            board: board);

    [Fact]
    public void TheResumeIsTheMessage_WhenThereIsABoard()
        => Assert.Equal(
            "You planned three things. You finished two.",
            Evening(narrativeLine: null, MixedDay).Message);

    [Fact]
    public void TheResumeBeatsTheLlmLine()
    {
        UserCheckInPrompt prompt = Evening(
            narrativeLine: "What a day that was. You should be proud of the shape it took.",
            MixedDay);

        Assert.Equal("You planned three things. You finished two.", prompt.Message);
    }

    // A day with no tasks has nothing to resume, so that card is deliberately left exactly as it
    // was - including its LLM line. This is the one branch phase 5 does not touch.
    [Fact]
    public void ADayWithNoTasksKeepsItsOldBehaviour()
    {
        UserCheckInPrompt prompt = Evening(narrativeLine: "A quiet one.", board: []);
        Assert.Equal("A quiet one.", prompt.Message);
    }

    // The measured line is its own block and must not be displaced by any of the above.
    [Fact]
    public void TheOtherEveningCardCopyIsUnchanged()
    {
        UserCheckInPrompt prompt = Evening(narrativeLine: null, MixedDay);

        Assert.Equal("Wrapping up", prompt.Title);
        Assert.Equal("What moves to tomorrow?", prompt.QuestionLabel);
        Assert.Equal(3, prompt.Tasks!.Count);
    }

    // The midday cards are not the evening card: the LLM line still wins there, and phase 5 must
    // not have leaked into them.
    [Fact]
    public void TheMiddayCardStillPrefersTheLlmLine()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Midday,
            goal: null,
            metrics: NoMetrics,
            carriedPlan: null,
            narrativeLine: "Halfway, and the hard one is still open.",
            board: MixedDay);

        Assert.Equal("Halfway, and the hard one is still open.", prompt.Message);
    }
}
