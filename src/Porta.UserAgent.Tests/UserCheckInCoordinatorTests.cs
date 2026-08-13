using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserCheckInCoordinatorTests
{
    [Fact]
    public void BuildSnapshot_NullGoal_IsAFreshDayWithNoGoal()
    {
        DailyGoalSnapshot snapshot = UserCheckInCoordinator.BuildSnapshot(null);

        Assert.False(snapshot.MorningRecorded);
        Assert.False(snapshot.MiddayRecorded);
        Assert.False(snapshot.EndOfDayRecorded);
        Assert.Null(snapshot.MorningAtLocal);
    }

    [Fact]
    public void BuildSnapshot_MorningGoalStated_RecordsMorningAndTimestamp()
    {
        DateTimeOffset createdUtc = DateTimeOffset.Parse("2026-03-08T08:15:00Z");
        var goal = new LocalApiDailyGoal(
            LocalDate: "2026-03-08",
            GoalText: "Ship the release",
            TrackingPref: "keep_on_track",
            MiddayAnswer: "",
            PlanTomorrow: "",
            CreatedAtUtc: createdUtc,
            MiddayAtUtc: null,
            EndOfDayAtUtc: null);

        DailyGoalSnapshot snapshot = UserCheckInCoordinator.BuildSnapshot(goal);

        Assert.True(snapshot.MorningRecorded);
        Assert.False(snapshot.MiddayRecorded);
        Assert.False(snapshot.EndOfDayRecorded);
        Assert.Equal(createdUtc.ToLocalTime(), snapshot.MorningAtLocal);
    }

    [Fact]
    public void BuildSnapshot_MiddayAnswered_RecordsMidday()
    {
        var goal = new LocalApiDailyGoal(
            LocalDate: "2026-03-08",
            GoalText: "Ship the release",
            TrackingPref: "keep_on_track",
            MiddayAnswer: "partly",
            PlanTomorrow: "",
            CreatedAtUtc: DateTimeOffset.Parse("2026-03-08T08:15:00Z"),
            MiddayAtUtc: DateTimeOffset.Parse("2026-03-08T12:30:00Z"),
            EndOfDayAtUtc: null);

        DailyGoalSnapshot snapshot = UserCheckInCoordinator.BuildSnapshot(goal);

        Assert.True(snapshot.MorningRecorded);
        Assert.True(snapshot.MiddayRecorded);
        Assert.False(snapshot.EndOfDayRecorded);
    }

    [Fact]
    public void BuildSnapshot_EndOfDayWithoutMorningGoal_ReadsAsNoMorning()
    {
        // The EOD check-in fires even when the user never set a morning goal, so a row can exist
        // with an empty goal_text but eod_at_utc set. That must NOT count as a stated morning goal.
        var goal = new LocalApiDailyGoal(
            LocalDate: "2026-03-08",
            GoalText: "",
            TrackingPref: "",
            MiddayAnswer: "",
            PlanTomorrow: "Rest",
            CreatedAtUtc: DateTimeOffset.Parse("2026-03-08T20:00:00Z"),
            MiddayAtUtc: null,
            EndOfDayAtUtc: DateTimeOffset.Parse("2026-03-08T20:00:00Z"));

        DailyGoalSnapshot snapshot = UserCheckInCoordinator.BuildSnapshot(goal);

        Assert.False(snapshot.MorningRecorded);
        Assert.Null(snapshot.MorningAtLocal);
        Assert.True(snapshot.EndOfDayRecorded);
    }

    [Fact]
    public void BuildReportLine_NoProcessedTimeline_IsEmpty()
    {
        var metrics = new LocalTodaySummaryMetrics(
            HasProcessedTimeline: false,
            FocusedTodaySeconds: 0,
            FocusQualityPercent: 0,
            DistractionTodaySeconds: 0,
            AwayTodaySeconds: 0,
            ContextExcursionTodaySeconds: 0,
            TrackedTodaySeconds: 0,
            DisplayedTrackedTodaySeconds: 0,
            MostDistractedHourLabel: "Not available");

        Assert.Equal(string.Empty, UserCheckInCoordinator.BuildReportLine(metrics));
    }

    [Fact]
    public void BuildReportLine_WithMetrics_SummarizesFocusOffTrackAndAway()
    {
        var metrics = new LocalTodaySummaryMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: 2 * 3600 + 30 * 60,   // 2h30m of focus-episode time
            FocusQualityPercent: 53,
            DistractionTodaySeconds: 20 * 60,           // 20m
            AwayTodaySeconds: 45 * 60,                   // 45m
            ContextExcursionTodaySeconds: 0,
            TrackedTodaySeconds: 3 * 3600,
            DisplayedTrackedTodaySeconds: 3 * 3600,
            MostDistractedHourLabel: "14:00-14:59");

        string line = UserCheckInCoordinator.BuildReportLine(metrics);

        Assert.Contains("2h30m focused", line, StringComparison.Ordinal);
        Assert.Contains("53% focus quality", line, StringComparison.Ordinal);
        Assert.Contains("20m off-track", line, StringComparison.Ordinal);
        Assert.Contains("45m away", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReportLine_ZeroQuality_OmitsTheQualitySegment()
    {
        var metrics = new LocalTodaySummaryMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: 0,
            FocusQualityPercent: 0,
            DistractionTodaySeconds: 10 * 60,
            AwayTodaySeconds: 0,
            ContextExcursionTodaySeconds: 0,
            TrackedTodaySeconds: 3600,
            DisplayedTrackedTodaySeconds: 3600,
            MostDistractedHourLabel: "Not available");

        string line = UserCheckInCoordinator.BuildReportLine(metrics);

        Assert.DoesNotContain("focus quality", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Morning_AsksAFreshQuestionWithNoReport()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics());

        Assert.Equal(UserCheckInKind.Morning, prompt.Kind);
        Assert.Equal(string.Empty, prompt.ReportLine);
        Assert.Equal(string.Empty, prompt.PrefillGoalText);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Message));
    }

    // Part 3 — memory: last night's plan opens the morning card (confirm/edit), not a blank ask.
    [Fact]
    public void BuildPrompt_Morning_WithCarriedPlan_OpensWithLastNightsPlan()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(), carriedPlan: "Finish the report");

        Assert.Equal(UserCheckInKind.Morning, prompt.Kind);
        Assert.Contains("Last night you planned", prompt.Message, StringComparison.Ordinal);
        Assert.Contains("Finish the report", prompt.Message, StringComparison.Ordinal);
        Assert.Equal("Finish the report", prompt.PrefillGoalText);
    }

    [Fact]
    public void BuildPrompt_Morning_WhitespaceCarriedPlan_FallsBackToFreshAsk()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(), carriedPlan: "   ");

        Assert.DoesNotContain("Last night you planned", prompt.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, prompt.PrefillGoalText);
    }

    // Part 5 — the LLM line becomes the card message; everything else (title, prefill, report
    // numbers) stays exactly as the template card would show it.
    [Fact]
    public void BuildPrompt_WithNarrativeLine_UsesItAsTheMessage()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Midday, goal: null, EmptyMetrics(),
            narrativeLine: "You said the API was the day — protect the next hour for it.");

        Assert.Equal("You said the API was the day — protect the next hour for it.", prompt.Message);
        Assert.Equal("How's it going?", prompt.Title);
    }

    [Fact]
    public void BuildPrompt_MorningWithCarriedPlanAndNarrative_KeepsThePrefill()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(),
            carriedPlan: "Finish the report",
            narrativeLine: "Last night you set tomorrow up — still the move?");

        Assert.Equal("Last night you set tomorrow up — still the move?", prompt.Message);
        Assert.Equal("Finish the report", prompt.PrefillGoalText);
    }

    // Fail-soft: a blank narrative means the honest per-moment template, never a generic line.
    [Fact]
    public void BuildPrompt_BlankNarrative_FallsBackToTheHonestTemplate()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(), narrativeLine: "   ");

        Assert.Equal("Name today's focus and I'll help you protect it.", prompt.Message);
    }

    [Fact]
    public void BuildPrompt_EndOfDay_MessageStaysReflection_ReportRidesSeparately()
    {
        var metrics = new LocalTodaySummaryMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: 3600,
            FocusQualityPercent: 70,
            DistractionTodaySeconds: 0,
            AwayTodaySeconds: 0,
            ContextExcursionTodaySeconds: 0,
            TrackedTodaySeconds: 3600,
            DisplayedTrackedTodaySeconds: 3600,
            MostDistractedHourLabel: "Not available");

        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, goal: null, metrics, narrativeLine: "Solid close — the afternoon carried the day.");

        Assert.Equal("Solid close — the afternoon carried the day.", prompt.Message);
        Assert.Contains("focused", prompt.ReportLine, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Midday_EchoesTheGoalAndCarriesTheReport()
    {
        var goal = new LocalApiDailyGoal(
            LocalDate: "2026-03-08",
            GoalText: "Ship the release",
            TrackingPref: "keep_on_track",
            MiddayAnswer: "",
            PlanTomorrow: "",
            CreatedAtUtc: DateTimeOffset.Parse("2026-03-08T08:15:00Z"),
            MiddayAtUtc: null,
            EndOfDayAtUtc: null);
        var metrics = new LocalTodaySummaryMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: 3600,
            FocusQualityPercent: 80,
            DistractionTodaySeconds: 0,
            AwayTodaySeconds: 0,
            ContextExcursionTodaySeconds: 0,
            TrackedTodaySeconds: 3600,
            DisplayedTrackedTodaySeconds: 3600,
            MostDistractedHourLabel: "Not available");

        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(UserCheckInKind.Midday, goal, metrics);

        Assert.Equal(UserCheckInKind.Midday, prompt.Kind);
        Assert.Equal("Ship the release", prompt.GoalText);
        Assert.Contains("Ship the release", prompt.Message, StringComparison.Ordinal);
        Assert.Contains("focused", prompt.ReportLine, StringComparison.Ordinal);
    }

    // ── Part 1a/1b — BuildSnapshot carries active-work + away through to the scheduler ─────────────

    [Fact]
    public void BuildSnapshot_CarriesActiveWorkAndAway()
    {
        var goal = GoalWithText("Ship the release");

        DailyGoalSnapshot snapshot = UserCheckInCoordinator.BuildSnapshot(
            goal,
            activeWorkSinceMorning: TimeSpan.FromHours(2),
            awayToday: TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromHours(2), snapshot.ActiveWorkSinceMorning);
        Assert.Equal(TimeSpan.FromHours(3), snapshot.AwayToday);
    }

    [Fact]
    public void BuildSnapshot_DefaultsToWallClockFallback_WhenActiveWorkOmitted()
    {
        DailyGoalSnapshot snapshot = UserCheckInCoordinator.BuildSnapshot(GoalWithText("Ship it"));

        Assert.Null(snapshot.ActiveWorkSinceMorning);   // → scheduler uses the wall-clock rule
        Assert.Equal(TimeSpan.Zero, snapshot.AwayToday);
    }

    // ── Part 2b — the card ALWAYS carries a fixed question above its controls ──────────────────────

    [Fact]
    public void BuildPrompt_Midday_AlwaysCarriesTheFixedQuestionLabel_EvenWithNarrative()
    {
        UserCheckInPrompt withLlm = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Midday, goal: null, EmptyMetrics(),
            narrativeLine: "You said the API was the day — did you get to it?");
        UserCheckInPrompt withoutLlm = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Midday, goal: null, EmptyMetrics(), narrativeLine: "   ");

        Assert.Equal("Did you get to your goal?", withLlm.QuestionLabel);
        Assert.Equal("Did you get to your goal?", withoutLlm.QuestionLabel);
    }

    // Step 7 changed WHAT the evening asks — "Plan for tomorrow?" is gone, because Yadin stopped
    // answering it and unfinished work now carries itself. Part 2b's rule is unchanged and is what
    // this still guards: a fixed question is always present, whatever the LLM line does.
    [Fact]
    public void BuildPrompt_EndOfDay_AlwaysCarriesTheFixedQuestionLabel_EvenWithNarrative()
    {
        UserCheckInPrompt withLlm = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, goal: null, EmptyMetrics(), narrativeLine: "Nice close.");
        UserCheckInPrompt withoutLlm = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, goal: null, EmptyMetrics(), narrativeLine: "");

        Assert.Equal("Anything for tomorrow?", withLlm.QuestionLabel);
        Assert.Equal("Anything for tomorrow?", withoutLlm.QuestionLabel);

        // The guard here used to be "the label must not mention tomorrow", standing in for step 7
        // having removed "Plan for tomorrow?". That was too blunt once the copy was corrected on
        // 2026-08-13: with NO tasks at all, unfinished work cannot carry itself, the single box IS
        // the only way anything reaches tomorrow, and what is typed there is stored as
        // plan_tomorrow and pre-fills the next morning. Asking about tomorrow there is honest.
        //
        // What step 7 actually killed is the old wording, on a day that HAS work. Guard that.
        Assert.DoesNotContain("Plan for tomorrow", withLlm.QuestionLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Plan for tomorrow", withoutLlm.QuestionLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPrompt_EndOfDay_WithOpenTasks_AsksWhatMovesInsteadOfWhatsNext()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, goal: null, EmptyMetrics(),
            board: new[] { Line(1, "Pricing page") });

        Assert.Equal("What moves to tomorrow?", prompt.QuestionLabel);
    }

    [Fact]
    public void BuildPrompt_Morning_HasNoFixedQuestionLabel()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics());

        Assert.Equal(string.Empty, prompt.QuestionLabel);
    }

    // ── Part 1b — the EOD fallback copy is away-aware when there was a gap ─────────────────────────

    [Fact]
    public void BuildPrompt_EndOfDay_AwayGap_FallbackAcknowledgesTimeAway()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, GoalWithText("Ship the release"), EmptyMetrics(),
            narrativeLine: "   ", middaySkipped: true, awayGap: true);

        Assert.Contains("away", prompt.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ship the release", prompt.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Today's goal was", prompt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_EndOfDay_NoAwayGap_FallbackIsTheNormalWrap()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, GoalWithText("Ship the release"), EmptyMetrics(),
            narrativeLine: "   ", middaySkipped: false, awayGap: false);

        Assert.Contains("Today's goal was", prompt.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("away", prompt.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalApiDailyGoal GoalWithText(string goalText) => new(
        LocalDate: "2026-03-08",
        GoalText: goalText,
        TrackingPref: "keep_on_track",
        MiddayAnswer: "",
        PlanTomorrow: "",
        CreatedAtUtc: DateTimeOffset.Parse("2026-03-08T08:15:00Z"),
        MiddayAtUtc: null,
        EndOfDayAtUtc: null);

    // ── steps 4-7: every surface now speaks in terms of the day's real tasks ──

    // Step 4 — an open task outranks last night's sentence. A real piece of unfinished work is a
    // better proposal than a note, and it is the thing the measured hours are attached to.
    [Fact]
    public void BuildPrompt_Morning_OpenTasksOutrankTheCarriedPlan()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(),
            carriedPlan: "Finish the report",
            board: new[] { Line(1, "Pricing page") });

        Assert.Equal("Pricing page", Assert.Single(prompt.Tasks!).Title);
        Assert.DoesNotContain("Last night you planned", prompt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Morning_NoOpenTasks_StillFallsBackToTheCarriedPlan()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(),
            carriedPlan: "Finish the report",
            board: new[] { Line(1, "Pricing page", state: "done") });

        Assert.Equal("Finish the report", prompt.PrefillGoalText);
    }

    [Fact]
    public void BuildPrompt_Morning_ProposalCarriesTheNarrativeLineWhenThereIsOne()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(),
            narrativeLine: "You left the pricing page unfinished yesterday.",
            board: new[] { Line(1, "Pricing page") });

        Assert.Equal("You left the pricing page unfinished yesterday.", prompt.Message);
        Assert.Single(prompt.Tasks!);
    }

    // Step 6 — the notice rides on the prompt, said once, and only past the threshold.
    [Fact]
    public void BuildPrompt_Morning_SurfacesTheCarriedNoticePastTheThreshold()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(),
            board: new[] { Line(1, "Pricing page", daysCarried: 4) });

        Assert.Contains("moved forward 4 days", prompt.CarriedNotice, StringComparison.Ordinal);
    }

    // Step 5 — midday stops asking one vague question about "the goal".
    [Fact]
    public void BuildPrompt_Midday_WithTasks_AsksAboutTheTasks()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Midday, GoalWithText("Pricing page"), EmptyMetrics(),
            board: new[] { Line(1, "Pricing page") });

        Assert.Equal("Anything done?", prompt.QuestionLabel);
        Assert.Single(prompt.Tasks!);
    }

    [Fact]
    public void BuildPrompt_Midday_WithoutTasks_KeepsTheOldGoalQuestion()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Midday, GoalWithText("Ship the release"), EmptyMetrics());

        Assert.Equal("Did you get to your goal?", prompt.QuestionLabel);
    }

    // Step 7 — the evening closes the day. "Plan for tomorrow?" is gone.
    [Fact]
    public void BuildPrompt_EndOfDay_WithTasks_AsksWhatMovesRatherThanWhatsNext()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, GoalWithText("Pricing page"), EmptyMetrics(),
            board: new[] { Line(1, "Pricing page"), Line(2, "Invoices", state: "done") });

        Assert.Equal("What moves to tomorrow?", prompt.QuestionLabel);
        Assert.DoesNotContain("Plan for tomorrow", prompt.QuestionLabel, StringComparison.Ordinal);
        // The message is the day's score, not a fresh question.
        Assert.Equal("You planned two things. You finished one.", prompt.Message);
    }

    [Fact]
    public void BuildPrompt_EndOfDay_WithoutTasks_KeepsThePreStep3Wrap()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.EndOfDay, GoalWithText("Ship the release"), EmptyMetrics());

        Assert.Contains("Ship the release", prompt.Message, StringComparison.Ordinal);
    }

    private static TaskBoardLine Line(long id, string title, string state = "pending", int daysCarried = 0)
        => new(id, title, PlannedSeconds: 3600, MeasuredSeconds: 0d, State: state, DaysCarried: daysCarried);

    private static LocalTodaySummaryMetrics EmptyMetrics() => new(
        HasProcessedTimeline: false,
        FocusedTodaySeconds: 0,
        FocusQualityPercent: 0,
        DistractionTodaySeconds: 0,
        AwayTodaySeconds: 0,
        ContextExcursionTodaySeconds: 0,
        TrackedTodaySeconds: 0,
        DisplayedTrackedTodaySeconds: 0,
        MostDistractedHourLabel: "Not available");
}
