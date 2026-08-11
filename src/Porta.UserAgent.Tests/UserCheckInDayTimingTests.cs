using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Complaints 1 + 2 (2026-08-11) - WHEN each card is due. One test per case Yadin asked to have
// answered rather than assumed, named so a failure says which promise broke.
public sealed class UserCheckInDayTimingTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 7, 16, hour, minute, 0, Off);

    // An 8-hour day from 08:00: thirds at 10:40 and 13:20, half at 12:00, end at 16:00.
    private static UserCheckInDayTiming.WorkDaySnapshot Day(
        TimeSpan? activeWork = null,
        TimeSpan? taskSpent = null,
        TimeSpan? taskBudget = null,
        TimeSpan? idleFor = null,
        DateTimeOffset? workStart = null,
        TimeSpan? dayLength = null)
        => new(
            WorkStartLocal: workStart ?? At(8),
            DayLength: dayLength ?? TimeSpan.FromHours(8),
            ActiveTaskSpent: taskSpent,
            ActiveTaskBudget: taskBudget,
            ActiveWorkToday: activeWork ?? TimeSpan.FromHours(3),
            IdleFor: idleFor ?? TimeSpan.Zero);

    // -- The estimate is the trigger ------------------------------------------------------------

    [Theory]
    [InlineData(1, true)]   // said 1h, spent 1h  -> ask
    [InlineData(2, true)]   // said 2h, spent 2h  -> ask
    [InlineData(3, false)]  // said 3h, spent 2h  -> do NOT ask
    public void FirstMidday_AsksWhenTheUsersOwnEstimateRunsOut(int budgetHours, bool expected)
    {
        var day = Day(
            activeWork: TimeSpan.FromHours(2),
            taskSpent: TimeSpan.FromHours(2),
            taskBudget: TimeSpan.FromHours(budgetHours));

        Assert.Equal(expected, UserCheckInDayTiming.IsFirstMiddayDue(At(11), day).Due);
    }

    [Fact]
    public void FirstMidday_Floor_NoInterruptionBeforeNinetyMinutesOfActiveWork()
    {
        // Case: three short tasks must not mean three interruptions in the first hour.
        var day = Day(
            activeWork: TimeSpan.FromMinutes(40),
            taskSpent: TimeSpan.FromMinutes(30),
            taskBudget: TimeSpan.FromMinutes(30));

        UserCheckInDayTiming.TimingDecision decision = UserCheckInDayTiming.IsFirstMiddayDue(At(9), day);
        Assert.False(decision.Due);
        Assert.Contains("below_active_work_floor", decision.Reason);
    }

    [Fact]
    public void FirstMidday_Ceiling_FiresByHalfTheDaySoItCannotDriftIntoTheSecondCard()
    {
        // A 3h estimate is ~4h40m of wall clock at the measured 64% ratio, which would otherwise
        // land on top of the two-thirds card. Half the day is the cap.
        var day = Day(
            activeWork: TimeSpan.FromHours(3),
            taskSpent: TimeSpan.FromHours(2),
            taskBudget: TimeSpan.FromHours(8));

        UserCheckInDayTiming.TimingDecision decision = UserCheckInDayTiming.IsFirstMiddayDue(At(12), day);
        Assert.True(decision.Due);
        Assert.Equal("ceiling_half_day", decision.Reason);
    }

    [Fact]
    public void FirstMiddayCeiling_AlwaysLeavesAGapBeforeTheSecondCard()
    {
        // The property that matters, across day lengths: the cap is strictly before two thirds.
        foreach (int hours in new[] { 4, 6, 8, 10, 12 })
        {
            var day = Day(dayLength: TimeSpan.FromHours(hours));
            DateTimeOffset ceiling = UserCheckInDayTiming.FractionAt(day, UserCheckInDayTiming.FirstMiddayCeilingFraction)!.Value;
            DateTimeOffset second = UserCheckInDayTiming.FractionAt(day, UserCheckInDayTiming.SecondMiddayFraction)!.Value;
            Assert.True(ceiling < second, $"{hours}h day: ceiling {ceiling:HH:mm} must precede {second:HH:mm}");
        }
    }

    // -- Case b: +15 on the widget --------------------------------------------------------------

    [Fact]
    public void CaseB_ExtendingATaskMovesTheCheckWithIt()
    {
        // ActOnTask "extend" adds 900s to extended_seconds, a SEPARATE column from planned_seconds,
        // so the budget the trigger reads must be planned + extended.
        TimeSpan planned = TimeSpan.FromHours(2);
        TimeSpan extended = TimeSpan.FromMinutes(15);
        var spent = TimeSpan.FromHours(2);

        Assert.True(UserCheckInDayTiming.IsFirstMiddayDue(
            At(11), Day(taskSpent: spent, taskBudget: planned)).Due);

        Assert.False(UserCheckInDayTiming.IsFirstMiddayDue(
            At(11), Day(taskSpent: spent, taskBudget: planned + extended)).Due);
    }

    // -- Cases c and d: nothing active ----------------------------------------------------------

    [Fact]
    public void CaseC_NoTasksAtAll_FallsBackToAThirdOfTheDay()
    {
        var day = Day(taskSpent: null, taskBudget: null);

        Assert.False(UserCheckInDayTiming.IsFirstMiddayDue(At(10), day).Due);      // before 10:40
        Assert.True(UserCheckInDayTiming.IsFirstMiddayDue(At(10, 45), day).Due);   // after
    }

    [Fact]
    public void CaseD_MarkingATaskDoneDoesNotFireTheCard_ButItCannotVanishEither()
    {
        // Marking done IS the answer; asking "did you finish?" ten seconds later is the card that
        // teaches a person to ignore cards. With nothing active the fallback still guarantees it.
        var nothingActive = Day(taskSpent: null, taskBudget: null);

        Assert.False(UserCheckInDayTiming.IsFirstMiddayDue(At(9, 30), nothingActive).Due);
        Assert.True(UserCheckInDayTiming.IsFirstMiddayDue(At(13), nothingActive).Due);
    }

    // -- Complaint 2: the evening card ----------------------------------------------------------

    [Fact]
    public void Evening_DoesNotFireBeforeThePredictedEnd()
    {
        Assert.False(UserCheckInDayTiming.IsEveningDue(At(15), Day(idleFor: TimeSpan.FromMinutes(30))).Due);
    }

    [Fact]
    public void Evening_WaitsForARealBreak_RatherThanClosingADayMidWork()
    {
        var stillWorking = Day(idleFor: TimeSpan.FromMinutes(1));
        UserCheckInDayTiming.TimingDecision decision = UserCheckInDayTiming.IsEveningDue(At(16, 30), stillWorking);

        Assert.False(decision.Due);
        Assert.Contains("still_working", decision.Reason);
    }

    [Fact]
    public void Evening_FiresOnAFewMinutesGenuinelyIdle()
    {
        var onABreak = Day(idleFor: UserCheckInDayTiming.EveningIdleBreak);
        Assert.True(UserCheckInDayTiming.IsEveningDue(At(16, 30), onABreak).Due);
    }

    [Fact]
    public void Evening_HardCeiling_FiresTwoHoursPastRegardlessOfWork()
    {
        var neverIdle = Day(idleFor: TimeSpan.Zero);
        Assert.False(UserCheckInDayTiming.IsEveningDue(At(17, 59), neverIdle).Due);

        UserCheckInDayTiming.TimingDecision decision = UserCheckInDayTiming.IsEveningDue(At(18, 1), neverIdle);
        Assert.True(decision.Due);
        Assert.Equal("hard_ceiling_two_hours_past", decision.Reason);
    }

    // -- Complaint 3 / case: work start ---------------------------------------------------------

    [Fact]
    public void NoWorkStartYet_LeavesEveryFractionCardUndecided_RatherThanGuessing()
    {
        var unknown = Day(workStart: null) with { WorkStartLocal = null };

        Assert.Equal("no_work_start", UserCheckInDayTiming.IsEveningDue(At(20), unknown).Reason);
        Assert.Equal("no_work_start", UserCheckInDayTiming.IsSecondMiddayDue(At(20), unknown).Reason);
    }

    [Fact]
    public void CardsAreSpacedByFractions_SoAnEightAndATenHourDayNeedNoSpecialCase()
    {
        // Yadin's own worked example: 08:00 / 10:40 / 13:20 / 16:00 and 08:00 / 11:20 / 14:40 / 18:00.
        var eight = Day(dayLength: TimeSpan.FromHours(8));
        Assert.Equal(At(10, 40), UserCheckInDayTiming.FractionAt(eight, UserCheckInDayTiming.FirstMiddayFallbackFraction));
        Assert.Equal(At(13, 20), UserCheckInDayTiming.FractionAt(eight, UserCheckInDayTiming.SecondMiddayFraction));
        Assert.Equal(At(16, 0), UserCheckInDayTiming.FractionAt(eight, 1d));

        var ten = Day(dayLength: TimeSpan.FromHours(10));
        Assert.Equal(At(11, 20), UserCheckInDayTiming.FractionAt(ten, UserCheckInDayTiming.FirstMiddayFallbackFraction));
        Assert.Equal(At(14, 40), UserCheckInDayTiming.FractionAt(ten, UserCheckInDayTiming.SecondMiddayFraction));
        Assert.Equal(At(18, 0), UserCheckInDayTiming.FractionAt(ten, 1d));
    }

    // -- The learned day length -----------------------------------------------------------------

    [Fact]
    public void DayLength_DefaultsToEightHours_AndIsClamped()
    {
        Assert.Equal(UserCheckInDayTiming.DefaultDayLength, UserCheckInDayTiming.ResolveDayLength(null));
        Assert.Equal(UserCheckInDayTiming.DefaultDayLength, UserCheckInDayTiming.ResolveDayLength(TimeSpan.Zero));
        Assert.Equal(UserCheckInDayTiming.MinDayLength, UserCheckInDayTiming.ResolveDayLength(TimeSpan.FromHours(1)));
        Assert.Equal(UserCheckInDayTiming.MaxDayLength, UserCheckInDayTiming.ResolveDayLength(TimeSpan.FromHours(20)));
        Assert.Equal(TimeSpan.FromHours(9), UserCheckInDayTiming.ResolveDayLength(TimeSpan.FromHours(9)));
    }
}
