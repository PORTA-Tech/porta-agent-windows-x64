using System;
using Porta.Agent.Service.Fleet;
using Xunit;

namespace Porta.Agent.Service.Tests;

// WHEN the weekly digest fires. Yadin's decision, 2026-08-25: Monday 07:00 local, and Sunday's
// daily still sends. The rule is pure so it is tested rather than reasoned about in a poll loop —
// every timing bug the daily ever had was a bug of reasoning about a clock inside a loop.
public sealed class WeeklyDigestScheduleTests
{
    private static readonly TimeSpan Dubai = TimeSpan.FromHours(4);

    private static DateTimeOffset Local(int y, int m, int d, int hh, int mm = 0)
        => new(new DateTime(y, m, d, hh, mm, 0), Dubai);

    // Monday 24 August 2026. The week before it is Mon 17 – Sun 23.
    [Fact]
    public void FiresOnMondayAtSeven_ForTheWeekThatJustEnded()
    {
        var due = WeeklyDigestSchedule.DueWeek(Local(2026, 8, 24, 7, 0), lastSentWeekEnd: null);

        Assert.NotNull(due);
        Assert.Equal(new DateOnly(2026, 8, 17), due!.Value.Start);
        Assert.Equal(new DateOnly(2026, 8, 23), due.Value.End);
    }

    [Fact]
    public void DoesNotFireBeforeSevenOnMonday()
    {
        Assert.Null(WeeklyDigestSchedule.DueWeek(Local(2026, 8, 24, 6, 59), lastSentWeekEnd: null));
    }

    [Fact]
    public void DoesNotFireTwiceForTheSameWeek()
    {
        var already = new DateOnly(2026, 8, 23);
        Assert.Null(WeeklyDigestSchedule.DueWeek(Local(2026, 8, 24, 9, 0), already));
        Assert.Null(WeeklyDigestSchedule.DueWeek(Local(2026, 8, 26, 15, 0), already));
    }

    [Fact]
    public void CatchesUpWhenTheMachineWasOffOnMonday()
    {
        // Booted Wednesday. The week that ended on Sunday is still the one to send — the same
        // behaviour the daily has always had.
        var due = WeeklyDigestSchedule.DueWeek(Local(2026, 8, 26, 10, 0), lastSentWeekEnd: null);

        Assert.NotNull(due);
        Assert.Equal(new DateOnly(2026, 8, 23), due!.Value.End);
    }

    [Fact]
    public void ADeadWeekIsSkippedForGoodRatherThanArrivingAFortnightStale()
    {
        // Never sent, and it is now the Monday AFTER the missed week. The week on offer is the one
        // that just ended, not the one before it: a fortnight-old review is worse than none.
        var due = WeeklyDigestSchedule.DueWeek(Local(2026, 8, 31, 8, 0), lastSentWeekEnd: null);

        Assert.NotNull(due);
        Assert.Equal(new DateOnly(2026, 8, 24), due!.Value.Start);
        Assert.Equal(new DateOnly(2026, 8, 30), due.Value.End);
    }

    [Fact]
    public void SundayIsNotYetAWeek()
    {
        // On Sunday the current week has not finished — a goal day runs to 05:00 Monday. What is on
        // offer is the PREVIOUS week, which has already been sent, so nothing is due.
        var due = WeeklyDigestSchedule.DueWeek(Local(2026, 8, 23, 20, 0), lastSentWeekEnd: new DateOnly(2026, 8, 16));
        Assert.Null(due);
    }

    [Fact]
    public void TheSmallHoursOfMondayStillBelongToSunday()
    {
        // 03:00 Monday is goal-day Sunday, so the week is not over and nothing new is due. This is
        // the 05:00 boundary that task_day_hours is written on; getting it wrong here would send a
        // week that is missing its own last night.
        Assert.Equal(new DateOnly(2026, 8, 23), WeeklyDigestSchedule.GoalDayOf(Local(2026, 8, 24, 3, 0)));
        Assert.Null(WeeklyDigestSchedule.DueWeek(Local(2026, 8, 24, 3, 0), lastSentWeekEnd: new DateOnly(2026, 8, 16)));
    }

    [Fact]
    public void FiveAmMondayIsAlreadyMondayButStillTooEarly()
    {
        Assert.Equal(new DateOnly(2026, 8, 24), WeeklyDigestSchedule.GoalDayOf(Local(2026, 8, 24, 5, 0)));
        Assert.Null(WeeklyDigestSchedule.DueWeek(Local(2026, 8, 24, 5, 0), lastSentWeekEnd: null));
    }

    [Fact]
    public void LabelsAreFixedEnglish_SoADigestDoesNotChangeLanguageWithAMachineSetting()
    {
        Assert.Equal("Mon", WeeklyDigestSchedule.LabelFor(new DateOnly(2026, 8, 17)));
        Assert.Equal("Sun", WeeklyDigestSchedule.LabelFor(new DateOnly(2026, 8, 23)));
    }

    [Fact]
    public void EveryWeekItOffersRunsMondayToSunday()
    {
        // Walk a year of Mondays: the window must always be seven days, Monday to Sunday.
        for (var d = new DateTime(2026, 1, 5); d < new DateTime(2027, 1, 4); d = d.AddDays(7))
        {
            var due = WeeklyDigestSchedule.DueWeek(new DateTimeOffset(d.AddHours(8), Dubai), lastSentWeekEnd: null);
            Assert.NotNull(due);
            Assert.Equal(DayOfWeek.Monday, due!.Value.Start.DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, due.Value.End.DayOfWeek);
            Assert.Equal(6, due.Value.End.DayNumber - due.Value.Start.DayNumber);
        }
    }
}
