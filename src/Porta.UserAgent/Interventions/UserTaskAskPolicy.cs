namespace Porta.UserAgent.Interventions;

// Complaints 4 + 5 (settled 2026-08-12) — "if I finish a task, or start a new one, without pressing
// anything on the widget, Ari doesn't know."
//
// The locked rule holds: Ari NEVER infers how time splits across tasks. So this is not detection of
// what the user is working on. It is one question, asked at the only honest moment there is — the
// moment his OWN estimate runs out — which holds him to a number he chose rather than guessing at
// one.
//
// WHAT WAS ALREADY HERE, and why it did not close the loop: `UserAgentHost.RefreshTasksAsync` has
// fired a toast on every expiry, once per (task id, planned+extended budget), since Workstream 4.
// The toast said "mark it done, add 15 minutes, or switch, FROM THE WIDGET" — it notified about the
// ask instead of being the ask, so answering it still cost exactly the trip to the widget that the
// complaint is about. The widget has likewise printed "12m over" on the row all along. Both are
// passive, both shipped weeks ago, and neither changed anything. That is the evidence the design
// turns on (Yadin, 2026-08-12).
//
// So the cadence is not new. What is new is that the ask is answerable where it appears, and that it
// is rationed — because an ask that can arrive at any moment becomes the next thing to be ignored.
//
// THE RATIONING, set by Yadin 2026-08-12:
//
//   * 45 minutes between interruptions — measured on the WALL CLOCK, not on active work. The
//     codebase keeps two currencies deliberately (UserCheckInDayTiming), and a task trigger would
//     normally use active work; this one must not. A floor against pestering is about how often the
//     user is disturbed, which is felt in real time. In active work 45m would mean ~70m of wall
//     clock at the measured 64% ratio — a far bigger gap than he asked for. It cannot be gamed by
//     idling either: a second expiry needs more ACTIVE work regardless, so the two guards compose.
//
//   * The floor counts the four scheduled cards as interruptions too, not just other asks. A card
//     at 10:00 and an ask at 10:25 is precisely the double-interruption being banned.
//
//   * Three delivered asks per goal-day, beyond the scheduled cards. A FOLDED ask does not count:
//     it cost no separate interruption, which is the only thing the cap exists to limit.
//
//   * An expiry within 20 minutes BEFORE a scheduled card folds into that card. The card already
//     puts every task in front of the user with one-tap done, so it can carry the question.
//
//   * Once the evening card is due-but-waiting-for-a-break, the ask stands down entirely. The
//     evening card's fire time cannot be predicted (it waits up to two hours for a real break), so
//     "20 minutes before" is not computable for it, and it is about to ask about every task anyway.
//
// NOTHING here reassigns measured time. When the user answers "switched", the time already credited
// stays exactly where it was measured and only the future changes — dropping it would make the day
// stop adding up (the bug 1.2.139 just fixed) and asking him to remember it would put an invented
// number in a client's report. Decision: Yadin, 2026-08-12.
//
// Pure, I/O-free, with a reason code for every outcome — same shape as UserCheckInDayTiming and
// UserCheckInScheduler.
internal static class UserTaskAskPolicy
{
    /// <summary>Minimum WALL CLOCK between any two interruptions — asks and scheduled cards alike.</summary>
    public static readonly TimeSpan MinGapBetweenInterruptions = TimeSpan.FromMinutes(45);

    /// <summary>Delivered asks allowed per goal-day, over and above the four scheduled cards.</summary>
    public const int MaxAsksPerDay = 3;

    /// <summary>
    /// An expiry this close before a scheduled card is carried BY that card instead of arriving on
    /// its own.
    /// </summary>
    public static readonly TimeSpan FoldWindow = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long a folded expiry waits for its card before it is released back to the normal path.
    /// A held ask is not a queue: if the card never appears — meeting shield, critical state, a
    /// presentation check that fails — the expiry must not be silently swallowed. Sized as the fold
    /// window plus the same again, so a card that is merely late still gets to carry it.
    /// </summary>
    public static readonly TimeSpan FoldReleaseGrace = TimeSpan.FromMinutes(40);

    internal enum TaskAskOutcome
    {
        /// <summary>Deliver the ask now, on the widget.</summary>
        Ask,

        /// <summary>A card is imminent: say nothing and let the card carry the question.</summary>
        Fold,

