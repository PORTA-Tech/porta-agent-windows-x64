using Porta.Agent.Common.Analysis;
using Porta.Agent.LocalApi;
using Porta.UserAgent;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Item 8 (Chat 8 decisions, 2026-08-25) — the late-start hole.
//
// Chat 2 put an 11:00 ceiling on the morning card, for good reason: a card about the day ahead
// arriving at 23:00 is a question whose answer cannot matter. But a day whose first card
// opportunity fell after 11:00 then got no card at all — and because the midday and evening arms
// both gate on the morning having been OFFERED, the whole day recorded nothing. A noon start was
// a silent day.
//
// The decision: past 11:00 the first card of the day is still a plan card, worded for a short day.
// Do not move the ceiling and do not remove it. These tests hold both halves of that at once.
public sealed class UserCheckInLateStartTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 8, 24, hour, minute, 0, Off);

    private static Func<UserCheckInKind, bool> NoneOffered => _ => false;

    private static Func<UserCheckInKind, bool> Offered(params UserCheckInKind[] kinds)
        => k => kinds.Contains(k);

    private static UserCheckInDayTiming.WorkDaySnapshot Day(
        DateTimeOffset? workStart,
        TimeSpan? activeWork = null,
        TimeSpan? idleFor = null)
        => new(
            WorkStartLocal: workStart,
            DayLength: TimeSpan.FromHours(8),
            ActiveTaskSpent: null,
            ActiveTaskBudget: null,
            ActiveWorkToday: activeWork ?? TimeSpan.FromHours(2),
            IdleFor: idleFor ?? TimeSpan.Zero);

    private static DailyGoalSnapshot NothingRecorded => new(false, false, false, null);

    // ── the decision's own case ───────────────────────────────────────────────────────

    [Fact]
    public void ANoonStartIsNoLongerASilentDay()
    {
        (UserCheckInKind? kind, string reason) = UserCheckInScheduler.DecideCheckIn(
            At(12), NothingRecorded, Day(workStart: At(12)), NoneOffered);

        Assert.Equal(UserCheckInKind.Morning, kind);
        Assert.Contains("late_start_plan", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AndTheDayGoesOnRecording_BecauseTheFirstCardWasOffered()
    {
        // The real damage was downstream: DayHasBegun reads "morning recorded OR morning offered",
        // so no morning card meant no midday and no evening either. Once the plan card has been
        // offered, the rest of the day's cards can fire again.
        (UserCheckInKind? kind, _) = UserCheckInScheduler.DecideCheckIn(
            At(17), NothingRecorded,
            Day(workStart: At(12), activeWork: TimeSpan.FromHours(4)),
            Offered(UserCheckInKind.Morning));

        Assert.NotNull(kind);
        Assert.NotEqual(UserCheckInKind.Morning, kind);
    }

    // ── the ceiling is untouched ──────────────────────────────────────────────────────

    [Fact]
    public void TheElevenOClockCeilingHasNotMoved()
    {
        Assert.Equal(11, UserCheckInDayTiming.MorningLatestHour);
        Assert.True(UserCheckInDayTiming.IsMorningDue(At(10, 59)).Due);
        Assert.False(UserCheckInDayTiming.IsMorningDue(At(11, 0)).Due);
    }

    [Fact]
    public void BeforeElevenTheLateArmDoesNotFire_TheOrdinaryCardIsStillTheOrdinaryCard()
    {
        UserCheckInDayTiming.TimingDecision late =
            UserCheckInDayTiming.IsLatePlanDue(At(9), Day(workStart: At(9)));

        Assert.False(late.Due);
        Assert.Contains("within_morning_window", late.Reason, StringComparison.Ordinal);
    }

    // ── and the card it was built to kill stays dead ──────────────────────────────────

    [Fact]
    public void ALaptopOpenedAtElevenAtNightOnADayNeverWorkedStillGetsNothing()
    {
        // The exact Chat 2 symptom. WorkStartLocal is the first bucket carrying REAL work, so a
        // machine that was merely on has no work start at all — and this arm refuses on that
        // before it ever looks at the clock.
        (UserCheckInKind? kind, string reason) = UserCheckInScheduler.DecideCheckIn(
            At(23), NothingRecorded,
            Day(workStart: null, activeWork: TimeSpan.Zero), NoneOffered);

        Assert.Null(kind);
        Assert.Contains("no_work_start", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvenGenuineWorkAtElevenAtNightGetsNoPlanCard()
    {
        // Working at 23:00 DOES produce a work start, so the no-work-start guard cannot catch this
        // one. The plan card needs its own ceiling or the 23:00 card walks back in through it.
        (UserCheckInKind? kind, string reason) = UserCheckInScheduler.DecideCheckIn(
            At(23), NothingRecorded, Day(workStart: At(23)), NoneOffered);

        Assert.Null(kind);
        Assert.Contains("past_plan_ceiling", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenuineEveningStartStillGetsItsCard()
    {
        (UserCheckInKind? kind, _) = UserCheckInScheduler.DecideCheckIn(
            At(19), NothingRecorded, Day(workStart: At(19)), NoneOffered);

        Assert.Equal(UserCheckInKind.Morning, kind);
    }

    [Fact]
    public void ThePlanCeilingIsEightInTheEvening()
    {
        Assert.Equal(20, UserCheckInDayTiming.LatePlanLatestHour);
        Assert.True(UserCheckInDayTiming.IsLatePlanDue(At(19, 59), Day(workStart: At(19))).Due);
        Assert.False(UserCheckInDayTiming.IsLatePlanDue(At(20, 0), Day(workStart: At(19))).Due);
    }

    // ── the case the decision did not name, closed by the same rule ───────────────────

    [Fact]
    public void AnEarlyStartWhoseAgentWasDownUntilNoonAlsoGetsItsCard()
    {
        // Work began at 09:00 but nothing could ask until 12:00 — the agent was down. The hole is
        // identical: the first card opportunity is past 11:00 and the day would record nothing.
        // Keying the rule on NOW rather than on when work started closes both with one predicate.
        (UserCheckInKind? kind, string reason) = UserCheckInScheduler.DecideCheckIn(
            At(12), NothingRecorded, Day(workStart: At(9)), NoneOffered);

        Assert.Equal(UserCheckInKind.Morning, kind);
        Assert.Contains("late_start_plan", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACardAlreadyOfferedIsNotOfferedTwice()
    {
        (UserCheckInKind? kind, _) = UserCheckInScheduler.DecideCheckIn(
            At(12), NothingRecorded, Day(workStart: At(12), activeWork: TimeSpan.Zero),
            Offered(UserCheckInKind.Morning));

        Assert.NotEqual(UserCheckInKind.Morning, kind);
    }

    // ── the words ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ItIsTheSameCardWithDifferentWords_BlankBoard()
    {
        UserCheckInPrompt late = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(), restOfDay: true);

        Assert.Equal(UserCheckInKind.Morning, late.Kind);
        Assert.Equal("The rest of today", late.Title);
        Assert.Contains("Less of the day left", late.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsTheSameCardWithDifferentWords_CarriedPlan()
    {
        UserCheckInPrompt late = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics(),
            carriedPlan: "Finish the report", restOfDay: true);

        Assert.Equal("The rest of today", late.Title);
        Assert.Contains("Finish the report", late.Message, StringComparison.Ordinal);
        // the plan still pre-fills, so confirming is still one tap
        Assert.Equal("Finish the report", late.PrefillGoalText);
    }

    [Fact]
    public void TheOrdinaryWordingIsUnchangedWhenTheFlagIsOff()
    {
        // Defaulted off, so every existing caller and every existing test is unaffected.
        UserCheckInPrompt ordinary = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, EmptyMetrics());

        Assert.Equal("Today", ordinary.Title);
        Assert.DoesNotContain("Less of the day left", ordinary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordingPredicateAgreesWithTheArmThatChoseToSpeak()
    {
        // The card's words and the decision to show it must turn on the same fact, or a card can
        // arrive late while reading as though the day had not started.
        Assert.False(UserCheckInDayTiming.IsRestOfDayCard(At(10, 59)));
        Assert.True(UserCheckInDayTiming.IsRestOfDayCard(At(11, 0)));
        Assert.True(UserCheckInDayTiming.IsRestOfDayCard(At(12)));
    }

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
