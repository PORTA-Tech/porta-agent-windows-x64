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
            FocusedTodaySeconds: 2 * 3600 + 30 * 60,   // 2h30m
            DistractionTodaySeconds: 20 * 60,           // 20m
            AwayTodaySeconds: 45 * 60,                   // 45m
            ContextExcursionTodaySeconds: 0,
            TrackedTodaySeconds: 3 * 3600,
            DisplayedTrackedTodaySeconds: 3 * 3600,
            MostDistractedHourLabel: "14:00-14:59");

        string line = UserCheckInCoordinator.BuildReportLine(metrics);

        Assert.Contains("2h30m focused", line, StringComparison.Ordinal);
        Assert.Contains("20m off-track", line, StringComparison.Ordinal);
        Assert.Contains("45m away", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrompt_Morning_AsksAFreshQuestionWithNoReport()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics());

        Assert.Equal(UserCheckInKind.Morning, prompt.Kind);
        Assert.Equal(string.Empty, prompt.ReportLine);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Message));
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

    private static LocalTodaySummaryMetrics EmptyMetrics() => new(
        HasProcessedTimeline: false,
        FocusedTodaySeconds: 0,
        DistractionTodaySeconds: 0,
        AwayTodaySeconds: 0,
        ContextExcursionTodaySeconds: 0,
        TrackedTodaySeconds: 0,
        DisplayedTrackedTodaySeconds: 0,
        MostDistractedHourLabel: "Not available");
}