        /// <summary>Not now, but still true — re-evaluate on the next poll.</summary>
        Hold,

        /// <summary>Nothing to ask about, or the day's allowance is gone. Do not ask again today.</summary>
        Drop
    }

    internal readonly record struct TaskAskDecision(TaskAskOutcome Outcome, string Reason)
    {
        public static TaskAskDecision Ask(string reason) => new(TaskAskOutcome.Ask, reason);
        public static TaskAskDecision Fold(string reason) => new(TaskAskOutcome.Fold, reason);
        public static TaskAskDecision Hold(string reason) => new(TaskAskOutcome.Hold, reason);
        public static TaskAskDecision Drop(string reason) => new(TaskAskOutcome.Drop, reason);
    }

    /// <summary>
    /// Everything the decision needs. Deliberately NOT the task itself: the caller has already
    /// established that an active task's budget is spent, so this is only about whether the moment
    /// is a fair one to speak.
    /// </summary>
    internal readonly record struct TaskAskContext(
        // The last time ANYTHING interrupted the user — a scheduled card or an earlier ask. Null =
        // nothing yet today.
        DateTimeOffset? LastInterruptionLocal,
        // Asks actually DELIVERED today. Folded ones are not counted.
        int AsksDeliveredToday,
        // When the next of the four scheduled cards is due, if one is still to come. Null when the
        // day has no card left to fold into.
        DateTimeOffset? NextScheduledCardDueLocal,
        // True once the evening card is past its due time and waiting for a real break.
        bool EveningDueWaiting,
        // When this expiry was first seen. Drives the release of a fold whose card never came.
        DateTimeOffset FirstSeenLocal);

    /// <summary>
    /// Decides whether the expiry the caller is holding may be spoken now.
    ///
    /// Order matters and encodes the settled rules: the evening stand-down outranks everything
    /// (the day is about to be closed), then the fold (a card is imminent, and folding costs
    /// nothing), then the floor and the cap (which only apply to asks that would actually
    /// interrupt).
    /// </summary>
    public static TaskAskDecision Decide(DateTimeOffset nowLocal, TaskAskContext ctx)
    {
        // Rule 3 — the evening card is due and waiting for a break. It will ask about every task,
        // so nothing separate may fire, however long the wait turns out to be.
        if (ctx.EveningDueWaiting)
        {
            return TaskAskDecision.Drop("evening_due_waiting");
        }

        // Rule 1 — fold forward. Checked BEFORE the floor and the cap: a folded ask spends neither.
        if (ctx.NextScheduledCardDueLocal is { } cardDue)
        {
            TimeSpan untilCard = cardDue - nowLocal;
            if (untilCard > TimeSpan.Zero && untilCard <= FoldWindow)
            {
                return TaskAskDecision.Fold($"card_due_in:{(int)untilCard.TotalMinutes}m");
            }

            // Rule 4 — a held ask is not a queue. Past the card's due time the fold only stands
            // while the card still might appear; after the grace it is released to the normal path
            // rather than swallowed by a card that never showed.
            if (untilCard <= TimeSpan.Zero && nowLocal - ctx.FirstSeenLocal < FoldReleaseGrace)
            {
                return TaskAskDecision.Fold($"card_overdue_still_folding:{(int)(nowLocal - ctx.FirstSeenLocal).TotalMinutes}m");
            }
        }

        // Rule 2 — the floor, counting scheduled cards as interruptions. Held, not dropped: the
        // estimate is still spent, and if the user settles it himself in the meantime the task
        // stops being active and the caller never asks again.
        if (ctx.LastInterruptionLocal is { } last)
        {
            TimeSpan since = nowLocal - last;
            if (since < MinGapBetweenInterruptions)
            {
                return TaskAskDecision.Hold(
                    $"within_floor:{(int)since.TotalMinutes}m<{(int)MinGapBetweenInterruptions.TotalMinutes}m");
            }
        }

        // The daily allowance. Dropped rather than held: it cannot come back today, and holding
        // would re-evaluate a decision that can no longer change.
        if (ctx.AsksDeliveredToday >= MaxAsksPerDay)
        {
            return TaskAskDecision.Drop($"daily_cap:{ctx.AsksDeliveredToday}>={MaxAsksPerDay}");
        }

        return TaskAskDecision.Ask($"estimate_spent_asked:{ctx.AsksDeliveredToday + 1}/{MaxAsksPerDay}");
    }
}
