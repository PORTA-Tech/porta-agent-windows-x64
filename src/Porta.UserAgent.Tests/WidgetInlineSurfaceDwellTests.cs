using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Chat 6 (2026-08-24) — "a card appears then is immediately replaced".
//
// The inline surface was a bare priority ladder with no queue and no dwell: it is re-evaluated on
// every state push AND again after every resolve, so answering one card dropped straight into the
// next in the same frame. Two different questions in the same place with no pause reads as one card
// being replaced mid-glance — and the second gets answered by a tap aimed at the first.
public sealed class WidgetInlineSurfaceDwellTests
{
    // The dwell has to be long enough to register as a gap and short enough not to feel doled out.
    [Fact]
    public void TheDwellIsLongEnoughToSee()
    {
        Assert.True(
            UserDesktopWidgetWindow.InlineSurfaceDwellForTests >= TimeSpan.FromSeconds(1),
            "a gap under a second is not a gap, it is a flicker");

        Assert.True(
            UserDesktopWidgetWindow.InlineSurfaceDwellForTests <= TimeSpan.FromSeconds(2),
            "longer than this and a queue of cards feels like it is being rationed");
    }

    // ── the rule, isolated from WPF ───────────────────────────────────────────────────

    // The window itself needs an STA thread and a real HWND, so the DECISION is tested here and the
    // wiring is shown through the widget harness. What matters is the shape: after a resolve the
    // surface is held, and it frees itself without needing another state push.
    [Fact]
    public void AfterAResolve_TheSurfaceIsHeld()
    {
        DateTimeOffset resolvedAt = new(2026, 08, 24, 17, 0, 0, TimeSpan.Zero);
        DateTimeOffset freeAt = resolvedAt + UserDesktopWidgetWindow.InlineSurfaceDwellForTests;

        Assert.True(UserDesktopWidgetWindow.InlineSurfaceHeldAt(resolvedAt.AddMilliseconds(1), freeAt));
        Assert.True(UserDesktopWidgetWindow.InlineSurfaceHeldAt(
            resolvedAt + TimeSpan.FromMilliseconds(600), freeAt));
    }

    [Fact]
    public void OnceTheDwellHasPassed_TheNextCardMayTakeTheSurface()
    {
        DateTimeOffset resolvedAt = new(2026, 08, 24, 17, 0, 0, TimeSpan.Zero);
        DateTimeOffset freeAt = resolvedAt + UserDesktopWidgetWindow.InlineSurfaceDwellForTests;

        Assert.False(UserDesktopWidgetWindow.InlineSurfaceHeldAt(freeAt, freeAt));
        Assert.False(UserDesktopWidgetWindow.InlineSurfaceHeldAt(freeAt.AddSeconds(1), freeAt));
    }

    // Nothing resolved yet: the very first card of a session must not be delayed.
    [Fact]
    public void WithNothingResolved_TheSurfaceIsFree()
        => Assert.False(UserDesktopWidgetWindow.InlineSurfaceHeldAt(
            DateTimeOffset.UtcNow, DateTimeOffset.MinValue));

    // Three cards resolving in sequence: each resolve re-arms the hold from ITS moment, so the gap
    // is between consecutive cards rather than a single window from the first one.
    [Fact]
    public void EachResolveRearmsTheHoldFromItsOwnMoment()
    {
        TimeSpan dwell = UserDesktopWidgetWindow.InlineSurfaceDwellForTests;
        DateTimeOffset first = new(2026, 08, 24, 17, 0, 0, TimeSpan.Zero);
        DateTimeOffset second = first + dwell + TimeSpan.FromMilliseconds(50);

        // The first hold has expired by the time the second card is answered...
        Assert.False(UserDesktopWidgetWindow.InlineSurfaceHeldAt(second, first + dwell));

        // ...and answering it starts a fresh gap rather than letting the third card straight in.
        Assert.True(UserDesktopWidgetWindow.InlineSurfaceHeldAt(
            second + TimeSpan.FromMilliseconds(100), second + dwell));
    }
}
