using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Porta.UserAgent.Tests;

/// <summary>
/// Presses a control the way a USER presses it, and refuses to press one a user could not.
///
/// WHY THIS EXISTS (2026-08-13). Twelve project-picker tests passed against a picker that read as
/// completely dead on the real card. They passed because they all did
/// <c>RaiseEvent(ButtonBase.ClickEvent)</c>, which invokes the handler directly and <b>bypasses hit
/// testing, layout and visibility entirely</b>. Every one of them proved "the handler works when
/// called". None proved "pressing it does anything".
///
/// That is the same shape as the caret bug: every focus test asserted which control was ARMED and
/// none asserted the caret actually ARRIVED. The lesson did not generalise the first time, so it
/// is a helper now rather than a note.
///
/// <see cref="Press"/> fails if the control is not visible, not enabled, not hit-test-visible,
/// has no area, sits outside the window, or is covered by something else — and only then raises
/// the click. Card tests should use it instead of RaiseEvent, so a control that cannot be pressed
/// fails the test that claims to press it.
/// </summary>
internal static class CardInteraction
{
    /// <summary>Presses the control, asserting first that a real click there would reach it.</summary>
    internal static void Press(Window window, FrameworkElement target, string what)
    {
        AssertPressable(window, target, what);
        target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>
    /// Everything that has to be true for a press to land, checked against the live visual tree
    /// rather than the logical one. Exposed separately so a test can assert pressability without
    /// actually pressing.
    /// </summary>
    internal static void AssertPressable(Window window, FrameworkElement target, string what)
    {
        Assert.True(target.IsVisible, $"{what}: not visible");
        Assert.True(target.IsEnabled, $"{what}: not enabled");
        Assert.True(target.IsHitTestVisible, $"{what}: IsHitTestVisible is false");
        Assert.True(
            target.ActualWidth > 0 && target.ActualHeight > 0,
            $"{what}: has no area ({target.ActualWidth}x{target.ActualHeight})");

        Point topLeft = target.TranslatePoint(new Point(0, 0), window);
        Assert.True(
            topLeft.Y >= 0 && topLeft.Y + target.ActualHeight <= window.ActualHeight + 0.5,
            $"{what}: lies outside the window vertically (y={topLeft.Y:F2}, h={target.ActualHeight:F2}, window={window.ActualHeight:F2})");

        // The real check: does a click at the centre actually resolve to this control, or is
        // something sitting on top of it?
        Point centre = target.TranslatePoint(
            new Point(target.ActualWidth / 2d, target.ActualHeight / 2d), window);
        HitTestResult? hit = VisualTreeHelper.HitTest(window, centre);
        Assert.True(hit?.VisualHit is not null, $"{what}: a click at its centre hits nothing at all");
        Assert.True(
            IsWithin(hit!.VisualHit, target),
            $"{what}: a click at its centre lands on {hit.VisualHit.GetType().Name}, not on it");
    }

    /// <summary>
    /// The vertical gap from the bottom of <paramref name="anchor"/> to the top of
    /// <paramref name="other"/>, in screen pixels. Positive = below. Screen coordinates matter
    /// because this card grows UPWARD (its bottom edge is pinned), so a control can keep the same
    /// position within the card while moving on screen, and vice versa.
    /// </summary>
    internal static double ScreenGapBelow(FrameworkElement anchor, FrameworkElement other)
    {
        double anchorBottom = anchor.PointToScreen(new Point(0, anchor.ActualHeight)).Y;
        double otherTop = other.PointToScreen(new Point(0, 0)).Y;
        return otherTop - anchorBottom;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        DependencyObject? current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
