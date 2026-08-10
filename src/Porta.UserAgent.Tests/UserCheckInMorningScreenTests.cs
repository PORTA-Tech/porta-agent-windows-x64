using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Step 8 — the morning screen, built as a taller CARD rather than a full-screen window (Yadin,
// 2026-08-10). The card decision is load-bearing for the keyboard: same window means the same HWND,
// so the foreground acquisition, the retry and the refusal hint all carry over unchanged. A new
// full-screen window would have been a new HWND making its own grab under the same foreground lock.
//
// Measured on the target display: work area 672 DIPs, three-task morning card 452 DIPs, so the
// yesterday block has real room. These guard the rules rather than the pixels.
[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInMorningScreenTests
{
    [Fact]
    public void Morning_ShowsYesterdayAboveToday_WithTheScoreAndTheRows()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            List<string> text = Descendants<TextBlock>(window)
                .Select(TextOf)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            Assert.Contains("YESTERDAY", text);
            Assert.Contains("TODAY", text);
            Assert.Contains("You planned three things. You finished one.", text);
            Assert.Contains(text, t => t.Contains("keep improving the system", StringComparison.Ordinal));

            // Yesterday must come before today, or the card reads as history appended to a form.
            Assert.True(text.IndexOf("YESTERDAY") < text.IndexOf("TODAY"));
        });
    }

    [Fact]
    public void Yesterday_IsReadOnly_NoInputsAndNoChipsInThatBlock()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            // Three yesterday rows, but only today's single proposal row has a box.
            Assert.Single(Descendants<TextBox>(window));
            // The only remove buttons belong to today's rows, and with one row there are none.
            Assert.Empty(Descendants<Button>(window)
                .Where(b => (b.Tag as string) == "remove-row" && b.Visibility == Visibility.Visible));
        });
    }

    // Sub-minute work is the most eloquent number on the card: 20 seconds against a planned three
    // hours. Rounding it to "0m" would delete exactly the fact worth seeing.
    [Fact]
    public void SubMinuteWork_ReadsInSeconds_NotAsZeroMinutes()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            IReadOnlyList<string> text = Descendants<TextBlock>(window).Select(TextOf).ToList();
            // 20 seconds reads as 20s. A genuinely untouched task still reads 0m, which is honest.
            Assert.Contains("20s", text);
            Assert.Contains("1h54m", text);
        });
    }

    [Fact]
    public void NumbersUseTabularFigures_SoTheColumnLinesUp()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            IReadOnlyList<TextBlock> values = Descendants<TextBlock>(window)
                .Where(t => TextOf(t) is "20s" or "1h54m" or "0m")
                .ToList();

            Assert.NotEmpty(values);
            Assert.All(values, v =>
            {
                Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(v));
                Assert.Equal(TextAlignment.Right, v.TextAlignment);
            });
        });
    }

    // Green means finished, and nothing else. The primary button is the only other green thing.
    // Nothing on the card is red: dropping a task is a decision, not an error.
    [Fact]
    public void OnlyFinishedWorkIsGreen_AndNothingIsRed()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            IReadOnlyList<SolidColorBrush> brushes = Descendants<TextBlock>(window)
                .SelectMany(t => t.Inlines.OfType<Run>())
                .Select(r => r.Foreground)
                .OfType<SolidColorBrush>()
                .ToList();

            // No red anywhere on the card: nothing where red dominates the other channels.
            Assert.All(
                Descendants<TextBlock>(window).Select(t => t.Foreground).OfType<SolidColorBrush>(),
                b => Assert.False(
                    b.Color.R > 150 && b.Color.G < 90 && b.Color.B < 90,
                    $"a red-ish brush reached the card: {b.Color}"));

            // The only green Run is the finished-task check.
            Assert.Single(brushes.Where(b => b.Color.G > 150 && b.Color.R < 100));
        });
    }

    [Fact]
    public void DroppedWorkFadesBack_FinishedWorkIsFullStrength()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            TextBlock finished = Descendants<TextBlock>(window)
                .Single(t => TextOf(t).Contains("keep improving the system", StringComparison.Ordinal));
            TextBlock dropped = Descendants<TextBlock>(window)
                .Single(t => TextOf(t).Contains("writting", StringComparison.Ordinal));

            var finishedBrush = (SolidColorBrush)finished.Foreground;
            var droppedBrush = (SolidColorBrush)dropped.Foreground;

            // Quiet yesterday is still a hierarchy: the thing that got done is legible, the thing
            // that was dropped recedes.
            Assert.True(
                finishedBrush.Color.R > droppedBrush.Color.R,
                "finished work must read stronger than dropped work");
        });
    }

    [Fact]
    public void NoYesterday_ShowsNoSectionAtAll_SoAFirstDayIsNotAnEmptyFrame()
    {
        RunCard(window =>
        {
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning, "What's the one thing today?", "Name today's focus."));

            IReadOnlyList<string> text = Descendants<TextBlock>(window).Select(TextOf).ToList();
            Assert.DoesNotContain("YESTERDAY", text);
            Assert.DoesNotContain("TODAY", text);
        });
    }

    [Fact]
    public void TheButtonStartsTheDay()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            Assert.Single(Descendants<Button>(window)
                .Where(b => (b.Content as string) == "Start the day"));
            Assert.Empty(Descendants<Button>(window)
                .Where(b => (b.Content as string) == "Share with Ari"));
        });
    }

    // The guard, not a design feature: the card grows UPWARD because the bottom edge is pinned, so
    // content that outgrows the work area would push the header off the top of the screen.
    [Fact]
    public void TheCardCanNeverGrowTallerThanTheWorkArea()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());
            window.UpdateLayout();

            Assert.True(
                window.MaxHeight <= SystemParameters.WorkArea.Height,
                "the card must never be allowed to exceed the usable screen");
            Assert.True(window.Top >= SystemParameters.WorkArea.Top, "the header must stay on screen");
        });
    }

    // Both keyboard changes from 2026-08-10, at the taller size.
    [Fact]
    public void WhenTheKeyboardIsRefused_TheHintSitsWithTheInput_NotAtTheFootOfATallCard()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            TextBlock hint = Descendants<TextBlock>(window)
                .Single(t => TextOf(t).Contains("Click the box to type", StringComparison.Ordinal));
            TextBox box = Descendants<TextBox>(window).Single();

            Assert.Equal(Visibility.Visible, hint.Visibility);
            // Same parent as the rows, and ordered before them: attached to the box it is about.
            List<DependencyObject> flat = Flatten(window).ToList();
            Assert.True(flat.IndexOf(hint) < flat.IndexOf(box), "the hint must appear above the input");
        });
    }

    [Fact]
    public void WhenTheKeyboardIsRefused_TheArmedBoxIsAccented()
    {
        RunCard(window =>
        {
            window.ShowPrompt(MorningWithYesterday());

            var box = (TextBox)window.FocusTarget!;
            var border = (SolidColorBrush)box.BorderBrush;

            // Grey, not green (Yadin, 2026-08-10). A refused box POINTS; it does not celebrate, and
            // green is reserved for the finished check, the finished bar and the primary button.
            Assert.Equal("#FF8B929E", border.Color.ToString());
            Assert.Equal(1d, box.BorderThickness.Left);   // 1px marked vs the 0.5px default
        });
    }

    private static UserCheckInPrompt MorningWithYesterday()
        => new(
            UserCheckInKind.Morning,
            "Still on this today?",
            "“Pricing page” is still open. Keep it, or change it below.",
            Tasks: new[] { Line(9, "Pricing page", "pending") },
            Yesterday: new[]
            {
                Line(5, "keep improving the system", "done", measured: 6815d, planned: 7200),
                Line(6, "writting", "abandoned", measured: 20d, planned: 10800),
                Line(7, "working on apollo", "abandoned", measured: 0d, planned: 3600),
            },
            YesterdaySummary: "You planned three things. You finished one.");

    private static TaskBoardLine Line(
        long id, string title, string state, double measured = 0d, int planned = 3600)
        => new(id, title, planned, measured, state, DaysCarried: 0, EndedAtLocal: DateTimeOffset.Now.AddDays(-1));

    /// <summary>
    /// Yesterday's titles are composed from Runs (a green check plus the title), so read the text
    /// back the same way rather than trusting TextBlock.Text on an inline-composed block.
    /// </summary>
    private static string TextOf(TextBlock block)
    {
        string fromRuns = string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));
        return fromRuns.Length > 0 ? fromRuns : block.Text;
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
