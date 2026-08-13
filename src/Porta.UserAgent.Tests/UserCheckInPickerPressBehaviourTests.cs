using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

/// <summary>
/// The tests that would have caught "the picker does nothing when pressed" (2026-08-13).
///
/// The twelve existing picker tests all passed while the feature read as dead, because they
/// asserted STRUCTURE — the options exist in the tree, the card got taller — and pressed via
/// RaiseEvent, which skips hit testing. None of them asked the only question that matters:
/// <b>after a user presses this, does anything change where they are looking?</b>
///
/// So these assert what a person sees, in SCREEN coordinates, at the point of the press. The card
/// grows upward with its bottom edge pinned, so a control can hold its position inside the card
/// while moving on screen — card-relative assertions hide exactly the bug that shipped.
/// </summary>
[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInPickerPressBehaviourTests
{
    /// <summary>
    /// THE REGRESSION. The options opened 53 px ABOVE the chip that was pressed, between the text
    /// box and the duration row, because pickerHost was added to the row panel before durationRow
    /// was. The brief says "expands one row beneath"; a user watching the button saw nothing.
    /// </summary>
    [Fact]
    public void TheOptionsOpenBELOWTheChipThatWasPressed()
    {
        RunCard(window =>
        {
            window.ShowPrompt(Morning(projects: 2));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChip(window);
            CardInteraction.Press(window, chip, "the + project chip");
            window.UpdateLayout();

            Button nolan = Descendants<Button>(window).First(b => (b.Content as string) == "Nolan");
            double gap = CardInteraction.ScreenGapBelow(chip, nolan);

            Assert.True(
                gap >= 0,
                $"the options opened {-gap:F2} px ABOVE the chip that was pressed — that is the bug that shipped in 1.2.143");
            Assert.True(gap < 40, $"the options opened {gap:F2} px below the chip, too far to read as attached to it");
        });
    }

    /// <summary>
    /// The other half of "nothing happens": the chip was byte-identical open and closed, and
    /// because the card grows upward it did not move on screen either. A pressed control must
    /// change, or the press has no acknowledgement at the one place the user is looking.
    /// </summary>
    [Fact]
    public void TheChipItselfVisiblyChangesWhenPressed()
    {
        RunCard(window =>
        {
            window.ShowPrompt(Morning(projects: 2));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChip(window);
            Color before = ((SolidColorBrush)chip.Background).Color;

            CardInteraction.Press(window, chip, "the + project chip");
            window.UpdateLayout();

            Color after = ((SolidColorBrush)chip.Background).Color;
            Assert.NotEqual(before, after);

            // Neutral, never the accent: green means finished work and the primary button.
            Assert.Equal("#FF2E353E", after.ToString());
        });
    }

    [Fact]
    public void PressingItAgain_ReturnsTheChipToRest()
    {
        RunCard(window =>
        {
            window.ShowPrompt(Morning(projects: 2));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChip(window);
            Color rest = ((SolidColorBrush)chip.Background).Color;

            CardInteraction.Press(window, chip, "the + project chip");
            window.UpdateLayout();
            CardInteraction.Press(window, chip, "the + project chip, closing");
            window.UpdateLayout();

            Assert.Equal(rest, ((SolidColorBrush)chip.Background).Color);
            Assert.DoesNotContain(Descendants<Button>(window), b => (b.Content as string) == "+ new");
        });
    }

    /// <summary>
    /// A press must REACH the control. This is the check RaiseEvent skipped: it would fail on a
    /// chip that is off-window, zero-sized, covered by something, or not hit-test-visible — none
    /// of which the old tests could see.
    /// </summary>
    [Fact]
    public void EveryControlThePickerOffers_IsActuallyPressable()
    {
        RunCard(window =>
        {
            window.ShowPrompt(Morning(projects: 3));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChip(window);
            CardInteraction.AssertPressable(window, chip, "the + project chip");

            CardInteraction.Press(window, chip, "the + project chip");
            window.UpdateLayout();

            foreach (Button option in Descendants<Button>(window)
                         .Where(b => b.Content is "Nolan" or "Project 1" or "Project 2" or "+ new"))
            {
                CardInteraction.AssertPressable(window, option, $"the '{option.Content}' option");
            }
        });
    }

    /// <summary>
    /// The whole point of the feature, pressed the way a user presses it rather than by invoking
    /// handlers: pick a project and the chip carries its name.
    /// </summary>
    [Fact]
    public void PressingAProject_PutsItsNameOnTheChip()
    {
        RunCard(window =>
        {
            window.ShowPrompt(Morning(projects: 2));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChip(window);
            CardInteraction.Press(window, chip, "the + project chip");
            window.UpdateLayout();

            Button nolan = Descendants<Button>(window).First(b => (b.Content as string) == "Nolan");
            CardInteraction.Press(window, nolan, "the Nolan option");
            window.UpdateLayout();

            Assert.Equal("Nolan", chip.Content);
        });
    }

    /// <summary>
    /// "+ new" has to give the user somewhere to type that they can actually reach and see.
    /// </summary>
    [Fact]
    public void PressingPlusNew_OpensABoxThatIsOnScreenAndArmed()
    {
        RunCard(window =>
        {
            window.ShowPrompt(Morning(projects: 0));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChip(window);
            CardInteraction.Press(window, chip, "the + project chip");
            window.UpdateLayout();

            Button addNew = Descendants<Button>(window).Single(b => (b.Content as string) == "+ new");
            CardInteraction.Press(window, addNew, "the + new option");
            window.UpdateLayout();

            TextBox box = Descendants<TextBox>(window).Single(t => (t.Tag as string) == "Project name");
            Assert.True(box.IsVisible);
            Assert.True(CardInteraction.ScreenGapBelow(chip, box) >= 0, "the new-project box opened above the chip");
            Assert.Same(box, window.FocusTarget);
        });
    }

    private static Button ProjectChip(DependencyObject root)
        => Descendants<Button>(root).First(b => (b.Content as string) == "+ project");

    private static UserCheckInPrompt Morning(int projects)
    {
        var list = new List<UserCheckInProject>();
        for (int i = 0; i < projects; i++)
        {
            list.Add(new UserCheckInProject(i + 1, i == 0 ? "Nolan" : $"Project {i}"));
        }

        return new UserCheckInPrompt(
            UserCheckInKind.Morning,
            "Still on this today?",
            "“Pricing page” is still open. Keep it, or change it below.",
            Tasks: new[] { new TaskBoardLine(9, "Pricing page", 3600, 0d, "pending", 0, DateTimeOffset.Now) },
            Projects: list);
    }

    private static List<DependencyObject> Flatten(DependencyObject root)
    {
        var order = new List<DependencyObject>();
        void Walk(DependencyObject node)
        {
            foreach (object? child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject d)
                {
                    order.Add(d);
                    Walk(d);
                }
            }
        }

        Walk(root);
        return order;
    }

    private static IReadOnlyList<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
        => Flatten(root).OfType<T>().ToList();

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
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(25)), "timed out");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "join timed out");
        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
