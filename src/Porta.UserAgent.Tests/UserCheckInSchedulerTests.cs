using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Workstream D3 — the pure check-in scheduling decision. Morning fires once when no goal is set
// yet; midday ~2h after the morning reply; EOD in the evening; each once per day (via the
// already-offered predicate); EOD takes priority in the evening.
public sealed class UserCheckInSchedulerTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 7, 16, hour, minute, 0, Off);
    private static Func<UserCheckInKind, bool> NoneOffered => _ => false;

    private static DailyGoalSnapshot Empty => new(false, false, false, null);

    [Fact]
    public void Morning_WhenNoGoalYet_BeforeEvening()
    {
        Assert.Equal(UserCheckInKind.Morning, UserCheckInScheduler.DecideCheckIn(At(9), Empty, NoneOffered));
    }

    [Fact]
    public void Morning_NotOfferedTwice()
    {
        Func<UserCheckInKind, bool> morningOffered = k => k == UserCheckInKind.Morning;
        Assert.Null(UserCheckInScheduler.DecideCheckIn(At(9), Empty, morningOffered));
    }

    [Fact]
    public void Midday_TwoHoursAfterMorning_ButNotBefore()
    {
        var goal = new DailyGoalSnapshot(MorningRecorded: true, MiddayRecorded: false, EndOfDayRecorded: false, MorningAtLocal: At(9));

        Assert.Null(UserCheckInScheduler.DecideCheckIn(At(10, 30), goal, NoneOffered));   // only 1.5h
        Assert.Equal(UserCheckInKind.Midday, UserCheckInScheduler.DecideCheckIn(At(11), goal, NoneOffered)); // 2h
    }

    [Fact]
    public void Midday_RequiresAMorningGoalFirst()
    {
        // No morning goal → midday never fires (nothing to ask about); morning is offered instead.
        Assert.Equal(UserCheckInKind.Morning, UserCheckInScheduler.DecideCheckIn(At(13), Empty, NoneOffered));
    }

    [Fact]
    public void Midday_NotAfterAnswered()
    {
        var answered = new DailyGoalSnapshot(true, MiddayRecorded: true, false, At(9));
        Assert.Null(UserCheckInScheduler.DecideCheckIn(At(12), answered, NoneOffered));
    }

    [Fact]
    public void EndOfDay_InTheEvening_TakesPriority()
    {
        // Evening, morning was set but midday still pending — EOD wins over midday.
        var goal = new DailyGoalSnapshot(true, false, EndOfDayRecorded: false, At(9));
        Assert.Equal(UserCheckInKind.EndOfDay, UserCheckInScheduler.DecideCheckIn(At(19), goal, NoneOffered));
    }

    [Fact]
    public void EndOfDay_FiresEvenWithoutAMorningGoal()
    {
        Assert.Equal(UserCheckInKind.EndOfDay, UserCheckInScheduler.DecideCheckIn(At(20), Empty, NoneOffered));
    }

    [Fact]
    public void EndOfDay_NotAfterRecordedOrOffered()
    {
        var done = new DailyGoalSnapshot(true, true, EndOfDayRecorded: true, At(9));
        Assert.Null(UserCheckInScheduler.DecideCheckIn(At(20), done, NoneOffered));

        Func<UserCheckInKind, bool> eodOffered = k => k == UserCheckInKind.EndOfDay;
        Assert.Null(UserCheckInScheduler.DecideCheckIn(At(20), new DailyGoalSnapshot(true, true, false, At(9)), eodOffered));
    }

    // ── Part 1a — midday triggers on REAL active work, not wall-clock ──────────────────────────────

    [Fact]
    public void Midday_FiresAfterTwoHoursOfActiveWork()
    {
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: false, EndOfDayRecorded: false,
            MorningAtLocal: At(9), ActiveWorkSinceMorning: TimeSpan.FromHours(2));
        Assert.Equal(UserCheckInKind.Midday, UserCheckInScheduler.DecideCheckIn(At(11), goal, NoneOffered));
    }

    [Fact]
    public void Midday_DoesNotFireAt2hWallClockWhenActiveWorkIsShort()
    {
        // 2h of wall-clock have passed, but only 40m of real work — midday must NOT fire yet.
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: false, EndOfDayRecorded: false,
            MorningAtLocal: At(9), ActiveWorkSinceMorning: TimeSpan.FromMinutes(40));
        Assert.Null(UserCheckInScheduler.DecideCheckIn(At(11), goal, NoneOffered));
    }

    [Fact]
    public void Midday_FallsBackToWallClock_WhenActiveWorkUnavailable()
    {
        // ActiveWorkSinceMorning defaults to null (metrics unavailable) → old wall-clock rule applies.
        var goal = new DailyGoalSnapshot(true, false, false, At(9));
        Assert.Null(UserCheckInScheduler.DecideCheckIn(At(10, 30), goal, NoneOffered));       // 1.5h wall-clock
        Assert.Equal(UserCheckInKind.Midday, UserCheckInScheduler.DecideCheckIn(At(11), goal, NoneOffered)); // 2h wall-clock
    }

    // ── Part 1b — missed midday → ONE away-aware EOD wrap (no catch-up midday) ─────────────────────

    [Fact]
    public void AwayAllAfternoon_EveningFiresEndOfDay_WithMiddaySkippedAndAwayGap_NotMidday()
    {
        // Morning goal set, midday never answered, away all afternoon, return after 18:00.
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: false, EndOfDayRecorded: false,
            MorningAtLocal: At(9), ActiveWorkSinceMorning: TimeSpan.FromMinutes(30),
            AwayToday: TimeSpan.FromHours(5));

        // The single wrap is EOD — no separate or catch-up midday.
        Assert.Equal(UserCheckInKind.EndOfDay, UserCheckInScheduler.DecideCheckIn(At(19), goal, NoneOffered));

        (bool middaySkipped, bool awayGap) = UserCheckInScheduler.DescribeEndOfDay(goal);
        Assert.True(middaySkipped);
        Assert.True(awayGap);
    }

    [Fact]
    public void NormalDay_EndOfDay_IsNotMiddaySkipped_NorAwayGap()
    {
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: true, EndOfDayRecorded: false,
            MorningAtLocal: At(9), ActiveWorkSinceMorning: TimeSpan.FromHours(7),
            AwayToday: TimeSpan.FromMinutes(15));

        (bool middaySkipped, bool awayGap) = UserCheckInScheduler.DescribeEndOfDay(goal);
        Assert.False(middaySkipped);
        Assert.False(awayGap);
    }

    [Fact]
    public void DescribeEndOfDay_MiddaySkipped_WhenMorningSetButMiddayMissing_EvenWithoutAwayGap()
    {
        // Midday genuinely skipped (not away) — flagged, but no away-gap wording is implied.
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: false, EndOfDayRecorded: false,
            MorningAtLocal: At(9), ActiveWorkSinceMorning: TimeSpan.FromHours(6),
            AwayToday: TimeSpan.FromMinutes(20));

        (bool middaySkipped, bool awayGap) = UserCheckInScheduler.DescribeEndOfDay(goal);
        Assert.True(middaySkipped);
        Assert.False(awayGap);
    }
}
