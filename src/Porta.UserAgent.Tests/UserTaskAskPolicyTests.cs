using System;
using Porta.UserAgent.Interventions;
using Xunit;

namespace Porta.UserAgent.Tests;

// Complaints 4 + 5 — the rationing of the estimate-expiry ask. Every value here was set by Yadin on
// 2026-08-12; a change to one of them should fail a test with his name on it, not pass quietly.
public class UserTaskAskPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 11, 0, 0, TimeSpan.FromHours(4));

    private static UserTaskAskPolicy.TaskAskContext Context(
        DateTimeOffset? lastInterruption = null,
        int asksDelivered = 0,
        DateTimeOffset? nextCardDue = null,
        bool eveningDueWaiting = false,
        DateTimeOffset? firstSeen = null)
        => new(
            LastInterruptionLocal: lastInterruption,
            AsksDeliveredToday: asksDelivered,
            NextScheduledCardDueLocal: nextCardDue,
            EveningDueWaiting: eveningDueWaiting,
            FirstSeenLocal: firstSeen ?? Now);

    [Fact]
    public void Asks_WhenNothingIsInTheWay()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(Now, Context());

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Ask, decision.Outcome);
        Assert.Equal("estimate_spent_asked:1/3", decision.Reason);
    }

    // ── The floor: 45 minutes, wall clock, from ANY interruption ────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(44)]
    public void Holds_WithinFortyFiveMinutesOfTheLastInterruption(int minutesSince)
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(lastInterruption: Now.AddMinutes(-minutesSince)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Hold, decision.Outcome);
        Assert.StartsWith("within_floor:", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Asks_OnceTheFloorHasPassed()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(lastInterruption: Now.AddMinutes(-45)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Ask, decision.Outcome);
    }

    // The floor counts scheduled CARDS, not only other asks — a card at 10:00 and an ask at 10:25
    // is the double-interruption being banned. The host sets LastInterruption on every shown card.
    [Fact]
    public void Holds_TwentyFiveMinutesAfterACard()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(lastInterruption: Now.AddMinutes(-25)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Hold, decision.Outcome);
    }

    // Held, never dropped: the estimate is still spent. If the user settles it himself in the
    // meantime the task stops being active and the caller never asks at all.
    [Fact]
    public void TheFloorHoldsRatherThanDrops()
    {
        Assert.Equal(
            UserTaskAskPolicy.TaskAskOutcome.Hold,
            UserTaskAskPolicy.Decide(Now, Context(lastInterruption: Now.AddMinutes(-1))).Outcome);
    }

    // ── The cap: three delivered asks a day ────────────────────────────────────────────────

    [Fact]
    public void Drops_OnceThreeAsksHaveBeenDelivered()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(asksDelivered: 3));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Drop, decision.Outcome);
        Assert.Equal("daily_cap:3>=3", decision.Reason);
    }

    [Fact]
    public void Asks_OnTheThirdOfTheDay()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(asksDelivered: 2));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Ask, decision.Outcome);
        Assert.Equal("estimate_spent_asked:3/3", decision.Reason);
    }

    // ── The fold: 20 minutes before a scheduled card ───────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(19)]
    [InlineData(20)]
    public void Folds_WhenACardIsDueWithinTwentyMinutes(int minutesUntilCard)
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(nextCardDue: Now.AddMinutes(minutesUntilCard)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Fold, decision.Outcome);
        Assert.StartsWith("card_due_in:", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Asks_WhenTheCardIsStillWellAway()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(nextCardDue: Now.AddMinutes(21)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Ask, decision.Outcome);
    }

    // A folded ask costs no interruption, so it must not spend the day's allowance — and it must
    // outrank the floor, or a card and an ask could still both arrive.
    [Fact]
    public void TheFoldOutranksTheCapAndTheFloor()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now,
            Context(
                lastInterruption: Now.AddMinutes(-2),
                asksDelivered: 3,
                nextCardDue: Now.AddMinutes(10)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Fold, decision.Outcome);
    }

    // The estimate-driven first midday: the host passes "now", because this very expiry is what
    // fires that card. Without the fold the first expiry of the day would produce a pill AND the
    // card it triggered.
    [Fact]
    public void Folds_IntoTheCardThisVeryExpiryIsAboutToFire()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(Now, Context(nextCardDue: Now));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Fold, decision.Outcome);
        Assert.StartsWith("card_overdue_still_folding:", decision.Reason, StringComparison.Ordinal);
    }

    // Rule 4 — a held ask is not a queue. A card suppressed by the meeting shield or a failed
    // presentation check must not silently swallow the expiry.
    [Fact]
    public void ReleasesTheFold_WhenTheCardNeverCame()
    {
        DateTimeOffset firstSeen = Now.AddMinutes(-41);

        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(nextCardDue: Now.AddMinutes(-30), firstSeen: firstSeen));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Ask, decision.Outcome);
    }

    [Fact]
    public void KeepsFolding_WhileTheCardIsMerelyLate()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(nextCardDue: Now.AddMinutes(-5), firstSeen: Now.AddMinutes(-10)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Fold, decision.Outcome);
    }

    // ── The evening stand-down ─────────────────────────────────────────────────────────────

    // The evening card waits up to two hours for a real break, so its fire time cannot be predicted
    // and no ask may fire beside it. It outranks every other rule, including the fold.
    [Fact]
    public void Drops_OnceTheEveningCardIsDueAndWaiting()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(eveningDueWaiting: true, nextCardDue: Now.AddMinutes(90)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Drop, decision.Outcome);
        Assert.Equal("evening_due_waiting", decision.Reason);
    }

    [Fact]
    public void TheEveningStandDownOutranksAFoldableCard()
    {
        UserTaskAskPolicy.TaskAskDecision decision = UserTaskAskPolicy.Decide(
            Now, Context(eveningDueWaiting: true, nextCardDue: Now.AddMinutes(5)));

        Assert.Equal(UserTaskAskPolicy.TaskAskOutcome.Drop, decision.Outcome);
    }

    // ── The settled values themselves ──────────────────────────────────────────────────────

    [Fact]
    public void TheSettledValuesAreWhatWasAgreed()
    {
        Assert.Equal(TimeSpan.FromMinutes(45), UserTaskAskPolicy.MinGapBetweenInterruptions);
        Assert.Equal(3, UserTaskAskPolicy.MaxAsksPerDay);
        Assert.Equal(TimeSpan.FromMinutes(20), UserTaskAskPolicy.FoldWindow);
    }
}
