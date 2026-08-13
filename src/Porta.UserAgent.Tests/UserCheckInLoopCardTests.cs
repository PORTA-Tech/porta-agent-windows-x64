using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Steps 4, 5 and 7 as the user meets them: a morning that proposes instead of asking, a midday that
// takes one tap per task, and an evening that closes the day instead of opening a new one.
[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInLoopCardTests
{
    // ── step 4: propose, never an empty box ──

    [Fact]
    public void Morning_PreFillsARowPerProposedTask()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning,
                "Today's list — look right?",
                "Still open from before.",
                Tasks: new[] { Line(1, "Pricing page"), Line(2, "Client revisions") }));

            IReadOnlyList<TextBox> boxes = Descendants<TextBox>(window);
            Assert.Equal(2, boxes.Count);
            Assert.Equal("Pricing page", boxes[0].Text);
            Assert.Equal("Client revisions", boxes[1].Text);
        });
    }

    [Fact]
    public void Morning_ProposalIsAcceptedWithOneTap_AndKeepsItsDurations()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning,
                "Still on this today?",
                "Keep it or change it.",
                Tasks: new[] { Line(1, "Pricing page") }));

            // No typing at all — the whole point of step 4.
            Click(ButtonWithContent(window, "Start the day"));
        }, r => captured = r);

        UserCheckInTaskEntry only = Assert.Single(captured!.Tasks!);
        Assert.Equal("Pricing page", only.Title);
        Assert.Equal(UserCheckInChoices.MorningTaskDurations[0].Seconds, only.PlannedSeconds);
    }

    [Fact]
    public void Morning_ProposalNeverExceedsTheThreeRowCeiling()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning,
                "Today's list",
                "Lots open.",
                Tasks: Enumerable.Range(1, 5).Select(i => Line(i, $"Task {i}")).ToArray()));

            Assert.Equal(UserCheckInTaskPlanner.MaxMorningTasks, Descendants<TextBox>(window).Count);
        });
    }

    // ── removing a row you changed your mind about (2026-08-09) ──

    [Fact]
    public void Morning_RemovingARow_DropsThatRowOnly()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "Today's list", "Still open.",
                Tasks: new[] { Line(1, "Pricing page"), Line(2, "Invoices"), Line(3, "Client revisions") }));

            // Drop the middle one — not the last, so an off-by-one would be visible.
            Click(RemoveButtons(window)[1]);
            Click(ButtonWithContent(window, "Start the day"));
        }, r => captured = r);

        Assert.Equal(new[] { "Pricing page", "Client revisions" }, captured!.Tasks!.Select(t => t.Title));
    }

    [Fact]
    public void Morning_RemovingARow_KeepsTheOtherRowsDurations()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "Today's list", "Still open.",
                Tasks: new[] { Line(1, "Pricing page"), Line(2, "Invoices") }));

            // 3h on the second row, then delete the FIRST — the survivor must keep its own chip.
            Click(ButtonsWithContent(window, "3h")[1]);
            Click(RemoveButtons(window)[0]);
            Click(ButtonWithContent(window, "Start the day"));
        }, r => captured = r);

        UserCheckInTaskEntry only = Assert.Single(captured!.Tasks!);
        Assert.Equal("Invoices", only.Title);
        Assert.Equal(10800, only.PlannedSeconds);
    }

    [Fact]
    public void Morning_TheLastRowCannotBeRemoved_SoTheCardAlwaysHasABox()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "Still on this?", "One thing.",
                Tasks: new[] { Line(1, "Pricing page") }));

            // With one row there is nothing to remove, so the affordance is not offered at all.
            Assert.Empty(RemoveButtons(window));
            Assert.Single(Descendants<TextBox>(window));
        });
    }

    [Fact]
    public void Morning_RemovingDownToOne_HidesTheRemainingRemoveButton()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "Today's list", "Two things.",
                Tasks: new[] { Line(1, "Pricing page"), Line(2, "Invoices") }));

            Assert.Equal(2, RemoveButtons(window).Count);

            Click(RemoveButtons(window)[0]);

            Assert.Empty(RemoveButtons(window));
        });
    }

    [Fact]
    public void Morning_RemovingFromTheCeiling_BringsBackTheAddAffordance()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "Today's list", "Three things.",
                Tasks: new[] { Line(1, "A"), Line(2, "B"), Line(3, "C") }));

            Assert.Equal(Visibility.Collapsed, AddButton(window).Visibility);

            Click(RemoveButtons(window)[2]);

            Assert.Equal(Visibility.Visible, AddButton(window).Visibility);
        });
    }

    /// <summary>Titles are composed from Runs (an optional check plus the title).</summary>
    private static string TextOf(TextBlock block)
    {
        string fromRuns = string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
        return fromRuns.Length > 0 ? fromRuns : block.Text;
    }

    private static IReadOnlyList<Button> RemoveButtons(UserCheckInCardWindow window)
        => Descendants<Button>(window)
            .Where(b => (b.Tag as string) == "remove-row" && b.Visibility == Visibility.Visible)
            .ToList();

    private static Button AddButton(UserCheckInCardWindow window) => ButtonWithContent(window, "+ add");

    // ── step 5: one tap done, with the hours already beside it ──

    [Fact]
    public void Midday_ListsEachTaskWithItsMeasuredTime()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MiddayPrompt(
                Line(1, "Pricing page", measuredSeconds: 4320d),
                Line(2, "Client revisions", measuredSeconds: 7440d)));

            // Title and number are separate cells now (shared renderer, fixed number column), so
            // they are asserted separately rather than as one concatenated line.
            IReadOnlyList<string> text = Descendants<TextBlock>(window).Select(TextOf).ToList();
            Assert.Contains("Pricing page", text);
            Assert.Contains("1h12m", text);
            Assert.Contains("Client revisions", text);
            Assert.Contains("2h4m", text);
            // The number is present-and-working time, not wall clock. Saying so is what keeps it
            // trustworthy enough to eventually put in front of a client.
            Assert.Contains(text, t => t.Contains("while you were at the machine", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Midday_TickingOneOfTwo_ReportsThatTaskDoneAndDerivesPartly()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(MiddayPrompt(Line(1, "Pricing page"), Line(2, "Client revisions")));

            // Two rows, so two "Done" chips; tick the first only.
            Click(ButtonsWithContent(window, "Done")[0]);
            Click(ButtonWithContent(window, "Save"));
        }, r => captured = r);

        Assert.Equal(2, captured!.TaskOutcomes!.Count);
        Assert.Equal(UserCheckInTaskOutcome.Done, captured.TaskOutcomes[0].Action);
        Assert.Equal(1, captured.TaskOutcomes[0].TaskId);
        Assert.Equal(UserCheckInTaskOutcome.NotYet, captured.TaskOutcomes[1].Action);
        // The yes/partly/not_yet answer is derived, not asked a second time.
        Assert.Equal("partly", captured.Choice);
    }

    [Fact]
    public void Midday_TouchingNothing_ClaimsNothingDone()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(MiddayPrompt(Line(1, "Pricing page")));
            Click(ButtonWithContent(window, "Save"));
        }, r => captured = r);

        // A card answered without touching a chip must never mark work finished.
        Assert.Equal(UserCheckInTaskOutcome.NotYet, Assert.Single(captured!.TaskOutcomes!).Action);
        Assert.Equal("not_yet", captured.Choice);
    }

    [Fact]
    public void Midday_NoTasks_FallsBackToTheOriginalThreeChipQuestion()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Midday, "How's it going?", "Halfway.", QuestionLabel: "Did you get to your goal?"));

            Click(ButtonWithContent(window, "Partly"));
        }, r => captured = r);

        Assert.Equal("partly", captured!.Choice);
        Assert.Null(captured.TaskOutcomes);
    }

    // ── step 7: the evening closes the day ──

    [Fact]
    public void EndOfDay_HasNoPlanTomorrowBox_WhenThereAreTasksToClose()
    {
        RunCard(window =>
        {
            window.ShowPrompt(EndOfDayPrompt(Line(1, "Pricing page", state: "pending")));

            // The question Yadin stopped answering is gone entirely.
            Assert.Empty(Descendants<TextBox>(window));
            Assert.Null(window.FocusTarget);
            Assert.Equal("Close the day", ButtonWithContent(window, "Close the day").Content);
        });
    }

    [Fact]
    public void EndOfDay_UnfinishedWorkIsKeptByDefault_AndDroppedOnlyOnATap()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(EndOfDayPrompt(
                Line(1, "Pricing page", state: "pending"),
                Line(2, "Invoices", state: "pending")));

            Click(ButtonsWithContent(window, "Drop it")[1]);
            Click(ButtonWithContent(window, "Close the day"));
        }, r => captured = r);

        Assert.Equal(UserCheckInTaskOutcome.NotYet, captured!.TaskOutcomes![0].Action);   // carries
        Assert.Equal(UserCheckInTaskOutcome.Drop, captured.TaskOutcomes[1].Action);
        // No plan text is collected any more.
        Assert.True(string.IsNullOrEmpty(captured.TextReply));
    }

    [Fact]
    public void EndOfDay_ShowsFinishedWorkWithoutAskingAboutIt()
    {
        RunCard(window =>
        {
            window.ShowPrompt(EndOfDayPrompt(
                Line(1, "Pricing page", state: "done", measuredSeconds: 4320d),
                Line(2, "Invoices", state: "pending")));

            IReadOnlyList<string> text = Descendants<TextBlock>(window).Select(TextOf).ToList();
            // The check is a Run inside the title cell; the number is its own right-aligned cell.
            Assert.Contains("✓ Pricing page", text);
            Assert.Contains("1h12m", text);
            // One open task, so exactly one row of chips to answer.
            Assert.Single(ButtonsWithContent(window, "Drop it"));
        });
    }

    [Fact]
    public void EndOfDay_NothingLeftOpen_JustClosesTheDay()
    {
        UserCheckInResponse? captured = null;
        RunCard(window =>
        {
            window.ShowPrompt(EndOfDayPrompt(Line(1, "Pricing page", state: "done")));

            Assert.Empty(ButtonsWithContent(window, "Drop it"));
            Click(ButtonWithContent(window, "Good night"));
        }, r => captured = r);

        Assert.Empty(captured!.TaskOutcomes!);
    }

    [Fact]
    public void EndOfDay_NoTasksAtAll_KeepsANoteBox_SoTheCardIsNeverAnEmptyShell()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.EndOfDay, "Wrapping up", "Quiet day.", QuestionLabel: "Anything for tomorrow?"));

            Assert.Single(Descendants<TextBox>(window));
        });
    }

    // ── step 6: said once, plainly ──

    [Fact]
    public void Morning_CarriedNoticeIsShownWhenPresent()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning,
                "Today's list",
                "Still open.",
                Tasks: new[] { Line(1, "Pricing page") },
                CarriedNotice: "“Pricing page” has moved forward 4 days running."));

            Assert.Contains(
                Descendants<TextBlock>(window),
                t => t.Text.Contains("moved forward 4 days", StringComparison.Ordinal));
        });
    }

    private static UserCheckInPrompt MiddayPrompt(params TaskBoardLine[] tasks)
        => new(UserCheckInKind.Midday, "How's it going?", "Here's where today stands.",
            Tasks: tasks, QuestionLabel: "Anything done?");

    private static UserCheckInPrompt EndOfDayPrompt(params TaskBoardLine[] tasks)
        => new(UserCheckInKind.EndOfDay, "Wrapping up", "You planned things.",
            Tasks: tasks, QuestionLabel: "What moves to tomorrow?");

    private static TaskBoardLine Line(
        long id,
        string title,
        string state = "pending",
        double measuredSeconds = 0d,
        int daysCarried = 0)
        => new(id, title, PlannedSeconds: 3600, MeasuredSeconds: measuredSeconds, State: state, DaysCarried: daysCarried);

    private static Button ButtonWithContent(UserCheckInCardWindow window, string content)
        => Assert.Single(ButtonsWithContent(window, content));

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
