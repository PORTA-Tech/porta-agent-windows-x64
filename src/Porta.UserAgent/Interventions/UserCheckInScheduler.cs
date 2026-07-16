namespace Porta.UserAgent.Interventions;

// Workstream D3 — pure decision: which check-in (if any) is due right now. No I/O, no UI, no
// clock of its own, so it is fully unit-tested. The coordinator feeds it the day's goal-row
// state and an "already offered today" predicate (backed by the per-day mute), then handles
// delivery. Priority: end-of-day (evening) > midday > morning.
internal readonly record struct DailyGoalSnapshot(
    bool MorningRecorded,           // the morning goal was stated
    bool MiddayRecorded,            // the midday yes/partly/not_yet answer was given
    bool EndOfDayRecorded,          // the EOD wrap happened
    DateTimeOffset? MorningAtLocal); // when the morning goal was stated (local)

internal static class UserCheckInScheduler
{
    public const int EveningHour = 18;
    public static readonly TimeSpan MiddayDelayAfterMorning = TimeSpan.FromHours(2);

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
        if (evening
            && !goal.EndOfDayRecorded
            && !alreadyOfferedToday(UserCheckInKind.EndOfDay))
        {
            return UserCheckInKind.EndOfDay;
        }

        // Midday: only after a morning goal, ~2h later, before evening, once.
        if (!evening
            && goal.MorningRecorded
            && !goal.MiddayRecorded
            && goal.MorningAtLocal is { } morningAt
            && nowLocal >= morningAt + MiddayDelayAfterMorning
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

    /// <summary>The per-day mute class used to mark a check-in kind as already offered today.</summary>
    public static string MuteClassFor(UserCheckInKind kind) => kind switch
    {
        UserCheckInKind.Morning => "checkin:morning",
        UserCheckInKind.Midday => "checkin:midday",
        _ => "checkin:eod"
    };
}
