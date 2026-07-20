using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Part 1c — the pure settle-in state machine: a check-in is held until the user has been
// continuously active for SettleInDelay after a resume. Advance() tracks the "active since"
// timestamp; WithinSettleWindow() answers whether we are still inside the hold.
public sealed class UserCheckInActivityGateTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);
    private static DateTimeOffset At(int minute, int second = 0) => new(2026, 7, 16, 9, minute, second, Off);
    private static readonly TimeSpan Delay = UserCheckInActivityGate.SettleInDelay;

    [Fact]
    public void Advance_StartsCounting_OnFirstActiveTick()
    {
        DateTimeOffset? state = null;
        state = UserCheckInActivityGate.Advance(state, At(0), activeNow: true);
        Assert.Equal(At(0), state);
    }

    [Fact]
    public void Advance_KeepsTheOriginalStart_WhileStayingActive()
    {
        DateTimeOffset? state = null;
        state = UserCheckInActivityGate.Advance(state, At(0), activeNow: true);
        state = UserCheckInActivityGate.Advance(state, At(1), activeNow: true);
        state = UserCheckInActivityGate.Advance(state, At(2), activeNow: true);
        Assert.Equal(At(0), state); // window measured from the first active tick, not the latest
    }

    [Fact]
    public void Advance_Resets_OnIdleOrAway()
    {
        DateTimeOffset? state = At(0);
        state = UserCheckInActivityGate.Advance(state, At(1), activeNow: false);
        Assert.Null(state);
    }

    [Fact]
    public void Advance_LeavesStateUntouched_WhenSignalUnknown()
    {
        // Live session momentarily unavailable (null) must neither start nor reset the window.
        DateTimeOffset? counting = At(0);
        Assert.Equal(At(0), UserCheckInActivityGate.Advance(counting, At(1), activeNow: null));

        DateTimeOffset? notCounting = null;
        Assert.Null(UserCheckInActivityGate.Advance(notCounting, At(1), activeNow: null));
    }

    [Fact]
    public void WithinSettleWindow_HoldsBeforeThreshold_FiresAfter()
    {
        DateTimeOffset start = At(0);
        // Just before the threshold → still holding.
        Assert.True(UserCheckInActivityGate.WithinSettleWindow(start, start + Delay - TimeSpan.FromSeconds(1), Delay));
        // At/after the threshold → no longer holding.
        Assert.False(UserCheckInActivityGate.WithinSettleWindow(start, start + Delay, Delay));
        Assert.False(UserCheckInActivityGate.WithinSettleWindow(start, start + Delay + TimeSpan.FromMinutes(1), Delay));
    }

    [Fact]
    public void WithinSettleWindow_DoesNotHold_WhenNoActiveRun()
    {
        // Fail-soft: an unset timestamp (unknown activity) must not wedge a check-in permanently.
        Assert.False(UserCheckInActivityGate.WithinSettleWindow(null, At(30), Delay));
    }

    [Fact]
    public void ResumeAfterIdle_RestartsTheWindow()
    {
        // Active, then idle (reset), then active again → the window restarts from the return.
        DateTimeOffset? state = null;
        state = UserCheckInActivityGate.Advance(state, At(0), activeNow: true);
        state = UserCheckInActivityGate.Advance(state, At(10), activeNow: false); // idle → reset
        state = UserCheckInActivityGate.Advance(state, At(20), activeNow: true);  // resume
        Assert.Equal(At(20), state);
        Assert.True(UserCheckInActivityGate.WithinSettleWindow(state, At(21), Delay));  // held right after resume
        Assert.False(UserCheckInActivityGate.WithinSettleWindow(state, At(20) + Delay, Delay)); // fires after settle
    }
}
