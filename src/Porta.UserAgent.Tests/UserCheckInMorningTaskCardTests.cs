using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Step 3 — the morning card now collects the day's task list rather than one sentence. What these
// guard is the contract between the card and the host: the card must hand back real entries with a
// duration on each, must never require a number to be typed, and must still produce the joined goal
// sentence every pre-step-3 consumer reads.
public sealed class UserCheckInMorningTaskCardTests
{
    [Fact]
    public void Morning_OpensWithOneRow_SoTheCardIsNoTallerThanBefore()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());

            Assert.Single(Descendants<TextBox>(window));
            Assert.Equal(Visibility.Visible, AddButton(window).Visibility);
        });
    }

    [Fact]
    public void Morning_AddButton_RevealsUpToThreeRowsThenHidesItself()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());

            Click(AddButton(window));
            Assert.Equal(2, Descendants<TextBox>(window).Count);
            Assert.Equal(Visibility.Visible, AddButton(window).Visibility);

            Click(AddButton(window));
            Assert.Equal(3, Descendants<TextBox>(window).Count);

            // Three is the ceiling (locked 2026-08-07). Hide the affordance rather than let a
            // fourth row be added and then silently refused by the planner.
            Assert.Equal(Visibility.Collapsed, AddButton(window).Visibility);
        });
    }

    [Fact]
    public void Morning_EveryRowCarriesADurationWithoutAnyoneTypingANumber()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());
            Click(AddButton(window));

            IReadOnlyList<TextBox> boxes = Descendants<TextBox>(window);
            boxes[0].Text = "Pricing page";
            boxes[1].Text = "Client revisions";

            Click(ButtonWithContent(window, "Share with Ari"));
        }, response => captured = response);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Tasks);
        Assert.Equal(2, captured.Tasks!.Count);
        Assert.Equal("Pricing page", captured.Tasks[0].Title);
        Assert.Equal("Client revisions", captured.Tasks[1].Title);
        // Untouched chips: the default is a real, legal duration, so a card answered in three
        // seconds still produces tasks the store will accept.
        Assert.All(captured.Tasks, t => Assert.Equal(
            UserCheckInChoices.MorningTaskDurations[0].Seconds, t.PlannedSeconds));
    }

    [Fact]
    public void Morning_TappingADurationChipSetsThatRowOnly()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());
            Click(AddButton(window));

            IReadOnlyList<TextBox> boxes = Descendants<TextBox>(window);
            boxes[0].Text = "Pricing page";
            boxes[1].Text = "Client revisions";

            // Duration chips render one row per task, in order: [1h 2h 3h][1h 2h 3h].
            IReadOnlyList<Button> threeHour = ButtonsWithContent(window, "3h");
            Assert.Equal(2, threeHour.Count);
            Click(threeHour[1]);

            Click(ButtonWithContent(window, "Share with Ari"));
        }, response => captured = response);

        Assert.Equal(UserCheckInChoices.MorningTaskDurations[0].Seconds, captured!.Tasks![0].PlannedSeconds);
        Assert.Equal(10800, captured.Tasks[1].PlannedSeconds);
    }

    [Fact]
    public void Morning_TextReplyIsStillTheJoinedSentence_SoTheGoalRowKeepsWorking()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());
            Click(AddButton(window));

            IReadOnlyList<TextBox> boxes = Descendants<TextBox>(window);
            boxes[0].Text = "Pricing page";
            boxes[1].Text = "Client revisions";

            Click(ButtonWithContent(window, "Share with Ari"));
        }, response => captured = response);

        Assert.Equal("Pricing page; Client revisions", captured!.TextReply);
        // The tracking preference row is untouched by step 3 and still defaults to the first chip.
        Assert.Equal(UserCheckInChoices.MorningTracking[0].Value, captured.Choice);
    }

    [Fact]
    public void Morning_EmptyRowsAreDropped_NotSentAsBlankTasks()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(MorningPrompt());
            Click(AddButton(window));
            Click(AddButton(window));

            Descendants<TextBox>(window)[1].Text = "  Client revisions  ";

            Click(ButtonWithContent(window, "Share with Ari"));
        }, response => captured = response);

        UserCheckInTaskEntry only = Assert.Single(captured!.Tasks!);
        Assert.Equal("Client revisions", only.Title);
    }

    [Fact]
    public void Morning_CarriedPlanStillPrefillsTheFirstRow()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning,
                "Still the goal for today?",
                "Last night you planned this.",
                PrefillGoalText: "Pricing page"));

            IReadOnlyList<TextBox> boxes = Descendants<TextBox>(window);
            Assert.Single(boxes);
            Assert.Equal("Pricing page", boxes[0].Text);
        });
    }

    // Step 1's regression, re-asserted through the rebuilt morning body: the caret must be armed on
    // the FIRST row of every show, not just the first show of the process.
    [Fact]
    public void Morning_ArmsTheFirstRowOnEveryShow()
    {
        RunCard(window =>
        {
            var armed = new List<Control?>();
            for (int show = 0; show < 3; show++)
            {
                window.ShowPrompt(MorningPrompt());
                armed.Add(window.FocusTarget);
                Assert.Same(Descendants<TextBox>(window)[0], window.FocusTarget);
            }

            Assert.Equal(3, armed.Distinct().Count());
        });
    }

    private static UserCheckInPrompt MorningPrompt()
        => new(UserCheckInKind.Morning, "What's the one thing today?", "Name today's focus.");

    private static Button AddButton(UserCheckInCardWindow window) => ButtonWithContent(window, "+ add");

    private static Button ButtonWithContent(UserCheckInCardWindow window, string content)
        => Assert.Single(ButtonsWithContent(window, content));

    private static IReadOnlyList<Button> ButtonsWithContent(UserCheckInCardWindow window, string content)
        => Descendants<Button>(window)
            .Where(b => string.Equals(b.Content as string, content, StringComparison.Ordinal))
            .ToList();

    private static void Click(ButtonBase button)
        => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // The logical tree, not the visual one: these cards are never rendered in a test, and the
    // logical tree is populated by StackPanel.Children regardless.
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

    private static void RunCard(Action<UserCheckInCardWindow> body, Action<UserCheckInResponse>? onResponse = null)
    {
        RunOnStaThread(() =>
        {
            UserCheckInCardWindow.SuppressForegroundAcquisition = true;
            try
            {
                var window = new UserCheckInCardWindow(r => onResponse?.Invoke(r));
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
                // Shut the dispatcher down with the thread, or WPF resources stay resident for the
                // rest of the run and GuiHandleLeakStressTests reports a widget leak that isn't real.
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
