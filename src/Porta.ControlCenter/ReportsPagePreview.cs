using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Porta.Agent.Core;
using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

/// <summary>
/// Step 10 (2026-08-13) — a preview harness for the Projects and Reports pages, the same idea as
/// <c>--preview-checkins</c> in the user agent and for the same reason: Yadin judges design from
/// images, and `Step10_Report_BuildBrief.md` §6 asks for the equivalent of the card harness rather
/// than a description in prose.
///
/// <b>It renders the REAL MainWindow</b>, not a mock-up of it. That is the whole point — a harness
/// with its own copy of the markup drifts, and then the picture stops being evidence. The window is
/// constructed but never shown, so nothing in <c>Loaded</c> runs and no agent is needed; the pages
/// are filled with sample data through the ordinary render paths.
///
/// <list type="bullet">
/// <item><c>--preview-reports --png</c> — writes the PNGs and prints the folder.</item>
/// </list>
/// </summary>
internal static class ReportsPagePreview
{
    internal const string Flag = "--preview-reports";
    internal const string PngFlag = "--png";

    internal static bool ShouldRun(string[] args)
        => args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    internal static void Run(string[] args)
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "porta-reports-preview",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        // Runs inside the Application WPF already created for us; creating a second one throws.
        // OnExplicitShutdown because each state closes its window, and the default mode would treat
        // the first close as "the last window went away" and shut the app down mid-run.
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow.PreviewMode = true;
        int written = 0;

        foreach (PreviewState state in BuildStates())
        {
            // A fresh window per state: these pages are static once rendered, and reusing one
            // would carry the previous state's rows into the next picture.
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                // Off-screen rather than hidden: a Window that has never been shown has no
                // realised visual tree, and RenderTargetBitmap renders it as a blank page.
                Left = -20000,
                Top = -20000,
                Width = state.Width,
                Height = state.Height,
                ShowInTaskbar = false,
                WindowState = WindowState.Normal
            };
            window.Show();

            // Fill AFTER showing, so nothing the window does on Loaded overwrites the sample.
            window.ShowPreviewPage(state.Section, state.Fill);
            Flush(window);

            // Two shots. The window shot shows the page beside the navigation it has to live next
            // to — which is where "looks like a different application" is actually visible. The
            // page shot is the whole page at its natural height, uncut.
            string file = Path.Combine(dir, $"{++written:00}-{Slug(state.Name)}-in-app.png");
            SaveElementPng((FrameworkElement)window.Content, file);
            Console.WriteLine(Path.GetFileName(file));

            string pageFile = Path.Combine(dir, $"{written:00}-{Slug(state.Name)}-page.png");
            SavePagePng(window.PreviewPageBody(state.Section), pageFile);
            Console.WriteLine(Path.GetFileName(pageFile));

