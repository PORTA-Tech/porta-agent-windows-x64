using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// 2026-08-11, priority one. The morning card reported `foreground=True reason=ok_attached` and
// STILL would not accept typing. Because the card believed it had won the keyboard, it showed
// neither the grey border nor the hint - a silent failure, which is worse than a refusal, because
// a refusal already has a visible state.
//
// WHAT WAS RULED OUT: the control-template change from the design work. These tests were written
// expecting it to be the cause and they PASS against it - the caret lands, and PART_ContentHost
// resolves. The WPF focus path is intact.
//
// WHAT IT ACTUALLY IS: the card trusted SetForegroundWindow's return value. Windows can return
// nonzero while the window never reaches the foreground - under an active foreground lock it may
// only flash the taskbar button. The fix asks GetForegroundWindow who really holds it and treats a
// reported-but-not-granted result as a refusal, so the hint and the grey border appear.
//
// THE GAP THESE CLOSE: every focus test before them asserted which control was ARMED. None asserted
// that the caret ARRIVES, and none covered the refused state on any card but morning.

[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInCardCaretTests
{
    [Fact]
    public void Morning_TheProposalBoxActuallyTakesTheCaret()
        => AssertCaretLands(new UserCheckInPrompt(
            UserCheckInKind.Morning, "Still on this today?", "Keep it or change it.",
            Tasks: new[] { Line(1, "Pricing page") }));

    [Fact]
    public void Morning_TheBlankBoxActuallyTakesTheCaret()
        => AssertCaretLands(new UserCheckInPrompt(
            UserCheckInKind.Morning, "What's the one thing today?", "Name today's focus."));

    [Fact]
    public void Morning_ARowAddedLaterActuallyTakesTheCaret()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "What's the one thing today?", "Name today's focus."));

            Button add = Descendants<Button>(window)
                .First(b => (b.Content as string) == "+ add");
            add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            TextBox added = Descendants<TextBox>(window)[1];
            Drain(window);

            Assert.Same(added, window.FocusTarget);
            AssertHasCaret(added, "a row opened by + add");
        });
    }

    [Fact]
    // Chat 6 (2026-08-24) — REVERSED. The evening card's note box is gone on a day with no tasks:
    // it asked "What's first tomorrow?" into a blank with nothing prefilled and nothing to suggest.
    // There is now nothing to type into there, so nothing should be armed — asserted so "no caret"
    // stays a deliberate state rather than looking like the arming bug this file exists to catch.
    public void EndOfDay_NoTasks_ArmsNothing_BecauseThereIsNoBoxToTypeInto()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.EndOfDay, "Wrapping up", "Wrapping up the day."));

            Assert.Null(window.FocusTarget);
            Assert.Empty(Descendants<TextBox>(window));
        });
    }

    // Midday is chips-only, so there is nothing to type into and nothing should be armed. Asserted
    // so "no caret" stays a deliberate state rather than looking like the same failure.
    [Fact]
    public void Midday_ArmsNothing_BecauseItHasNoBoxToTypeInto()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Midday, "How's it going?", "Halfway.",
                QuestionLabel: "Did you get to your goal?"));

            Assert.Null(window.FocusTarget);
            Assert.Empty(Descendants<TextBox>(window));
        });
    }

    // The mechanism, isolated: a TextBox styled by the card must remain focusable. If this fails,
    // the card's own template is the cause rather than anything about the check-in flow.
    [Fact]
    public void AStyledInputIsStillFocusable()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "What's the one thing today?", "Name today's focus."));

            TextBox box = Descendants<TextBox>(window).Single();
            Assert.True(box.Focusable, "the card's input must stay focusable");
            Assert.True(box.IsEnabled, "the card's input must stay enabled");
            Assert.NotNull(box.Template.FindName("PART_ContentHost", box));
        });
    }

    // ── the refused state, per card (2026-08-11) ──
    //
    // The hint and the grey border were previously asserted on the morning card only. They are the
    // ONLY thing standing between a refused keyboard and a user typing into nothing, so every card
    // that owns a box has to prove it shows them.

    [Fact]
    public void Morning_WhenRefused_ShowsTheHintAndTheGreyBorder()
        => AssertRefusedStateVisible(new UserCheckInPrompt(
            UserCheckInKind.Morning, "Still on this today?", "Keep it or change it.",
            Tasks: new[] { Line(1, "Pricing page") }));

    [Fact]
    public void Morning_Blank_WhenRefused_ShowsTheHintAndTheGreyBorder()
        => AssertRefusedStateVisible(new UserCheckInPrompt(
            UserCheckInKind.Morning, "What's the one thing today?", "Name today's focus."));

    [Fact]
    // Chat 6 — the evening card no longer has a box on a task-less day, so it needs no keyboard
    // and must show no refusal hint. Same shape as the midday assertion below it.
    public void EndOfDay_NoTasks_WhenRefused_ShowsNothing_BecauseItNeedsNoKeyboard()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.EndOfDay, "Wrapping up", "Wrapping up the day."));

            Assert.DoesNotContain(
                Descendants<TextBlock>(window),
                t => t.Visibility == Visibility.Visible
                     && t.Text.Contains("Click the box to type", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Midday_WhenRefused_ShowsNothing_BecauseItNeedsNoKeyboard()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Midday, "How's it going?", "Halfway.",
                QuestionLabel: "Did you get to your goal?"));

            Assert.DoesNotContain(
                Descendants<TextBlock>(window),
                t => t.Visibility == Visibility.Visible
                     && t.Text.Contains("Click the box to type", StringComparison.Ordinal));
        });
    }

    private static void AssertRefusedStateVisible(UserCheckInPrompt prompt)
    {
        RunCard(window =>
        {
            // Suppression IS the refused path: the grab returns false, exactly as in the field.
            window.ShowPrompt(prompt);

            Assert.Contains(
                Descendants<TextBlock>(window),
                t => t.Visibility == Visibility.Visible
                     && t.Text.Contains("Click the box to type", StringComparison.Ordinal));

            var box = (TextBox)window.FocusTarget!;
            Assert.Equal("#FF8B929E", ((System.Windows.Media.SolidColorBrush)box.BorderBrush).Color.ToString());
            Assert.Equal(1d, box.BorderThickness.Left);
        });
    }

    private static void AssertCaretLands(UserCheckInPrompt prompt)
    {
        RunCard(window =>
        {
            window.ShowPrompt(prompt);
            Drain(window);

            TextBox box = Descendants<TextBox>(window).Single();
            Assert.Same(box, window.FocusTarget);
            AssertHasCaret(box, prompt.Kind.ToString());
        });
    }

    private static void AssertHasCaret(TextBox box, string what)
    {
        // Keyboard.Focus is queued at Input priority, so the dispatcher has to run before the caret
        // has actually moved; Drain does that.
        Assert.True(
            box.IsKeyboardFocused || ReferenceEquals(Keyboard.FocusedElement, box),
            $"{what}: the box was armed but never received the caret — this is the typing bug.");
    }

    /// <summary>Runs queued dispatcher work so Input-priority focus callbacks actually execute.</summary>
    private static void Drain(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static TaskBoardLine Line(long id, string title)
        => new(id, title, 3600, 0d, "pending", 0);

    private static IReadOnlyList<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        void Walk(DependencyObject n)
        {
            foreach (object? c in LogicalTreeHelper.GetChildren(n))
            {
                if (c is DependencyObject d)
                {
                    if (d is T m)
                    {
                        found.Add(m);
                    }

                    Walk(d);
                }
            }
        }

        Walk(root);
        return found;
    }

    private static void RunCard(Action<UserCheckInCardWindow> body)
    {
        RunOnStaThread(() =>
        {
            // Foreground acquisition is suppressed as everywhere else; this is about the CARET,
            // which is WPF's own business and is testable.
            UserCheckInCardWindow.SuppressForegroundAcquisition = true;
            try
            {
                var window = new UserCheckInCardWindow(_ => { });
                try
                {
                    body(window);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                UserCheckInCardWindow.SuppressForegroundAcquisition = false;
            }
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(15)), "Timed out waiting for the STA caret test.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out joining the STA caret thread.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
