using Porta.Agent.Runtime;

namespace Porta.Agent.Runtime.Tests;

// Chat 3 (2026-08-24) — a failed idle read used to return 0 seconds, which is indistinguishable
// from "the user just typed". A broken idle channel therefore read as continuous activity and
// every bucket it touched was credited as work.
public sealed class IdleReadFallbackTests
{
    [Fact]
    public void ASuccessfulReadIsUsedAsIs()
        => Assert.Equal(
            42d,
            IdleReadFallback.Resolve(
                readSucceeded: true, rawSeconds: 42d,
                lastGoodSeconds: 5d, sinceLastGood: TimeSpan.FromSeconds(90)));

    [Fact]
    public void ANegativeReadingIsFlooredAtZero()
        => Assert.Equal(
            0d,
            IdleReadFallback.Resolve(
                readSucceeded: true, rawSeconds: -3d,
                lastGoodSeconds: null, sinceLastGood: TimeSpan.Zero));

    // The whole point: a failure must never look like activity.
    [Fact]
    public void AFailedReadDoesNotReturnZero()
    {
        double resolved = IdleReadFallback.Resolve(
            readSucceeded: false, rawSeconds: 0d,
            lastGoodSeconds: 30d, sinceLastGood: TimeSpan.FromSeconds(60));

        Assert.NotEqual(0d, resolved);
    }

    // Idle was 30s a minute ago and nothing has been measurable since, so the user has been idle
    // at least 90s. The clock keeps running rather than resetting on every failed poll.
    [Fact]
    public void AFailedReadContinuesTheIdleClockFromTheLastGoodReading()
        => Assert.Equal(
            90d,
            IdleReadFallback.Resolve(
                readSucceeded: false, rawSeconds: 0d,
                lastGoodSeconds: 30d, sinceLastGood: TimeSpan.FromSeconds(60)));

    [Fact]
    public void TheIdleClockKeepsGrowingWhileTheChannelStaysBroken()
    {
        double after1m = IdleReadFallback.Resolve(false, 0d, 30d, TimeSpan.FromMinutes(1));
        double after10m = IdleReadFallback.Resolve(false, 0d, 30d, TimeSpan.FromMinutes(10));

        Assert.True(after10m > after1m, "a longer outage must read as more idle, not the same");
    }

    // Broken from the very first poll: there is no history to continue, so assume idle. Assuming
    // ACTIVE would credit a whole session to a task on the strength of a failed API call.
    [Fact]
    public void WithNoHistoryAtAll_ItAssumesIdle()
        => Assert.Equal(
            IdleReadFallback.AssumeIdleSeconds,
            IdleReadFallback.Resolve(
                readSucceeded: false, rawSeconds: 0d,
                lastGoodSeconds: null, sinceLastGood: TimeSpan.Zero));

    [Fact]
    public void TheNoHistoryFallbackIsLongEnoughToReadAsAway()
        => Assert.True(
            IdleReadFallback.AssumeIdleSeconds >= 120d,
            "the fallback must be long enough that the pipeline does not read it as presence");
}
