namespace Porta.UserAgent.Interventions;

// Complaints 1 + 2 (2026-08-11) — WHEN each of the four cards is due.
//
// The old model had two defects the founder hit in the field on the same day:
//
//   1. The evening card fired at 18:04 because it read `nowLocal.Hour >= 18` and had no idea he was
//      still working. A card that closes the day before the day ends breaks the whole loop.
//   2. The first midday check fired after a fixed 2h of active work, regardless of what the user
//      had estimated. Asking "done?" at 2h on a task he said would take 3h can only ever get one
//      answer, and that is how a person learns to ignore a card.
//
// The model that replaces it:
//
//   * The three CLOCK-DRIVEN cards sit at fractions of the working day, measured in WALL CLOCK from
//     when work actually started — morning at the start, the second midday two thirds in, the
//     evening at the end. One rule; an 8h day and a 10h day need no special cases.
//   * The FIRST midday is driven by the user's own estimate instead: it is due when the active
//     task has spent the time he said it would take. That is not Ari inferring anything — it is
//     holding him to a number he chose.
//
// Two currencies, deliberately, and they are not interchangeable:
//   - WALL CLOCK for the fractions, because a day is a span of a day.
//   - ACTIVE WORK for the task trigger and the floor, because that is the only currency time is
//     ever credited to a task in (SqliteBrainStateStore.AttributeBucketToActiveTask: away buckets
//     contribute nothing). Measured at ~64% of wall clock in the one live sample, so the two run at
//     visibly different speeds and mixing them silently would misplace every card.
//
// Pure, I/O-free, with a reason code for every refusal — same shape as UserCheckInScheduler.
internal static class UserCheckInDayTiming
{
    /// <summary>Assumed length of a working day before anything has been learned. Wall clock.</summary>
    public static readonly TimeSpan DefaultDayLength = TimeSpan.FromHours(8);

    /// <summary>Clamps on the learned day length, so one strange week cannot produce a strange day.</summary>
    public static readonly TimeSpan MinDayLength = TimeSpan.FromHours(4);
    public static readonly TimeSpan MaxDayLength = TimeSpan.FromHours(12);

    /// <summary>
    /// Nothing before this much ACTIVE WORK, so three short tasks do not mean three interruptions
    /// in the first hour. Set by Yadin, 2026-08-11.
    /// </summary>
    public static readonly TimeSpan FirstMiddayActiveWorkFloor = TimeSpan.FromMinutes(90);

    /// <summary>
    /// When there is no estimate to expire (no active task), the first midday falls back to the
    /// fraction the four-card model would have given it anyway. Keeps one rule instead of two.
    /// </summary>
    public const double FirstMiddayFallbackFraction = 1d / 3d;

    /// <summary>
    /// The first midday must not drift into the second. Anchoring its ceiling at HALF the day
    /// guarantees a gap of a sixth of the day — 80 minutes on an 8h day — and scales automatically,
    /// where a fixed hour would break the moment the day is not 8 hours. It is needed because the
    /// two currencies run at different speeds: a 3h estimate is ~4h40m of wall clock at the
    /// measured 64% ratio, which would otherwise land on top of the two-thirds card.
    /// </summary>
    public const double FirstMiddayCeilingFraction = 0.5d;

    /// <summary>The second midday — the close-out card — sits two thirds through the day.</summary>
    public const double SecondMiddayFraction = 2d / 3d;

    /// <summary>
    /// Past the predicted end of the day the evening card waits for a REAL break rather than
    /// interrupting live work. "A few minutes genuinely idle" (Yadin, 2026-08-11).
    /// </summary>
    public static readonly TimeSpan EveningIdleBreak = TimeSpan.FromMinutes(5);

    /// <summary>
    /// ...but not forever. Two hours past the predicted end it fires regardless, because a day that
    /// is never closed is worse than one closed slightly early.
    /// </summary>
    public static readonly TimeSpan EveningHardCeiling = TimeSpan.FromHours(2);

    /// <summary>
    /// The timing facts the four cards need. Separate from DailyGoalSnapshot, which is about the
    /// goal ROW; this is about the shape of the day and the task currently being measured.
    /// </summary>
    internal readonly record struct WorkDaySnapshot(
        // First CAPTURED activity of the goal day. Comes from the service's timeline, NOT from when
        // the user agent process started: on 2026-08-09 the user agent was down until 14:03 after a
        // watchdog relaunch while the service captured throughout, so anchoring on the process would
        // have misplaced every card that day. null = nothing captured yet.
        DateTimeOffset? WorkStartLocal,
        // Learned from recent days, clamped. The user is never asked how long he intends to work —
        // that turns a glance into a meeting.
        TimeSpan DayLength,
        // The ACTIVE task's spent and budgeted time. Budget is planned + extended, so the widget's
        // +15 (ActOnTask "extend", +900s to extended_seconds) moves the check with it. null = no
        // task is active, so there is no estimate to expire.
        TimeSpan? ActiveTaskSpent,
        TimeSpan? ActiveTaskBudget,
        // Active work today (tracked minus away). Drives the floor.
        TimeSpan ActiveWorkToday,
        // How long the user has been continuously idle right now. Drives the evening break guard.
        TimeSpan IdleFor);

    /// <summary>A timing decision plus the reason it went that way. Reason codes are logged.</summary>
    internal readonly record struct TimingDecision(bool Due, string Reason)
    {
        public static TimingDecision Yes(string reason) => new(true, reason);
        public static TimingDecision No(string reason) => new(false, reason);
    }

