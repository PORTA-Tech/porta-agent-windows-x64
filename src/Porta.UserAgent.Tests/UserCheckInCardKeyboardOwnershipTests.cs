using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// The rule, stated by Yadin 2026-08-09 after typing a task into another application without
// noticing: WHILE AN ARI CARD WITH A TEXT BOX IS OPEN, THE KEYBOARD BELONGS TO ARI. Not only at the
// instant the card appears.
//
// Step 1 fixed the appearing case and its tests guard it (UserCheckInCardWindowFocusTests). What it
// did not cover is a box that opens LATER on a card already on screen: "+ add" set the focus target
// and called Keyboard.Focus directly, skipping foreground acquisition entirely, and skipping the
// dispatcher hop that lets WPF focus a control that has not been laid out yet.
//
// These tests are written against the observable that matters — whether the card re-asserted
// ownership of the keyboard — rather than against real foreground, which cannot be unit-tested and
// is confirmed by `foreground=` in user-agent.log.
public sealed class UserCheckInCardKeyboardOwnershipTests
{
    [Fact]
    public void AddingARow_ReAssertsTheKeyboardForTheNewBox()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());
            int afterShow = window.FocusAssertions;

            Click(AddButton(window));

            // The new box is the same moment as a card appearing: it must run the same routine,
            // foreground acquisition included.
            Assert.Equal(afterShow + 1, window.FocusAssertions);
            Assert.Same(Descendants<TextBox>(window)[1], window.FocusTarget);
        });
    }

    [Fact]
    public void AddingTwoRows_ReAssertsForEachOne()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());
            int afterShow = window.FocusAssertions;

            Click(AddButton(window));
            Click(AddButton(window));

            Assert.Equal(afterShow + 2, window.FocusAssertions);
            Assert.Same(Descendants<TextBox>(window)[2], window.FocusTarget);
        });
    }

    [Fact]
    public void RemovingTheRowThatOwnsTheCaret_HandsItToASurvivingBox_NotNowhere()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());
            Click(AddButton(window));

            IReadOnlyList<TextBox> before = Descendants<TextBox>(window);
            Assert.Same(before[1], window.FocusTarget);

            // Remove the row holding the caret. Leaving the keyboard pointed at a box that is no
            // longer in the visual tree is the same defect step 1 fixed, arrived at from the far end.
            Click(RemoveButtons(window)[1]);

            Control? target = window.FocusTarget;
            Assert.NotNull(target);
            Assert.Contains(target, Descendants<TextBox>(window));
        });
    }

    private static UserCheckInPrompt MorningPrompt()
        => new(UserCheckInKind.Morning, "What's the one thing today?", "Name today's focus.");

    private static Button AddButton(UserCheckInCardWindow window)
        => Assert.Single(ButtonsWithContent(window, "+ add"));

    private static IReadOnlyList<Button> RemoveButtons(UserCheckInCardWindow window)
        => ButtonsWithContent(window, "✕").Where(b => b.Tag as string == "remove-row").ToList();

    private static IReadOnlyList<Button> ButtonsWithContent(UserCheckInCardWindow window, string content)
        => Descendants<Button>(window)
            .Where(b => string.Equals(b.Content as string, content, StringComparison.Ordinal))
            .ToList();

    private static void Click(ButtonBase button)
        => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IReadOnlyList<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        Walk(root);
        return found;

        void Walk(DependencyObject node)
        {
            foreach (object? child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is not DependencyObject dependencyObject)
                {
                    continue;
                }

                if (dependencyObject is T match)
                {
                    found.Add(match);
                }

                Walk(dependencyObject);
            }
        }
    }

    private static void RunCard(Action<UserCheckInCardWindow> body)
    {
        RunOnStaThread(() =>
        {
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
                // Shut the dispatcher down with the thread, or GuiHandleLeakStressTests reports a
                // widget leak that does not exist.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Timed out waiting for STA check-in card test to finish.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for STA check-in card test thread to join.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
