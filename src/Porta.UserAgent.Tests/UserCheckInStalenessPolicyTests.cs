using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Step 2 — the goal-day rollover rule that expires a card which outlived its day, so last
// night's unanswered card can never sit in front of this morning's. Pure function, so every
// case is pinned here rather than reasoned about at a live 05:00 boundary.
public sealed class UserCheckInStalenessPolicyTests
{
    private static DateTimeOffset Local(string iso) => DateTimeOffset.Parse(iso);

    [Fact]
    public void NoOpenCard_DoesNotExpire_AndSaysWhy()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            openCard: null, nowLocal: Local("2026-08-06T07:37:00+04:00"));

        Assert.False(decision.Expire);
        Assert.Equal("no_open_card", decision.ReasonCode);
    }

    // The exact sequence from the live log on 2026-08-06: an EOD card shown at 16:40 the previous
    // evening, still unanswered when the morning check-in fell due at 07:37. This is the case the
    // whole rule exists for.
    [Fact]
    public void EodCardLeftOpenOvernight_IsStaleWhenTheMorningCardFallsDue()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.EndOfDay, Local("2026-08-05T16:40:32+04:00")),
            Local("2026-08-06T07:37:24+04:00"));

        Assert.True(decision.Expire);
        Assert.Equal("stale_goal_day_rollover", decision.ReasonCode);
    }

    [Fact]
    public void CardShownEarlierToday_IsNotStale()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.Morning, Local("2026-08-06T07:30:00+04:00")),
            Local("2026-08-06T11:15:00+04:00"));

        Assert.False(decision.Expire);
        Assert.Equal("same_goal_day", decision.ReasonCode);
    }

    // A midday card still open when EOD falls due is the same goal day. Deliberately NOT expired:
    // that is a question about which card should win, not a card that has outlived its day.
    [Fact]
    public void MiddayCardStillOpenWhenEodFallsDue_IsNotExpiredByThisRule()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.Midday, Local("2026-08-06T12:00:00+04:00")),
            Local("2026-08-06T18:05:00+04:00"));

        Assert.False(decision.Expire);
        Assert.Equal("same_goal_day", decision.ReasonCode);
    }

    // 23:00 -> 02:00 crosses midnight but NOT the 05:00 goal-day boundary, so it is still one day.
    // An age-in-hours rule would have to pick a constant that gets this case wrong in one
    // direction or the other; the goal-day rule gets it right without one.
    [Fact]
    public void LateNightCardBeforeTheFiveAmBoundary_IsStillTheSameGoalDay()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.EndOfDay, Local("2026-08-05T23:00:00+04:00")),
            Local("2026-08-06T02:00:00+04:00"));

        Assert.False(decision.Expire);
        Assert.Equal("same_goal_day", decision.ReasonCode);
    }

    // The same three-hour gap, moved across 05:00, IS a rollover. Three hours is stale or not
    // depending on which side of the boundary it falls — which is exactly the point.
    [Fact]
    public void ThreeHourGapAcrossTheFiveAmBoundary_IsARollover()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.EndOfDay, Local("2026-08-06T03:30:00+04:00")),
            Local("2026-08-06T06:30:00+04:00"));

        Assert.True(decision.Expire);
        Assert.Equal("stale_goal_day_rollover", decision.ReasonCode);
    }

    [Fact]
    public void OneMinuteBeforeTheBoundary_IsNotYetARollover()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.EndOfDay, Local("2026-08-05T20:00:00+04:00")),
            Local("2026-08-06T04:59:00+04:00"));

        Assert.False(decision.Expire);
        Assert.Equal("same_goal_day", decision.ReasonCode);
    }

    [Fact]
    public void OneMinuteAfterTheBoundary_IsARollover()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.EndOfDay, Local("2026-08-05T20:00:00+04:00")),
            Local("2026-08-06T05:01:00+04:00"));

        Assert.True(decision.Expire);
        Assert.Equal("stale_goal_day_rollover", decision.ReasonCode);
    }

    [Fact]
    public void CardOpenForSeveralDays_IsStale()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.Morning, Local("2026-08-01T09:00:00+04:00")),
            Local("2026-08-06T09:00:00+04:00"));

        Assert.True(decision.Expire);
        Assert.Equal("stale_goal_day_rollover", decision.ReasonCode);
    }

    // A backwards clock (DST, NTP correction, manual change) cannot prove staleness, and an
    // unanswered card the user may be mid-answer on is worth more than a punctual replacement.
    [Fact]
    public void ClockMovedBackwards_RefusesRatherThanExpiring()
    {
        StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(
            new OpenCheckInCard(UserCheckInKind.Morning, Local("2026-08-06T09:00:00+04:00")),
            Local("2026-08-05T09:00:00+04:00"));

        Assert.False(decision.Expire);
        Assert.Equal("clock_went_backwards", decision.ReasonCode);
    }

    [Fact]
    public void EveryOutcomeCarriesANonEmptyReasonCode()
    {
        var cases = new (OpenCheckInCard? Card, DateTimeOffset Now)[]
        {
            (null, Local("2026-08-06T07:00:00+04:00")),
            (new OpenCheckInCard(UserCheckInKind.Morning, Local("2026-08-06T07:00:00+04:00")), Local("2026-08-06T09:00:00+04:00")),
            (new OpenCheckInCard(UserCheckInKind.EndOfDay, Local("2026-08-05T18:00:00+04:00")), Local("2026-08-06T07:00:00+04:00")),
            (new OpenCheckInCard(UserCheckInKind.Midday, Local("2026-08-06T12:00:00+04:00")), Local("2026-08-05T12:00:00+04:00")),
        };

        foreach ((OpenCheckInCard? card, DateTimeOffset now) in cases)
        {
            StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(card, now);
            Assert.False(string.IsNullOrWhiteSpace(decision.ReasonCode));
        }
    }

    [Theory]
    [InlineData("2026-08-06T04:59:59+04:00", "2026-08-05")] // before 05:00 -> previous day
    [InlineData("2026-08-06T05:00:00+04:00", "2026-08-06")] // the boundary itself -> that day
    [InlineData("2026-08-06T23:59:59+04:00", "2026-08-06")]
    [InlineData("2026-08-06T00:00:00+04:00", "2026-08-05")]
    public void GoalDayOf_UsesTheFiveAmBoundary(string local, string expected)
        => Assert.Equal(expected, UserCheckInStalenessPolicy.GoalDayOf(DateTimeOffset.Parse(local)));
}