    /// <summary>
    /// Clamps a learned day length into something sane, falling back to the default when there is
    /// no history yet. Pure so the learning source can change without touching the timing rules.
    /// </summary>
    public static TimeSpan ResolveDayLength(TimeSpan? learned)
    {
        if (learned is not { } value || value <= TimeSpan.Zero)
        {
            return DefaultDayLength;
        }

        if (value < MinDayLength) return MinDayLength;
        if (value > MaxDayLength) return MaxDayLength;
        return value;
    }

    /// <summary>The wall-clock instant a given fraction of the working day is reached.</summary>
    public static DateTimeOffset? FractionAt(WorkDaySnapshot day, double fraction)
        => day.WorkStartLocal is { } start
            ? start + TimeSpan.FromTicks((long)(day.DayLength.Ticks * fraction))
            : null;

    /// <summary>
    /// Complaint 1 — the FIRST midday check. Due when the active task has spent the time the user
    /// said it would take, subject to a floor and a ceiling.
    ///
    /// Order matters: the floor is checked before everything, because a 20-minute task expiring
    /// 20 minutes into the day must not produce a card.
    /// </summary>
    public static TimingDecision IsFirstMiddayDue(DateTimeOffset nowLocal, WorkDaySnapshot day)
    {
        if (day.ActiveWorkToday < FirstMiddayActiveWorkFloor)
        {
            return TimingDecision.No(
                $"below_active_work_floor:{(int)day.ActiveWorkToday.TotalMinutes}m<{(int)FirstMiddayActiveWorkFloor.TotalMinutes}m");
        }

        // Ceiling first among the "due" paths: once half the day has gone, it is due regardless of
        // what any estimate says, or it would drift into the two-thirds card.
        if (FractionAt(day, FirstMiddayCeilingFraction) is { } ceiling && nowLocal >= ceiling)
        {
            return TimingDecision.Yes("ceiling_half_day");
        }

        // The real trigger: the user's own estimate ran out on the task actually being measured.
        // Budget is planned + extended, so +15 on the widget moves this out by 15 minutes of work.
        if (day.ActiveTaskSpent is { } spent && day.ActiveTaskBudget is { } budget && budget > TimeSpan.Zero)
        {
            return spent >= budget
                ? TimingDecision.Yes($"estimate_spent:{(int)spent.TotalMinutes}m>={(int)budget.TotalMinutes}m")
                : TimingDecision.No($"estimate_remaining:{(int)(budget - spent).TotalMinutes}m");
        }

        // No active task, so no estimate to expire. Fall back to the fraction this card would have
        // had under the pure four-card model. Covers a "just track" day, a blank morning card, and
        // the gap after a task is marked done while nothing else is started.
        if (FractionAt(day, FirstMiddayFallbackFraction) is { } fallback)
        {
            return nowLocal >= fallback
                ? TimingDecision.Yes("no_active_task_fallback_third")
                : TimingDecision.No("no_active_task_before_third");
        }

        return TimingDecision.No("no_work_start");
    }

    /// <summary>Complaint 3 — the SECOND midday, the close-out card. Two thirds through the day.</summary>
    public static TimingDecision IsSecondMiddayDue(DateTimeOffset nowLocal, WorkDaySnapshot day)
    {
        if (FractionAt(day, SecondMiddayFraction) is not { } at)
        {
            return TimingDecision.No("no_work_start");
        }

        // Deliberately NOT conditional on the first midday having fired. If the first was deferred
        // to its ceiling and then something stopped it, this card must still arrive — and it says
        // the first never happened rather than pretending it did (see DescribeSecondMidday).
        return nowLocal >= at
            ? TimingDecision.Yes("two_thirds_of_day")
            : TimingDecision.No("before_two_thirds");
    }

    /// <summary>
    /// Complaint 2 — the EVENING card. Due at the end of the predicted day, but it must never close
    /// a day the user is still working through. Past the predicted end it waits for a real break;
    /// two hours past, it fires anyway.
    /// </summary>
    public static TimingDecision IsEveningDue(DateTimeOffset nowLocal, WorkDaySnapshot day)
    {
        if (FractionAt(day, 1d) is not { } predictedEnd)
        {
            return TimingDecision.No("no_work_start");
        }

        if (nowLocal < predictedEnd)
        {
            return TimingDecision.No($"before_day_end:{(int)(predictedEnd - nowLocal).TotalMinutes}m");
        }

        if (nowLocal >= predictedEnd + EveningHardCeiling)
        {
            return TimingDecision.Yes("hard_ceiling_two_hours_past");
        }

        // The guard that the 18:04 card lacked: active work, not the wall clock, decides whether
        // this is a moment to close the day.
        return day.IdleFor >= EveningIdleBreak
            ? TimingDecision.Yes($"real_break:{(int)day.IdleFor.TotalMinutes}m")
            : TimingDecision.No($"still_working:idle_{(int)day.IdleFor.TotalMinutes}m<{(int)EveningIdleBreak.TotalMinutes}m");
    }

    /// <summary>
    /// The one fact the second midday card needs so it can acknowledge a gap instead of pretending
    /// the day was tracked — same shape as UserCheckInScheduler.DescribeEndOfDay.
    /// </summary>
    public static bool DescribeSecondMidday(bool firstMiddayAnswered) => !firstMiddayAnswered;
}
