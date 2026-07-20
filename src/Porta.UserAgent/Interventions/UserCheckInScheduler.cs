namespace Porta.UserAgent.Interventions;

// Workstream D3 — pure decision: which check-in (if any) is due right now. No I/O, no UI, no
// clock of its own, so it is fully unit-tested. The coordinator feeds it the day's goal-row
// state and an "already offered today" predicate (backed by the per-day mute), then handles
// delivery. Priority: end-of-day (evening) > midday > morning.
internal readonly record struct DailyGoalSnapshot(
    bool MorningRecorded,           // the morning goal was stated
    bool MiddayRecorded,            // the midday yes/partly/not_yet answer was given
    bool EndOfDayRecorded,          // the EOD wrap happened
    DateTimeOffset? MorningAtLocal, // when the morning goal was stated (local)
    // Part 1a — active-work seconds accumulated since the morning goal (from LocalTodaySummaryMetrics:
    // tracked minus away). null = metrics unavailable, so midday falls back to the wall-clock rule
    // rather than becoming un-fireable. Default null keeps existing constructions on the old path.
    TimeSpan? ActiveWorkSinceMorning = null,
    // Part 1b — total away time so far today. Feeds the EOD "away gap" flag. Default zero = no gap.
    TimeSpan AwayToday = default);

internal static class UserCheckInScheduler
{
    public const int EveningHour = 18;
    // Wall-clock fallback used only when active-work is unavailable (see MiddayActiveWorkThreshold).
    public static readonly TimeSpan MiddayDelayAfterMorning = TimeSpan.FromHours(2);
    // Part 1a — midday is due after ~2h of REAL active work since the morning goal, not 2h of clock.
    public static readonly TimeSpan MiddayActiveWorkThreshold = TimeSpan.FromHours(2);
    // Part 1b — an away stretch this long makes the EOD wrap acknowledge the gap rather than framing
    // the day as a full work day.
    public static readonly TimeSpan AwayGapThreshold = TimeSpan.FromHours(2);

    /// <summary>
    /// Returns the check-in to offer now, or null when none is due. Assumes the caller has
    /// already cleared the per-day mute and cold-start gates and confirmed live activity — the
    /// morning check-in is meant to fire on the first activity after the 5:00 boundary.
    /// </summary>
    public static UserCheckInKind? DecideCheckIn(
        DateTimeOffset nowLocal,
        DailyGoalSnapshot goal,
        Func<UserCheckInKind, bool> alreadyOfferedToday)
    {
        bool evening = nowLocal.Hour >= EveningHour;

        // End-of-day wins in the evening; fires once, even if the user never set a morning goal.
        // Part 1b: when midday was skipped, EOD is the SINGLE wrap — no late/catch-up midday fires.
        // The middaySkipped/awayGap facts for its copy come from DescribeEndOfDay, computed by the
        // caller once EOD is chosen.
        if (evening
            && !goal.EndOfDayRecorded
            && !alreadyOfferedToday(UserCheckInKind.EndOfDay))
        {
            return UserCheckInKind.EndOfDay;
        }

        // Midday: only after a morning goal, before evening, once, and only after enough REAL work
        // (Part 1a). Active-work drives the trigger; the wall-clock rule is the fallback when the
        // metric is unavailable so midday can never become un-fireable.
        if (!evening
            && goal.MorningRecorded
            && !goal.MiddayRecorded
            && IsMiddayWorkThresholdMet(nowLocal, goal)
            && !alreadyOfferedToday(UserCheckInKind.Midday))
        {
            return UserCheckInKind.Midday;
        }

        // Morning: no goal yet, before evening, once (fires on first activity after 5:00).
        if (!evening
            && !goal.MorningRecorded
            && !alreadyOfferedToday(UserCheckInKind.Morning))
        {
            return UserCheckInKind.Morning;
        }

        return null;
    }

    // Part 1a — midday due when ~2h of active work has accumulated since the morning goal. When the
    // active-work figure is unavailable (null), fall back to the old "morning + 2h wall-clock" rule.
    private static bool IsMiddayWorkThresholdMet(DateTimeOffset nowLocal, DailyGoalSnapshot goal)
    {
        if (goal.ActiveWorkSinceMorning is { } activeWork)
        {
            return activeWork >= MiddayActiveWorkThreshold;
        }

        // Wall-clock fallback (unchanged legacy behavior).
        return goal.MorningAtLocal is { } morningAt
            && nowLocal >= morningAt + MiddayDelayAfterMorning;
    }

    /// <summary>
    /// Part 1b — the two facts an EOD wrap needs so it can frame the WHOLE day: whether midday was
    /// skipped (a morning goal was set but no midday answer was ever given) and whether there was a
    /// substantial away gap. Pure; the caller surfaces these into the narrative situation and the
    /// templated fallback copy.
    /// </summary>
    public static (bool MiddaySkipped, bool AwayGap) DescribeEndOfDay(DailyGoalSnapshot goal)
    {
        bool middaySkipped = goal.MorningRecorded && !goal.MiddayRecorded;
        bool awayGap = goal.AwayToday >= AwayGapThreshold;
        return (middaySkipped, awayGap);
    }

    /// <summary>The per-day mute class used to mark a check-in kind as already offered today.</summary>
    public static string MuteClassFor(UserCheckInKind kind) => kind switch
    {
        UserCheckInKind.Morning => "checkin:morning",
        UserCheckInKind.Midday => "checkin:midday",
        _ => "checkin:eod"
    };
}