            window.Close();
        }

        // The calendar popup, once per display mode.
        foreach ((string name, CalendarMode mode) in CalendarStates)
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                Width = 1180,
                Height = 900,
                ShowInTaskbar = false
            };
            window.Show();
            window.ShowPreviewPage("Reports", w => w.PreviewFillReport(SampleDraft));

            Popup popup = window.PreviewOpenCalendar();
            Flush(window);

            // Open first, settle, THEN switch mode — the base control resets it while opening.
            CalendarMode? actual = window.PreviewSetCalendarMode(popup, mode);
            Flush(window);

            string file = Path.Combine(dir, $"{++written:00}-{Slug(name)}.png");
            if (popup.Child is FrameworkElement child && child.ActualWidth > 0)
            {
                SavePopupPng(child, file);
                string warn = actual == mode ? "" : $"  !! asked {mode}, got {actual?.ToString() ?? "no calendar"}";
                Console.WriteLine(
                    $"{Path.GetFileName(file)}  {child.ActualWidth:F0}x{child.ActualHeight:F0}{warn}");
            }
            else
            {
                Console.WriteLine($"{Path.GetFileName(file)}  POPUP EMPTY — nothing rendered");
            }

            window.Close();
        }

        // Not a picture: the two things that cannot be seen in one. The format must survive a pick
        // made through the calendar, and a draft whose dates no longer match its pickers must say so.
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, Width = 1180, Height = 900, ShowInTaskbar = false
            };
            window.Show();
            window.ShowPreviewPage("Reports", w => w.PreviewFillReport(SampleDraft));
            Flush(window);

            window.PreviewPickThroughCalendar(new DateTime(2026, 08, 03));
            Flush(window);
            (string from, string to) = window.PreviewPickerTexts;

            Console.WriteLine();
            Console.WriteLine("CHECKS");
            Console.WriteLine($"  after picking 3 Aug through the calendar:");
            Console.WriteLine($"    from reads : \"{from}\"   {(from == "03 Aug 2026" ? "OK" : "WRONG")}");
            Console.WriteLine($"    to reads   : \"{to}\"   {(to == "18 Aug 2026" ? "OK" : "WRONG")}");
            Console.WriteLine(
                $"  stale notice shown after the range moved: {window.PreviewStaleNoticeShown} " +
                $"{(window.PreviewStaleNoticeShown ? "OK" : "WRONG")}");
            window.Close();
        }

        Console.WriteLine();
        Console.WriteLine($"{written} page states written to:");
        Console.WriteLine(dir);
    }

    /// <summary>
    /// Let the dispatcher finish layout and rendering before the bitmap is taken. Without this the
    /// picture can catch the page mid-arrange, which is a bug report about a design that is fine.
    /// </summary>
    private static void Flush(Window window)
    {
        window.UpdateLayout();
        for (int i = 0; i < 3; i++)
        {
            window.Dispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private static void SaveElementPng(FrameworkElement target, string path)
    {
        int w = Math.Max(1, (int)Math.Ceiling(target.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(target.ActualHeight));

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);
        Write(bitmap, path);
    }

    /// <summary>
    /// The page at its FULL height, off the visual tree's clip. Re-measuring against an unbounded
    /// height is what gets past the window's scroll viewport; the page is then drawn onto the page
    /// background so it is not a picture of dark text on transparency.
    /// </summary>
    private static void SavePagePng(FrameworkElement page, string path)
    {
        const double PageWidth = 760d;
        const double Pad = 24d;

        // Measure, arrange, then measure AGAIN: text that wraps only reports its true height once
        // it has been arranged at the final width, so a single pass clips the last line.
        page.Measure(new Size(PageWidth, double.PositiveInfinity));
        page.Arrange(new Rect(0, 0, PageWidth, page.DesiredSize.Height));
        page.UpdateLayout();
        page.Measure(new Size(PageWidth, double.PositiveInfinity));
        Size desired = new(PageWidth, Math.Max(page.DesiredSize.Height, page.ActualHeight));
        page.Arrange(new Rect(0, 0, PageWidth, desired.Height));
        page.UpdateLayout();

        int w = (int)Math.Ceiling(PageWidth + (Pad * 2));
        int h = (int)Math.Ceiling(desired.Height + (Pad * 2));

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(
                new VisualBrush(page) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null,
                new Rect(Pad, Pad, PageWidth, desired.Height));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Write(bitmap, path);
    }

    /// <summary>
    /// The popup's child, on the page background with a margin. A popup renders on transparency, so
    /// without a drawn backdrop the picture is dark text on nothing.
    /// </summary>
    private static void SavePopupPng(FrameworkElement child, string path)
    {
        const double Pad = 20d;
        double cw = child.ActualWidth;
        double ch = child.ActualHeight;

        int w = (int)Math.Ceiling(cw + (Pad * 2));
        int h = (int)Math.Ceiling(ch + (Pad * 2));

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x15)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(
                new VisualBrush(child) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null,
                new Rect(Pad, Pad, cw, ch));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Write(bitmap, path);
    }

    private static void Write(RenderTargetBitmap bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Slug(string name)
        => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Replace("--", "-", StringComparison.Ordinal)
            .Trim('-');

    private sealed record PreviewState(
        string Name,
        string Section,
        Action<MainWindow> Fill,
        int Width = 1180,
        int Height = 900);

    // ── the states ──

    private static IReadOnlyList<PreviewState> BuildStates() =>
    [
        new("projects with rows", "Projects", w => w.PreviewFillProjects(SampleProjects)),
        new("projects empty", "Projects", w => w.PreviewFillProjects([])),
        new("reports drafted", "Reports", w => w.PreviewFillReport(SampleDraft), Height: 1560),
        new("reports empty", "Reports", w => w.PreviewFillReport(EmptyDraft), Height: 1080),

        // §6b, both states. Every value is a placeholder — the real ones come from that user's own
        // account profile and the client they picked.
        new("confirm ready", "Reports", w =>
        {
            w.PreviewFillReport(ReadyDraft);
            w.PreviewFillConfirm(
                "A Real Person", "person@example.test",
                "accounts@example-client.test", "Nolan Studio",
                Preflight("A Real Person", "person@example.test", "accounts@example-client.test"),
                ReadyDraft);
        }, Height: 1080),

        new("confirm refused synthetic replyto", "Reports", w =>
        {
            w.PreviewFillReport(ReadyDraft);
            w.PreviewFillConfirm(
                "A Real Person", "displaced-acct_9f21c8@porta.internal",
                "accounts@example-client.test", "Nolan Studio",
                Preflight("A Real Person", "displaced-acct_9f21c8@porta.internal", "accounts@example-client.test"),
                ReadyDraft);
        }, Height: 1080),
    ];

    private static ReportSendVerdict Preflight(string from, string replyTo, string recipient)
        => ReportSendPreflight.Evaluate(ToAgentDraft(ReadyDraft), null, from, replyTo, recipient);

    /// <summary>
    /// The preflight works on the agent-core draft; the page holds the local-API shape. The harness
    /// converts rather than duplicating the rules, so the picture shows the real verdict.
    /// </summary>
    private static AgentReportDraft ToAgentDraft(LocalApiReportDraft d)
        => new(
            d.ProjectId, d.ProjectName, d.ClientName, d.RangeStartLocal, d.RangeEndLocal,
            d.TotalHours, d.DaysWorked, d.LinesFinished,
            d.Lines.Select(l => new AgentReportLine(
                l.TaskId, l.Title, l.ClientLabel, l.Finished, l.Hours,
                l.UnattributedHours, l.UnattributedMeasurable)).ToList(),
            d.Days.Select(x => new AgentReportDay(x.DateLocal, x.Hours)).ToList());

    /// <summary>Every line named, so the confirm screen is reached on its merits.</summary>
    private static LocalApiReportDraft ReadyDraft => new(
        ProjectId: 1,
        ProjectName: "nolan - chase invoice",
        ClientName: "Nolan Studio",
        RangeStartLocal: "2026-08-12",
        RangeEndLocal: "2026-08-18",
        TotalHours: 14.2,
        DaysWorked: 5,
        LinesFinished: 2,
        Lines:
        [
            new(1, "finish my writting part \"all the way in\"", "Chapter three — first draft", true, 6.2, 0.4, true),
            new(2, "continue to improve the system", "Interview transcripts", true, 4.9, 0d, true),
            new(3, "working on appolo leads", "Structural edit, part one", false, 3.1, 0d, false),
        ],
        Days:
        [
            new("2026-08-12", 3.7), new("2026-08-13", 5.2), new("2026-08-14", 0.0),
            new("2026-08-15", 4.6), new("2026-08-16", 0.0), new("2026-08-17", 0.7),
            new("2026-08-18", 0.0),
        ]);

    // The popup is a separate visual tree in its own window, so the page shots never contained it.
    // One picture per display mode, because the header button walks through all three and a broken
    // year view is just as invisible from the closed state as a broken month view was.
    private static IReadOnlyList<(string Name, CalendarMode Mode)> CalendarStates =>
    [
        ("calendar days", CalendarMode.Month),
        ("calendar months", CalendarMode.Year),
        ("calendar years", CalendarMode.Decade),
    ];

    private static readonly DateTimeOffset Made = new(2026, 08, 12, 9, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<LocalApiProject> SampleProjects =>
    [
        new(Id: 1, Name: "Nolan", Archived: false, CreatedAtUtc: Made, ClientName: "Nolan Studio"),
        new(Id: 2, Name: "nolan - chase invoice", Archived: false, CreatedAtUtc: Made),
        new(Id: 3, Name: "Apollo leads", Archived: false, CreatedAtUtc: Made),
        new(Id: 4, Name: "Old retainer", Archived: true, CreatedAtUtc: Made),
    ];

    // Deliberately mixed: one named line, one never named, one finished, one still running, and a
    // day in the middle with nothing on it. Every state the page has to be honest about.
    private static LocalApiReportDraft SampleDraft => new(
        ProjectId: 1,
        ProjectName: "nolan - chase invoice",
        ClientName: "Nolan Studio",
        RangeStartLocal: "2026-08-12",
        RangeEndLocal: "2026-08-18",
        TotalHours: 14.2,
        DaysWorked: 5,
        LinesFinished: 2,
        Lines:
        [
            new(TaskId: 1, Title: "finish my writting part \"all the way in\"",
                ClientLabel: "Chapter three — first draft", Finished: true,
                Hours: 6.2, UnattributedHours: 0.4, UnattributedMeasurable: true),
            new(TaskId: 2, Title: "continue to improve the system",
                ClientLabel: "Interview transcripts", Finished: true,
                Hours: 4.9, UnattributedHours: 0d, UnattributedMeasurable: true),
            new(TaskId: 3, Title: "working on appolo leads",
                ClientLabel: "", Finished: false,
                Hours: 3.1, UnattributedHours: 0d, UnattributedMeasurable: false),
        ],
        Days:
        [
            new("2026-08-12", 3.7), new("2026-08-13", 5.2), new("2026-08-14", 0.0),
            new("2026-08-15", 4.6), new("2026-08-16", 0.0), new("2026-08-17", 0.7),
            new("2026-08-18", 0.0),
        ]);

    private static LocalApiReportDraft EmptyDraft => new(
        ProjectId: 1,
        ProjectName: "Apollo leads",
        ClientName: "",
        RangeStartLocal: "2026-08-12",
        RangeEndLocal: "2026-08-14",
        TotalHours: 0d,
        DaysWorked: 0,
        LinesFinished: 0,
        Lines: [],
        Days: [new("2026-08-12", 0d), new("2026-08-13", 0d), new("2026-08-14", 0d)]);
}
