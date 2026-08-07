namespace Porta.UserAgent.Interventions;

/// <summary>The card currently on screen and unanswered, as the staleness rule sees it.</summary>
internal readonly record struct OpenCheckInCard(
    UserCheckInKind Kind,
    DateTimeOffset ShownAtLocal);

/// <summary>
/// Whether the open card should be expired to make way for the one that is now due, and why.
/// A reason code is carried for BOTH outcomes so the log explains a refusal as clearly as an
/// expiry — the same contract <see cref="UserCheckInScheduler"/> follows.
/// </summary>
internal readonly record struct StaleCardDecision(bool Expire, string ReasonCode);

// Step 2 — pure decision: has an open, unanswered check-in card outlived its day?
//
// The defect it fixes, reproduced from the live log on 2026-08-06:
//
//     2026-08-05 16:40:32  checkin_card_shown   kind=EndOfDay
//     2026-08-06 07:37:21  checkin_dismissed    kind=EndOfDay   <- dismissed the NEXT MORNING
//     2026-08-06 07:37:24  checkin_offered      kind=Morning
//
// The EOD card fired to an empty chair, nothing ever expired it, and UserCheckInCardPresenter
// refuses a second card while one is unanswered (checkin_card_busy). So the morning card — the
// highest-value three seconds in the product — sat behind last night's card until the user
// dismissed it by hand.
//
// The rule is a goal-day rollover rather than an age in hours: a card belongs to the day it was
// shown on, and any card from a previous goal day is stale no matter how few hours have passed.
// That reuses a boundary the product already has instead of introducing a constant to tune, and
// it cannot mis-fire during a long evening (22:00 -> 23:00 is one day; 23:00 -> 05:30 is two).
//
// No I/O, no UI, no clock of its own, so it is fully unit-tested.
internal static class UserCheckInStalenessPolicy
{
    /// <summary>
    /// The goal day starts at 05:00 local — a card shown at 02:00 still belongs to the previous
    /// day. Mirrors <c>AgentCoreController.GoalDay</c>, which is the source of truth but is
    /// internal to Porta.Agent.Core and not referenced by this project; NarrativeGateway carries
    /// the same duplicate for the same reason. Keep the three in step.
    /// </summary>
    public const int GoalDayBoundaryHour = 5;

    /// <summary>
    /// Decides whether the currently open card should be expired so a newly due check-in can show.
    /// The caller supplies local now; this function never reads a clock.
    /// </summary>
    public static StaleCardDecision Evaluate(OpenCheckInCard? openCard, DateTimeOffset nowLocal)
    {
        if (openCard is not { } card)
        {
            // Nothing on screen — the caller is free to show, and there is nothing to expire.
            return new StaleCardDecision(Expire: false, ReasonCode: "no_open_card");
        }

        if (nowLocal < card.ShownAtLocal)
        {
            // Clock moved backwards (DST, NTP correction, manual change). Refuse rather than
            // expire: an unanswered card the user may be mid-answer on is worth more than a
            // punctual replacement, and a backwards clock cannot prove staleness.
            return new StaleCardDecision(Expire: false, ReasonCode: "clock_went_backwards");
        }

        if (GoalDayOf(card.ShownAtLocal) == GoalDayOf(nowLocal))
        {
            // Same goal day. A midday card still open when EOD falls due is deliberately NOT
            // expired here: that is a same-day question about which card should win, not a card
            // that has outlived its day, and conflating the two would let a still-relevant card
            // be yanked out from under the user.
            return new StaleCardDecision(Expire: false, ReasonCode: "same_goal_day");
        }

        return new StaleCardDecision(Expire: true, ReasonCode: "stale_goal_day_rollover");
    }

    /// <summary>The 05:00-boundary goal day a local timestamp belongs to, as "yyyy-MM-dd".</summary>
    public static string GoalDayOf(DateTimeOffset local)
        => (local.Hour < GoalDayBoundaryHour ? local.AddDays(-1) : local).ToString("yyyy-MM-dd");
}
