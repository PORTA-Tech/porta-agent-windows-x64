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
    // REMOVED 2026-08-11, complaints 1 + 2 — EveningHour (18), MiddayDelayAfterMorning (2h) and
    // MiddayActiveWorkThreshold (2h). All three were the fixed clocks being replaced:
    //
    //   * EveningHour is what fired the 18:04 card that closed the day mid-work. The evening card
    //     is now due at the end of the PREDICTED day and then only at a real break.
    //   * The two midday constants said "ask after 2 hours" regardless of what the user estimated.
    //     The first midday is now anchored on the ACTIVE TASK's own estimate.
    //
    // The FALLBACK mechanism they served survives, re-pointed at a third of the day so the cards
    // share one currency instead of two (Yadin's answer to case f). See UserCheckInDayTiming.
    //
    // Part 1b — an away stretch this long makes the EOD wrap acknowledge the gap rather than framing
    // the day as a full work day.
    public static readonly TimeSpan AwayGapThreshold = TimeSpan.FromHours(2);

    /// <summary>
    /// Returns the check-in to offer now, or null when none is due. Assumes the caller has
    /// already cleared the per-day mute and cold-start gates and confirmed live activity — the
    /// morning check-in is meant to fire on the first activity after the 5:00 boundary.
    /// </summary>
    public static (UserCheckInKind? Kind, string Reason) DecideCheckIn(
        DateTimeOffset nowLocal,
        DailyGoalSnapshot goal,
        UserCheckInDayTiming.WorkDaySnapshot day,
        Func<UserCheckInKind, bool> alreadyOfferedToday)
    {
        // Complaint 2 - the evening card no longer keys off the wall-clock hour. It is due at the
        // end of the PREDICTED day, and then only at a real break, or two hours past regardless.
        // This is the fix for the 18:04 card that closed the day while the user was still working.
        UserCheckInDayTiming.TimingDecision evening = UserCheckInDayTiming.IsEveningDue(nowLocal, day);
        if (evening.Due
            && !goal.EndOfDayRecorded
            && !alreadyOfferedToday(UserCheckInKind.EndOfDay))
        {
            return (UserCheckInKind.EndOfDay, $"eod:{evening.Reason}");
        }

        // Nothing else may fire once the day is past its predicted end: a "what is left today?"
        // card after the day is over is noise, and the evening card above is the one waiting.
        bool dayIsOver = evening.Due || IsPastPredictedEnd(nowLocal, day);

        // Complaint 3 - the second midday, the close-out. Deliberately NOT conditional on the first
        // having fired: if the first was deferred to its ceiling and then something stopped it,
        // this one must still arrive. DescribeSecondMidday tells the card to say so.
        if (!dayIsOver
            && DayHasBegun(goal, alreadyOfferedToday)
            && !goal.EndOfDayRecorded
            && !alreadyOfferedToday(UserCheckInKind.MiddayClose))
        {
            UserCheckInDayTiming.TimingDecision close = UserCheckInDayTiming.IsSecondMiddayDue(nowLocal, day);
            if (close.Due)
            {
                return (UserCheckInKind.MiddayClose, $"midday_close:{close.Reason}");
            }
        }

        // Complaint 1 - the first midday. Due when the ACTIVE task has spent the time the user said
        // it would take, floored at 90 minutes of active work and capped at half the day.
        //
        // Suppressed once the close-out has been offered: showing "done, or not yet?" after
        // "what is still open?" is the conversation backwards.
        if (!dayIsOver
            && DayHasBegun(goal, alreadyOfferedToday)
            && !goal.MiddayRecorded
            && !alreadyOfferedToday(UserCheckInKind.Midday)
            && !alreadyOfferedToday(UserCheckInKind.MiddayClose))
        {
            UserCheckInDayTiming.TimingDecision midday = UserCheckInDayTiming.IsFirstMiddayDue(nowLocal, day);
            if (midday.Due)
            {
                return (UserCheckInKind.Midday, $"midday:{midday.Reason}");
            }
        }

        // Morning: no goal yet, once, on the first activity after 5:00. Still first in the day and
        // last in this list, because every check above is a no-op before it has been answered.
        if (!dayIsOver
            && !goal.MorningRecorded
            && !alreadyOfferedToday(UserCheckInKind.Morning))
        {
            return (UserCheckInKind.Morning, "morning:first_activity");
        }

        return (null, "none_due");
    }

    /// <summary>
    /// Whether the day has begun as far as the midday cards are concerned. Deliberately NOT just
    /// `MorningRecorded`: that keys off non-empty goal text, and since step 3 the goal text is
    /// derived from task titles - so a "just track" day or a blank morning card produces NO goal
    /// text, and under the old rule neither midday check could ever fire. Being asked counts, even
    /// when the answer was nothing.
    /// </summary>
    private static bool DayHasBegun(DailyGoalSnapshot goal, Func<UserCheckInKind, bool> alreadyOfferedToday)
        => goal.MorningRecorded || alreadyOfferedToday(UserCheckInKind.Morning);

    private static bool IsPastPredictedEnd(DateTimeOffset nowLocal, UserCheckInDayTiming.WorkDaySnapshot day)
        => UserCheckInDayTiming.FractionAt(day, 1d) is { } end && nowLocal >= end;


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

    /// <summary>
    /// The per-day mute class used to mark a check-in kind as already offered today. This string is
    /// the ONLY thing preventing a card firing twice in a day, so every kind must have its own.
    ///
    /// DELIBERATELY EXHAUSTIVE - no `_` arm. This used to end in `_ => "checkin:eod"`, which meant a
    /// newly added kind silently shared the EVENING card's mute: whichever fired first would
    /// suppress the other, with no error anywhere to explain it. Adding MiddayClose is exactly the
    /// change that would have triggered that bug. CS8509 is promoted to an error in the csproj, so a
    /// fifth kind now fails the build here instead of shipping a silent alias.
    /// </summary>
    public static string MuteClassFor(UserCheckInKind kind) => kind switch
    {
        UserCheckInKind.Morning => "checkin:morning",
        UserCheckInKind.Midday => "checkin:midday",
        UserCheckInKind.MiddayClose => "checkin:midday_close",
        UserCheckInKind.EndOfDay => "checkin:eod"
    };
}
