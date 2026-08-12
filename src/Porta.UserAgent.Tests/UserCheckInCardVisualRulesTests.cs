using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// The visual rules, pinned on ALL THREE cards rather than only the one they were written for
// (Yadin, 2026-08-10, after finding midday and evening still rendering tasks as plain text).
//
// Every card renders task rows through one method, AddTaskRow. These tests exist so that stays
// true: if a future change gives one surface its own dialect, the rule it breaks fails here rather
// than being found on screen weeks later.
//
// THE ONE SANCTIONED DIFFERENCE between the cards is documented in
// AddTaskRow(fadeUnfinished) and asserted at the bottom of this file.
[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInCardVisualRulesTests
{
    public static TheoryData<string> AllThreeCards => new() { "morning", "midday", "eod" };

    [Theory]
    [MemberData(nameof(AllThreeCards))]
    public void NothingOnAnyCardIsRed(string card)
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor(card));

            Assert.All(
                AllBrushes(window),
                b => Assert.False(
                    b.Color.R > 150 && b.Color.G < 90 && b.Color.B < 90,
                    $"a red-ish brush reached the {card} card: {b.Color}. A dropped task is a "
                    + "decision, not an error."));
        });
    }

    // THE GREEN RULE, restated by Yadin 2026-08-11 when the digest's timeline needed the accent for
    // focused time:
    //
    //     "Green means work that counted: a finished task, a finished bar, focused time on the
    //      timeline, plus the primary button. Nothing else, ever."
    //
    // He chose to fix the rule rather than widen it case by case. On a CARD there is no timeline, so
    // the enforceable set here is unchanged — the check, the finished bars, the button. The timeline
    // clause governs the digest email, which has no card surface to test. What must never change is
    // the exclusion: nothing on an input, a chip, or an unfinished row may be green.
    [Theory]
    [MemberData(nameof(AllThreeCards))]
    public void GreenMeansWorkThatCounted_AndNothingElse(string card)
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor(card));

            // Green text runs: only the finished-task check marks.
            int greenRuns = Descendants<TextBlock>(window)
                .SelectMany(t => t.Inlines.OfType<Run>())
                .Select(r => r.Foreground)
                .OfType<SolidColorBrush>()
                .Count(IsGreen);
            int finishedRows = FinishedTitles(window).Count;
            Assert.Equal(finishedRows, greenRuns);

            // Green fills: the finished rows' bars, plus the primary button. Nothing else.
            IReadOnlyList<SolidColorBrush> greenFills = Descendants<Border>(window)
                .Select(b => b.Background).OfType<SolidColorBrush>().Where(IsGreen)
                .Concat(Descendants<Button>(window).Select(b => b.Background).OfType<SolidColorBrush>().Where(IsGreen))
                .ToList();
            Assert.Equal(finishedRows + 1, greenFills.Count);

            // Chips went NEUTRAL on 2026-08-10, so the exception this test used to carry is gone:
            // a selected chip is a solid light fill with dark text, never green. Green is now the
            // finished check, the finished bar, and the primary button - three uses, one meaning
            // each, and nothing has to be explained away.
            Assert.Empty(Descendants<Button>(window)
                .Where(b => b.Foreground is SolidColorBrush g && IsGreen(g)));

            Assert.All(
                Descendants<Button>(window).Where(b => b.Background is SolidColorBrush g && IsGreen(g)),
                b => Assert.True(
                    b.Content is "Start the day" or "Save" or "Close the day" or "Good night",
                    $"only the primary button may be green; found: {b.Content}"));
        });
    }

    // Green means work that counted, and nothing else. Nothing on an input or a chip may ever be
    // green again: that is how it drifted to seven green elements on one card in the first place.
    [Theory]
    [MemberData(nameof(AllThreeCards))]
    public void NoInputOrChipIsEverGreen(string card)
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor(card));

            Assert.All(Descendants<TextBox>(window), box =>
            {
                Assert.False(box.BorderBrush is SolidColorBrush b && IsGreen(b), "an input border was green");
                Assert.False(box.Background is SolidColorBrush g && IsGreen(g), "an input fill was green");
                Assert.False(box.Foreground is SolidColorBrush f && IsGreen(f), "input text was green");
            });

            string[] primary = { "Start the day", "Save", "Close the day", "Good night" };
            Assert.All(
                Descendants<Button>(window).Where(b => !primary.Contains(b.Content as string)),
                b =>
                {
                    Assert.False(b.Background is SolidColorBrush g && IsGreen(g), $"a chip fill was green: {b.Content}");
                    Assert.False(b.Foreground is SolidColorBrush f && IsGreen(f), $"chip text was green: {b.Content}");
                });
        });
    }

    [Theory]
    [MemberData(nameof(AllThreeCards))]
    public void MeasuredNumbersAreRightAlignedAndTabular(string card)
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor(card));

            IReadOnlyList<TextBlock> values = Descendants<TextBlock>(window)
                .Where(t => TextOf(t) is "1h54m" or "20s" or "0m")
                .ToList();

            Assert.NotEmpty(values);
            Assert.All(values, v =>
            {
                Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(v));
                Assert.Equal(TextAlignment.Right, v.TextAlignment);
            });
        });
    }

    [Theory]
    [MemberData(nameof(AllThreeCards))]
    public void SubMinuteWorkReadsInSeconds(string card)
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor(card));

            // 20 seconds of a planned 3h is the most eloquent number available; "0m" would delete it.
            Assert.Contains("20s", Descendants<TextBlock>(window).Select(TextOf));
        });
    }

    [Theory]
    [MemberData(nameof(AllThreeCards))]
    public void EveryTaskRowHasABarOfActualAgainstPlanned(string card)
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor(card));

            // One track + one fill per rendered task row, all the same width, so the column reads
            // down the card rather than each row inventing its own scale.
            IReadOnlyList<Border> tracks = Descendants<Border>(window)
                .Where(b => Math.Abs(b.Width - 62d) < 0.01)
                .ToList();

            Assert.NotEmpty(tracks);
            Assert.All(tracks, t => Assert.Equal(5d, t.Height));

            // A barely-started task still shows a sliver rather than nothing.
            Assert.All(
                Descendants<Border>(window).Where(b => b.Height == 5d && b.Width < 62d),
                b => Assert.True(b.Width >= 2d, "a bar rounded to nothing reads as 'no data'"));
        });
    }

    [Theory]
    [MemberData(nameof(AllThreeCards))]
    public void SectionLabelsAreQuieterThanTheValuesBeneathThem(string card)
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor(card));

            IReadOnlyList<TextBlock> labels = Descendants<TextBlock>(window)
                .Where(t => TextOf(t) is "YESTERDAY" or "TODAY" or "FINISHED" or "STILL OPEN")
                .ToList();

            Assert.NotEmpty(labels);
            Assert.All(labels, l =>
            {
                Assert.True(l.FontSize <= 10d, "a section label must be smaller than its values");
                var brush = (SolidColorBrush)l.Foreground;
                // Muted, never full-strength title colour.
                Assert.True(brush.Color.R < 200, "a section label must be quieter than its values");
            });

            // Each label is followed by a hairline rule.
            Assert.NotEmpty(Descendants<Border>(window).Where(b => Math.Abs(b.Height - 0.5d) < 0.01));
        });
    }

    // ── the one sanctioned difference ──

    // Fading is TONE, not state. On the morning card's YESTERDAY block, "not done" means "you
    // dropped this" and receding is correct. On midday and evening, "not done" means "this is the
    // live work you are about to act on" — fading it would make the interactive rows the quietest
    // thing on the card, and would read as a reproach for work that is simply still in progress.
    [Fact]
    public void OnYesterday_UnfinishedWorkFades()
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor("morning"));

            var finished = (SolidColorBrush)TitleBlock(window, "keep improving the system").Foreground;
            var dropped = (SolidColorBrush)TitleBlock(window, "writting").Foreground;

            Assert.True(finished.Color.R > dropped.Color.R, "dropped work must recede on yesterday");
        });
    }

    [Fact]
    public void OnToday_UnfinishedWorkDoesNotFade_BecauseItIsTheLiveWork()
    {
        RunCard(window =>
        {
            window.ShowPrompt(PromptFor("midday"));

            var open = (SolidColorBrush)TitleBlock(window, "Pricing page").Foreground;
            var finishedOnMorning = (SolidColorBrush)Color(TitleFgProbe());

            // Full strength, identical to a finished title — the difference between them is the
            // check and the bar, never the legibility of the work you are being asked about.
            Assert.Equal(finishedOnMorning.Color, open.Color);
        });
    }

    private static SolidColorBrush Color(System.Windows.Media.Color c) => new(c);

    private static System.Windows.Media.Color TitleFgProbe()
        => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DFE3E9")!;   // the spec's live-title colour

    private static UserCheckInPrompt PromptFor(string card) => card switch
    {
        "morning" => new UserCheckInPrompt(
            UserCheckInKind.Morning,
            "Still on this today?",
            "“Pricing page” is still open.",
            Tasks: new[] { Line(9, "Pricing page", "pending") },
            Yesterday: new[]
            {
                Line(5, "keep improving the system", "done", 6815d, 7200),
                Line(6, "writting", "abandoned", 20d, 10800),
                Line(7, "working on apollo", "abandoned", 0d, 3600),
            },
            YesterdaySummary: "You planned three things. You finished one."),

        "midday" => new UserCheckInPrompt(
            UserCheckInKind.Midday,
            "How's it going?",
            "Here's where today stands.",
            Tasks: new[]
            {
                Line(1, "Pricing page", "active", 6815d, 7200),
                Line(2, "writting", "pending", 20d, 10800),
            },
            QuestionLabel: "Anything done?"),

        _ => new UserCheckInPrompt(
            UserCheckInKind.EndOfDay,
            "Wrapping up",
            "You planned two things. You finished one.",
            Tasks: new[]
            {
                Line(1, "keep improving the system", "done", 6815d, 7200),
                Line(2, "writting", "pending", 20d, 10800),
            },
            QuestionLabel: "What moves to tomorrow?"),
    };

    private static TaskBoardLine Line(
        long id, string title, string state, double measured = 0d, int planned = 3600)
        => new(id, title, planned, measured, state, DaysCarried: 0, EndedAtLocal: DateTimeOffset.Now.AddDays(-1));

    private static IReadOnlyList<string> FinishedTitles(UserCheckInCardWindow window)
        => Descendants<TextBlock>(window)
            .Select(TextOf)
            .Where(t => t.StartsWith("✓", StringComparison.Ordinal))
            .ToList();

    private static TextBlock TitleBlock(UserCheckInCardWindow window, string contains)
        => Descendants<TextBlock>(window)
            .First(t => TextOf(t).Contains(contains, StringComparison.Ordinal)
                        && !TextOf(t).Contains("planned", StringComparison.Ordinal));

    private static IReadOnlyList<SolidColorBrush> AllBrushes(UserCheckInCardWindow window)
        => Descendants<TextBlock>(window).Select(t => t.Foreground).OfType<SolidColorBrush>()
            .Concat(Descendants<TextBlock>(window).SelectMany(t => t.Inlines.OfType<Run>())
                .Select(r => r.Foreground).OfType<SolidColorBrush>())
            .Concat(Descendants<Border>(window).Select(b => b.Background).OfType<SolidColorBrush>())
            .Concat(Descendants<Button>(window).Select(b => b.Foreground).OfType<SolidColorBrush>())
            .ToList();

    private static bool IsGreen(SolidColorBrush b) => b.Color.G > 150 && b.Color.R < 100;

    private static string TextOf(TextBlock block)
    {
        string fromRuns = string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text));
        return fromRuns.Length > 0 ? fromRuns : block.Text;
    }

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
                if (child is not DependencyObject d)
                {
                    continue;
                }

                if (d is T match)
                {
                    found.Add(match);
                }

                Walk(d);
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
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Timed out waiting for the STA card test.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out joining the STA card thread.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
