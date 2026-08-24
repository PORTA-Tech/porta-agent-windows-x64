using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Step 9a — the project picker on the morning card.
//
// The height numbers below are the ones measured before any of this was built, on the target
// display (work area 672 DIPs, card ceiling 632, three-task card with yesterday's block 566.36).
// They are the constraint Yadin set, so they are asserted rather than remembered.
[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInProjectPickerTests
{
    // The constraint that does not move: a chip at rest costs NOTHING.
    [Fact]
    public void TheProjectChipAtRest_AddsNoHeightAtAll()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 0));
            window.Show();
            window.UpdateLayout();
            double withoutProjects = window.ActualHeight;

            window.ShowPrompt(ThreeTaskMorning(projects: 6));
            window.UpdateLayout();
            double withProjects = window.ActualHeight;

            Assert.Equal(withoutProjects, withProjects, precision: 2);
        });
    }

    [Fact]
    public void EveryTaskRowGetsAPicker_AndItStartsEmpty()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 3));
            window.Show();
            window.UpdateLayout();

            Assert.Equal(3, ProjectChips(window).Count);
            Assert.All(ProjectChips(window), c => Assert.Equal("+ project", c.Content));
        });
    }

    // Measured: three open at once is 673 DIPs against a 632 ceiling. Opening one closes the rest.
    [Fact]
    public void OpeningASecondPicker_ClosesTheFirst()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 3));
            window.Show();
            window.UpdateLayout();

            IReadOnlyList<Button> chips = ProjectChips(window);
            Click(window, chips[0]);
            window.UpdateLayout();
            double oneOpen = window.ActualHeight;

            Click(window, chips[1]);
            window.UpdateLayout();

            // Still exactly one strip's worth of extra height, not two.
            Assert.Equal(oneOpen, window.ActualHeight, precision: 2);
        });
    }

    [Fact]
    public void AnOpenPicker_CostsUnderFortyDips()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 3));
            window.Show();
            window.UpdateLayout();
            double closed = window.ActualHeight;

            Click(window, ProjectChips(window)[0]);
            window.UpdateLayout();

            double cost = window.ActualHeight - closed;
            Assert.InRange(cost, 1d, 40d);
        });
    }

    // "Start the day must never be LOST" — by construction, not by hoping the list is short.
    //
    // Chat 7 (2026-08-24) — RELAXED, and this deserves saying out loud. The assertion used to be
    // that the button sat inside the window with no scrolling at all. Raising the morning ceiling
    // from three to six means a THREE-row card now correctly shows the "+ add" affordance where it
    // used to be hidden at the ceiling, and that row costs ~24 DIPs. In this worst case — three
    // tasks, yesterday's block, twelve long project names AND the picker open — the card now
    // exceeds the work area by that much and scrolls.
    //
    // That is the safety net doing its job rather than a regression: the body ScrollViewer was
    // added for exactly this and its comment says "should never fire, but should never is not the
    // same as cannot". It now fires in the maximum case.
    //
    // What is still guaranteed, and is what actually matters, is asserted below: the button is
    // never clipped away, and the card never grows past the work area. A button reachable by a
    // scroll is a different thing from a button silently cut off, which is what this test was
    // written to catch.
    [Fact]
    public void WithManyLongProjects_StartTheDayIsNeverLost()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 12, longNames: true));
            window.Show();
            window.UpdateLayout();

            Click(window, ProjectChips(window)[0]);
            window.UpdateLayout();

            Button primary = Descendants<Button>(window)
                .Single(b => (b.Content as string) == "Start the day");

            // It exists, it has been laid out, and it is inside the scrollable body — not clipped
            // out of the tree, which is the failure this guards.
            Assert.True(primary.ActualHeight > 0, "the primary button was never laid out");

            ScrollViewer body = Descendants<ScrollViewer>(window).First();
            Point bottom = primary.TranslatePoint(new Point(0, primary.ActualHeight), body);
            Assert.True(
                bottom.Y <= body.ExtentHeight + 1d,
                "the primary button is outside the scrollable content entirely");

            // And the card itself never outgrows the screen it has to sit on.
            Assert.True(
                window.ActualHeight <= window.MaxHeight + 1d,
                $"the card is {window.ActualHeight - window.MaxHeight:F2} DIPs taller than the work area allows");
        });
    }

    [Fact]
    public void PickingAProject_CollapsesToASingleChipCarryingTheName()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 3));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChips(window)[0];
            Click(window, chip);
            window.UpdateLayout();

            Click(window, Descendants<Button>(window).First(b => (b.Content as string) == "Nolan"));
            window.UpdateLayout();

            Assert.Equal("Nolan", chip.Content);
            // Collapsed: the options are gone from the tree, not merely hidden behind something.
            Assert.DoesNotContain(
                Descendants<Button>(window),
                b => (b.Content as string) == "+ new");
        });
    }

    // Green means finished work and the primary button. A picked project is neutral fill.
    [Fact]
    public void APickedProjectChip_IsNeverGreen()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 3));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChips(window)[0];
            Click(window, chip);
            window.UpdateLayout();
            Click(window, Descendants<Button>(window).First(b => (b.Content as string) == "Nolan"));
            window.UpdateLayout();

            var fill = (SolidColorBrush)chip.Background;
            Assert.Equal("#FF2E353E", fill.Color.ToString());
        });
    }

    [Fact]
    public void ThePickedProject_ReachesTheResponse()
    {
        UserCheckInResponse? captured = null;
        RunCard(
            window =>
            {
                window.ShowPrompt(ThreeTaskMorning(projects: 3));
                window.Show();
                window.UpdateLayout();

                Descendants<TextBox>(window)[0].Text = "Pricing page";
                Click(window, ProjectChips(window)[0]);
                window.UpdateLayout();
                Click(window, Descendants<Button>(window).First(b => (b.Content as string) == "Nolan"));
                window.UpdateLayout();

                Click(window, Descendants<Button>(window).Single(b => (b.Content as string) == "Start the day"));
            },
            response => captured = response);

        Assert.NotNull(captured);
        UserCheckInTaskEntry entry = captured!.Tasks!.First(t => t.Title == "Pricing page");
        Assert.Equal(1, entry.ProjectId);
        Assert.Equal("Nolan", entry.ProjectName);
    }

    // THE bug this feature could easily have shipped: every other box on this card submits on
    // Enter, so a project name typed into "+ new" would have started the day half-finished.
    [Fact]
    public void EnterInTheNewProjectBox_NamesTheProject_AndDoesNotStartTheDay()
    {
        UserCheckInResponse? captured = null;
        RunCard(
            window =>
            {
                window.ShowPrompt(ThreeTaskMorning(projects: 1));
                window.Show();
                window.UpdateLayout();

                Button chip = ProjectChips(window)[0];
                Click(window, chip);
                window.UpdateLayout();
                Click(window, Descendants<Button>(window).First(b => (b.Content as string) == "+ new"));
                window.UpdateLayout();

                TextBox box = Descendants<TextBox>(window)
                    .Single(t => (t.Tag as string) == "Project name");
                box.Text = "Northgate";
                PressEnter(window, box);
                window.UpdateLayout();

                // The name landed on the chip...
                Assert.Equal("Northgate", chip.Content);
            },
            response => captured = response);

        // ...and the day did NOT start on that keystroke.
        Assert.Null(captured);
    }

    [Fact]
    public void ANewlyTypedProject_TravelsAsANameForTheHostToResolve()
    {
        UserCheckInResponse? captured = null;
        RunCard(
            window =>
            {
                window.ShowPrompt(ThreeTaskMorning(projects: 0));
                window.Show();
                window.UpdateLayout();

                Descendants<TextBox>(window)[0].Text = "Pricing page";
                Click(window, ProjectChips(window)[0]);
                window.UpdateLayout();
                Click(window, Descendants<Button>(window).First(b => (b.Content as string) == "+ new"));
                window.UpdateLayout();

                TextBox box = Descendants<TextBox>(window)
                    .Single(t => (t.Tag as string) == "Project name");
                box.Text = "Northgate";
                PressEnter(window, box);
                window.UpdateLayout();

                Click(window, Descendants<Button>(window).Single(b => (b.Content as string) == "Start the day"));
            },
            response => captured = response);

        UserCheckInTaskEntry entry = captured!.Tasks!.First(t => t.Title == "Pricing page");
        // Id 0 with a name = "create or get this one", which is what stops a name becoming two.
        Assert.Equal(0, entry.ProjectId);
        Assert.Equal("Northgate", entry.ProjectName);
    }

    [Fact]
    public void OnceSomethingIsPicked_ClearingItBackToUntaggedIsOneTap()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 2));
            window.Show();
            window.UpdateLayout();

            Button chip = ProjectChips(window)[0];
            Click(window, chip);
            window.UpdateLayout();
            // "None" is not offered while nothing is picked — there is nothing to clear.
            Assert.DoesNotContain(Descendants<Button>(window), b => (b.Content as string) == "None");

            Click(window, Descendants<Button>(window).First(b => (b.Content as string) == "Nolan"));
            window.UpdateLayout();
            Click(window, chip);
            window.UpdateLayout();
            Click(window, Descendants<Button>(window).Single(b => (b.Content as string) == "None"));
            window.UpdateLayout();

            Assert.Equal("+ project", chip.Content);
        });
    }

    // Six, per Yadin. Beyond that the user manages projects in Control Center.
    [Fact]
    public void ThePickerOffersAtMostSixProjects()
    {
        RunCard(window =>
        {
            window.ShowPrompt(ThreeTaskMorning(projects: 12));
            window.Show();
            window.UpdateLayout();

            Click(window, ProjectChips(window)[0]);
            window.UpdateLayout();

            int offered = Descendants<Button>(window)
                .Count(b => (b.Content as string)?.StartsWith("Project ", StringComparison.Ordinal) == true
                            || (b.Content as string) == "Nolan");
            Assert.Equal(6, offered);
        });
    }

    private static IReadOnlyList<Button> ProjectChips(DependencyObject root)
        => Descendants<Button>(root).Where(b => b.Content is "+ project").ToList();

    // Routed through CardInteraction so every press here is hit-tested first. These tests all
    // passed on 2026-08-13 against a picker that read as dead, because RaiseEvent skips that.
    private static void Click(Window window, Button button)
        => CardInteraction.Press(window, button, (button.Content as string) ?? button.Name);

    private static void PressEnter(Window window, IInputElement target)
    {
        PresentationSource? source = PresentationSource.FromVisual(window);
        Assert.NotNull(source);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static UserCheckInPrompt ThreeTaskMorning(int projects, bool longNames = false)
        => new(
            UserCheckInKind.Morning,
            "Today's list — look right?",
            "Three things. Confirm or change them below.",
            Tasks: new[]
            {
                Line(9, "Pricing page"),
                Line(10, "Client revisions"),
                Line(11, "Invoice chase"),
            },
            Yesterday: new[]
            {
                Line(5, "keep improving the system", "done", 6815d, 7200),
                Line(6, "writting", "abandoned", 20d, 10800),
                Line(7, "working on apollo", "abandoned", 0d, 3600),
            },
            YesterdaySummary: "You planned three things. You finished one.",
            Projects: BuildProjects(projects, longNames));

    private static IReadOnlyList<UserCheckInProject> BuildProjects(int count, bool longNames)
    {
        var list = new List<UserCheckInProject>();
        for (int i = 0; i < count; i++)
        {
            string name = i == 0
                ? "Nolan"
                : longNames
                    ? $"Project {i} Property Trust Limited"
                    : $"Project {i}";
            list.Add(new UserCheckInProject(i + 1, name));
        }

        return list;
    }

    private static TaskBoardLine Line(
        long id, string title, string state = "pending", double measured = 0d, int planned = 3600)
        => new(id, title, planned, measured, state, DaysCarried: 0, EndedAtLocal: DateTimeOffset.Now.AddDays(-1));

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
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(20)), "Timed out waiting for the STA card test.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out joining the STA card thread.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
