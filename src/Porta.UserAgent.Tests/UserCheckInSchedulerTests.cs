using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Workstream D3 - the pure check-in scheduling decision, rewritten 2026-08-11 for the four-card
// model (complaints 1 + 2 + 3).
//
// What changed, and why the old assertions are gone rather than patched:
//   * The evening card no longer fires at wall-clock hour 18. It fires at the end of the PREDICTED
//     day, and then only at a real break - the 18:04 card that closed the day mid-work is the bug.
//   * The first midday no longer fires after a fixed 2h of active work. It fires when the ACTIVE
//     task has spent the time the user estimated for it. Asking "done?" at 2h on a 3h task can only
//     get one answer, which is how a person learns to ignore the card.
//   * There is now a fourth card between them.
//
// Ordering here is the scheduler's job; the WHEN lives in UserCheckInDayTimingTests.
public sealed class UserCheckInSchedulerTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 7, 16, hour, minute, 0, Off);
    private static Func<UserCheckInKind, bool> NoneOffered => _ => false;

    private static DailyGoalSnapshot Empty => new(false, false, false, null);

    // An 8-hour day starting at 08:00: thirds at 10:40 and 13:20, end at 16:00.
    private static UserCheckInDayTiming.WorkDaySnapshot Day(
        TimeSpan? activeWork = null,
        TimeSpan? taskSpent = null,
        TimeSpan? taskBudget = null,
        TimeSpan? idleFor = null,
        DateTimeOffset? workStart = null)
        => new(
            WorkStartLocal: workStart ?? At(8),
            DayLength: TimeSpan.FromHours(8),
            ActiveTaskSpent: taskSpent,
            ActiveTaskBudget: taskBudget,
            ActiveWorkToday: activeWork ?? TimeSpan.FromHours(3),
            IdleFor: idleFor ?? TimeSpan.Zero);

    private static UserCheckInKind? Decide(
        DateTimeOffset now,
        DailyGoalSnapshot goal,
        UserCheckInDayTiming.WorkDaySnapshot day,
        Func<UserCheckInKind, bool>? offered = null)
        => UserCheckInScheduler.DecideCheckIn(now, goal, day, offered ?? NoneOffered).Kind;

    [Fact]
    public void Morning_WhenNoGoalYet_EarlyInTheDay()
    {
        Assert.Equal(UserCheckInKind.Morning, Decide(At(9), Empty, Day()));
    }

    [Fact]
    public void Morning_NotOfferedTwice()
    {
        Assert.Null(Decide(At(9), Empty, Day(), k => k == UserCheckInKind.Morning));
    }

    // -- Complaint 1: the first midday is anchored on the user's own estimate -----------------

    [Fact]
    public void FirstMidday_FiresWhenTheActiveTaskHasSpentItsEstimate()
    {
        var goal = new DailyGoalSnapshot(true, false, false, At(8));
        var day = Day(taskSpent: TimeSpan.FromHours(2), taskBudget: TimeSpan.FromHours(2));

        Assert.Equal(UserCheckInKind.Midday, Decide(At(11), goal, day));
    }

    [Fact]
    public void FirstMidday_DoesNotFireAtTwoHoursOnAThreeHourTask()
    {
        // The complaint, as a test: a check at 2h on a 3h task can only get one answer.
        var goal = new DailyGoalSnapshot(true, false, false, At(8));
        var day = Day(taskSpent: TimeSpan.FromHours(2), taskBudget: TimeSpan.FromHours(3));

        Assert.Null(Decide(At(11), goal, day));
    }

    [Fact]
    public void FirstMidday_NotAfterAnswered()
    {
        var answered = new DailyGoalSnapshot(true, MiddayRecorded: true, false, At(8));
        var day = Day(taskSpent: TimeSpan.FromHours(3), taskBudget: TimeSpan.FromHours(2));

        Assert.Null(Decide(At(11), answered, day));
    }

    [Fact]
    public void FirstMidday_SuppressedOnceTheCloseOutHasBeenOffered()
    {
        // "Done, or not yet?" after "what is still open?" is the conversation backwards.
        var goal = new DailyGoalSnapshot(true, false, false, At(8));
        var day = Day(taskSpent: TimeSpan.FromHours(3), taskBudget: TimeSpan.FromHours(2));

        Assert.Null(Decide(At(12), goal, day, k => k == UserCheckInKind.MiddayClose));
    }

    // -- Complaint 3: the fourth card ---------------------------------------------------------

    [Fact]
    public void SecondMidday_FiresTwoThirdsThroughTheDay()
    {
        var goal = new DailyGoalSnapshot(true, true, false, At(8));

        Assert.Equal(UserCheckInKind.MiddayClose, Decide(At(13, 30), goal, Day()));
    }

    [Fact]
    public void SecondMidday_StillFiresWhenTheFirstNeverDid_AndSaysSo()
    {
        // Yadin, 2026-08-11: if the first was deferred and something stopped it firing, the second
        // must still arrive, and must notice rather than pretend the first happened.
        var goal = new DailyGoalSnapshot(true, MiddayRecorded: false, false, At(8));

        Assert.Equal(UserCheckInKind.MiddayClose, Decide(At(13, 30), goal, Day()));
        Assert.True(UserCheckInDayTiming.DescribeSecondMidday(firstMiddayAnswered: false));
        Assert.False(UserCheckInDayTiming.DescribeSecondMidday(firstMiddayAnswered: true));
    }

    [Fact]
    public void SecondMidday_NotOfferedTwice()
    {
        var goal = new DailyGoalSnapshot(true, true, false, At(8));

        Assert.Null(Decide(At(13, 30), goal, Day(), k => k == UserCheckInKind.MiddayClose));
    }

    [Fact]
    public void EachCardHasItsOwnMuteClass_SoOneCannotSuppressAnother()
    {
        // The bug this change would otherwise have created: MuteClassFor used to end in
        // `_ => "checkin:eod"`, so a fourth kind silently shared the EVENING card's mute.
        string[] classes = new[]
        {
            UserCheckInKind.Morning,
            UserCheckInKind.Midday,
            UserCheckInKind.MiddayClose,
            UserCheckInKind.EndOfDay
        }.Select(UserCheckInScheduler.MuteClassFor).ToArray();

        Assert.Equal(classes.Length, classes.Distinct().Count());
        Assert.NotEqual(
            UserCheckInScheduler.MuteClassFor(UserCheckInKind.MiddayClose),
            UserCheckInScheduler.MuteClassFor(UserCheckInKind.EndOfDay));
    }

    // -- Complaint 2: the evening card ---------------------------------------------------------

    [Fact]
    public void EndOfDay_TakesPriority_AtTheEndOfTheDayOnABreak()
    {
        var goal = new DailyGoalSnapshot(true, false, false, At(8));
        var day = Day(idleFor: TimeSpan.FromMinutes(10));

        Assert.Equal(UserCheckInKind.EndOfDay, Decide(At(16, 30), goal, day));
    }

    [Fact]
    public void EndOfDay_DoesNotFireWhileStillWorking_TheEighteenOhFourBug()
    {
        // The card that broke the loop fired at 18:04 while he was still working. Past the
        // predicted end the card now WAITS while the user is active instead of closing the day.
        //
        // Worth being exact about the limit of this: the wait is not unbounded, because Yadin set a
        // hard ceiling of two hours past. On an 8h day starting 08:00 that ceiling lands at 18:00 -
        // so on those particular numbers a card at 18:04 would still appear. What has changed is
        // WHY: it fires because ten hours have passed, not because a clock said 18. And a day where
        // he is genuinely working at 18:04 teaches the day-length store a longer day, which moves
        // both the end and the ceiling out. See Evening_HardCeiling_FiresTwoHoursPastRegardlessOfWork.
        var goal = new DailyGoalSnapshot(true, true, false, At(8));
        var day = Day(idleFor: TimeSpan.Zero);

        Assert.Null(Decide(At(17), goal, day));       // one hour past the end, still working
        Assert.Null(Decide(At(17, 59), goal, day));   // right up to the ceiling
    }

    [Fact]
    public void EndOfDay_FiresEvenWithoutAMorningGoal()
    {
        Assert.Equal(
            UserCheckInKind.EndOfDay,
            Decide(At(16, 30), Empty, Day(idleFor: TimeSpan.FromMinutes(10))));
    }

    [Fact]
    public void EndOfDay_NotAfterRecordedOrOffered()
    {
        var day = Day(idleFor: TimeSpan.FromMinutes(10));
        var done = new DailyGoalSnapshot(true, true, EndOfDayRecorded: true, At(8));
        Assert.Null(Decide(At(17), done, day));

        Assert.Null(Decide(
            At(17),
            new DailyGoalSnapshot(true, true, false, At(8)),
            day,
            k => k == UserCheckInKind.EndOfDay));
    }

    [Fact]
    public void NoMiddayCardFires_OnceTheDayIsPastItsEnd()
    {
        // A "what is left today?" card after the day is over is noise; the evening card is the one
        // that should be waiting for a break.
        var goal = new DailyGoalSnapshot(true, false, false, At(8));
        var day = Day(taskSpent: TimeSpan.FromHours(4), taskBudget: TimeSpan.FromHours(2));

        Assert.NotEqual(UserCheckInKind.Midday, Decide(At(17), goal, day, k => k == UserCheckInKind.EndOfDay));
        Assert.NotEqual(UserCheckInKind.MiddayClose, Decide(At(17), goal, day, k => k == UserCheckInKind.EndOfDay));
    }

    // -- Case c: a day with no tasks at all -----------------------------------------------------

    [Fact]
    public void MiddayStillFires_OnAJustTrackDayWithNoGoalText()
    {
        // Since step 3 the goal text is DERIVED from task titles, so a "just track" day produces no
        // goal text at all. Under the old `MorningRecorded` gate neither midday check could ever
        // fire on such a day. Being asked counts, even when the answer was nothing.
        var noGoalText = new DailyGoalSnapshot(MorningRecorded: false, false, false, null);
        var day = Day(taskSpent: null, taskBudget: null);

        Assert.Equal(
            UserCheckInKind.Midday,
            Decide(At(11, 30), noGoalText, day, k => k == UserCheckInKind.Morning));
    }

    // -- Part 1b - the away-aware EOD wrap, unchanged --------------------------------------------

    [Fact]
    public void AwayAllAfternoon_EndOfDayIsTheSingleWrap_WithMiddaySkippedAndAwayGap()
    {
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: false, EndOfDayRecorded: false,
            MorningAtLocal: At(8), ActiveWorkSinceMorning: TimeSpan.FromMinutes(30),
            AwayToday: TimeSpan.FromHours(5));

        Assert.Equal(
            UserCheckInKind.EndOfDay,
            Decide(At(16, 30), goal, Day(idleFor: TimeSpan.FromMinutes(10))));

        (bool middaySkipped, bool awayGap) = UserCheckInScheduler.DescribeEndOfDay(goal);
        Assert.True(middaySkipped);
        Assert.True(awayGap);
    }

    [Fact]
    public void NormalDay_EndOfDay_IsNotMiddaySkipped_NorAwayGap()
    {
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: true, EndOfDayRecorded: false,
            MorningAtLocal: At(8), ActiveWorkSinceMorning: TimeSpan.FromHours(7),
            AwayToday: TimeSpan.FromMinutes(15));

        (bool middaySkipped, bool awayGap) = UserCheckInScheduler.DescribeEndOfDay(goal);
        Assert.False(middaySkipped);
        Assert.False(awayGap);
    }

    [Fact]
    public void DescribeEndOfDay_MiddaySkipped_WhenMorningSetButMiddayMissing_EvenWithoutAwayGap()
    {
        var goal = new DailyGoalSnapshot(
            MorningRecorded: true, MiddayRecorded: false, EndOfDayRecorded: false,
            MorningAtLocal: At(8), ActiveWorkSinceMorning: TimeSpan.FromHours(6),
            AwayToday: TimeSpan.FromMinutes(20));

        (bool middaySkipped, bool awayGap) = UserCheckInScheduler.DescribeEndOfDay(goal);
        Assert.True(middaySkipped);
        Assert.False(awayGap);
    }
}
