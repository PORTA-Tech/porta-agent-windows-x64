using System;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// WHEN the weekly digest is due. Pure and clock-free, so Monday 07:00 is unit-tested rather than
/// reasoned about — the daily's timing bugs were all bugs of reasoning about a clock in a loop.
///
/// Yadin's decision, 2026-08-25: <b>Monday 07:00 local</b>, and Sunday's daily still sends. The
/// agent owns this clock because only the agent knows the user's local Monday; the server sends
/// whatever week it is handed, whenever it is handed it.
/// </summary>
internal static class WeeklyDigestSchedule
{
    /// <summary>Local hour the weekly goes out on a Monday.</summary>
    public const int SendHourLocal = 7;

    /// <summary>
    /// The goal day a local instant falls on. Rolls at 05:00, the same boundary as everything else
    /// in the product — <c>AgentCoreController.GoalDay</c>, and the boundary
    /// <c>task_day_hours.local_date</c> is written on.
    /// </summary>
    public static DateOnly GoalDayOf(DateTimeOffset nowLocal)
        => DateOnly.FromDateTime((nowLocal.Hour < 5 ? nowLocal.AddDays(-1) : nowLocal).Date);

    /// <summary>
    /// The week that is due to be sent at <paramref name="nowLocal"/>, or null if none is.
    ///
    /// The week always ends on the most recent Sunday STRICTLY BEFORE today's goal day — on Sunday
    /// itself the week is not over yet, because a goal day runs to 05:00 Monday.
    ///
    /// Before 07:00 on a Monday, nothing is due: that is the whole decision. From Tuesday onward a
    /// week can still be due, which is the catch-up path for a machine that was off on Monday —
    /// the same behaviour the daily has always had. A week missed entirely is then skipped for
    /// good once the next Sunday passes, rather than arriving a fortnight stale.
    /// </summary>
    public static (DateOnly Start, DateOnly End)? DueWeek(DateTimeOffset nowLocal, DateOnly? lastSentWeekEnd)
    {
        DateOnly goalToday = GoalDayOf(nowLocal);

        // Monday before the send hour: the week that ended yesterday waits for 07:00.
        if (goalToday.DayOfWeek == DayOfWeek.Monday && nowLocal.Hour < SendHourLocal)
        {
            return null;
        }

        DateOnly weekEnd = MostRecentSundayBefore(goalToday);
        DateOnly weekStart = weekEnd.AddDays(-6);

        // Already sent this week — or a later one, which a clock moved backwards could produce.
        if (lastSentWeekEnd is { } last && last >= weekEnd)
        {
            return null;
        }

        return (weekStart, weekEnd);
    }

    /// <summary>The Sunday before <paramref name="day"/>. Never <paramref name="day"/> itself.</summary>
    private static DateOnly MostRecentSundayBefore(DateOnly day)
    {
        // DayOfWeek.Sunday is 0, so days-since-Sunday is the value itself; a Sunday must still step
        // back a full week, because today's own week has not finished.
        int back = (int)day.DayOfWeek;
        return day.AddDays(back == 0 ? -7 : -back);
    }

    /// <summary>"Mon", "Tue"… the three letters the email prints. Invariant: the server never
    /// guesses a locale, and a digest must not change language with a machine setting.</summary>
    public static string LabelFor(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        DayOfWeek.Sunday => "Sun",
        _ => day.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture)
    };
}
