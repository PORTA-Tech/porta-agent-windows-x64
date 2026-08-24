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

            if (state.Name.Contains("drawer open", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  -- field probe --");
                foreach (string line in window.PreviewProbeProjectFields())
                {
                    Console.WriteLine("     " + line);
                }
            }

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

            // The one thing no picture can show. See MainWindow.PreviewSendToCaretChecks.
            Console.WriteLine("  SEND TO can actually be typed into:");
            foreach ((string check, bool ok, string detail) in window.PreviewSendToCaretChecks())
            {
                Console.WriteLine($"    {(ok ? "OK  " : "FAIL")} {check}  —  {detail}");
            }

            Console.WriteLine("  the gate in front of confirm, and the words on a failure:");
            foreach ((string check, bool ok, string detail) in window.PreviewSendGateChecks())
            {
                Console.WriteLine($"    {(ok ? "OK  " : "FAIL")} {check}  —  {detail}");
            }

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

    /// A project with real work behind it: figures, a progress bar, tasks with their fixed colours,
    /// one task still missing the line about what came of it.
    private static readonly LocalApiProject ProjectWithWork = new(
        Id: 1, Name: "PORTA", Archived: false, CreatedAtUtc: DateTimeOffset.Now.AddDays(-30),
        ClientName: "PORTA SYSTEM", UseClientLabels: true, RateMinor: 0, RateCurrency: "",
        Status: "active", StartDay: "2026-08-12", CompletionDay: "",
        Recipients: ["client@example.test"], ReportingFrequency: "week",
        TotalTasks: 4, DoneTasks: 3, BlockedTasks: 1, InProgressTasks: 1, Hours: 7.7,
        StatusReason: "Set to active because you worked on it today.",
        StartDayReason: "Your first recorded work on this project.",
        RecipientsReason: "Taken from where you last sent. Stays on this machine.",
        DaysWorked: 4, LastWorkedLabel: "worked on today", LastSentLabel: "Last sent 18 Aug",
        NextReportDue: "Friday",
        Tasks:
        [
            new(1, "Work on phase 2, the report", "done", "14 – 18 Aug", 2.0, 1,
                "Report sending works end to end and the page renders at a permanent link."),
            new(2, "Finish the send button in the control center", "done", "17 Aug", 1.7, 4, ""),
            new(3, "Work on phase 3, the pages", "in_progress", "from 18 Aug", 1.3, 2,
                "Navigation restructured and the projects page built."),
            new(4, "Add the task page", "blocked", "held 1 day", 0.9, 5,
                "Waiting on the design being agreed.")
        ]);

    /// A project nobody has worked on yet, and with neither of the two things a report needs.
    /// This is the state that must NOT read as four zeroes and a row of dashes.
    private static readonly LocalApiProject ProjectWithNoWork = new(
        Id: 2, Name: "WRITTING", Archived: false, CreatedAtUtc: DateTimeOffset.Now.AddDays(-3),
        ClientName: "", UseClientLabels: false, RateMinor: 0, RateCurrency: "",
        Status: "active", StartDay: "", CompletionDay: "",
        Recipients: [], ReportingFrequency: "week",
        TotalTasks: 0, DoneTasks: 0, BlockedTasks: 0, InProgressTasks: 0, Hours: 0,
        StatusReason: "No recorded work yet.", StartDayReason: "", RecipientsReason: "",
        DaysWorked: 0, LastWorkedLabel: "no work recorded yet", LastSentLabel: "",
        NextReportDue: "", Tasks: []);

    /// The shapes Yadin's own database holds, including the two that were invisible: a task he put
    /// off until tomorrow, and one with recorded time that he has not marked finished.
    private static readonly LocalApiTaskRow[] SampleTasks =
    [
        new(1, "Work on phase 3, the pages", "planned", "PORTA", false, "from 18 Aug", 3.9, 2,
            "Navigation restructured and the projects page built.", "2026-08-18", "", ""),
        new(2, "Add the task page", "blocked", "PORTA", false, "held 1 day", 0.9, 5,
            "Waiting on the design being agreed.", "2026-08-17", "", ""),
        new(3, "daily weekly digest email", "planned", "", true, "—", 0, 0, "", "", "",
            DateTime.Now.AddDays(1).ToString("yyyy-MM-dd")),
        new(4, "Finish the send button", "done", "PORTA", false, "17 Aug", 1.7, 4, "", "2026-08-17", "", "")
    ];

    /// Chat 5 — the two rows the result-line fixes are about. The first has a saved line, which
    /// used to be plain text with no way back into the editor. The second is untagged, which used
    /// to be a flat "it can never appear in a client report" and nothing else.
    private static readonly LocalApiTaskRow[] ResultLineSample =
    [
        new(20, "Rewrite the onboarding email", "done", "Northbridge", false, "20 Aug", 1.4, 1,
            "Sent to the client for sign-off, with the new pricing section included.",
            "2026-08-20", "", ""),
        new(21, "Notes from the Tuesday call", "in_progress", "", true, "22 Aug", 0.6, 0,
            "", "2026-08-22", "", ""),
        new(22, "Landing page copy", "in_progress", "Northbridge", false, "from 22 Aug", 2.2, 2,
            "", "2026-08-22", "", "")
    ];

    /// Chat 5 — the projects the "give it a project" picker offers. Archived ones are dropped
    /// here because the live read drops them too; a picture that offers a project the page would
    /// not is not evidence of anything.
    private static IReadOnlyList<LocalApiProject> PickerProjects =>
        SampleProjects.Where(p => !p.Archived).ToList();

    /// Chat 4 — the task that used to vanish between the tabs. "Pricing page" still carries the
    /// future finish day it had while it was upcoming, which is what kept it stuck there; it has
    /// been moved to in_progress and has no measured hours yet, which is what kept it out of the
    /// In-progress tab. One task, two tabs, and before the fix it showed in the wrong one.
    private static readonly LocalApiTaskRow[] MovedTaskSample =
    [
        new(10, "Pricing page", "in_progress", "Northbridge", false, "from 24 Aug", 0, 1,
            "", "", DateTime.Now.AddDays(6).ToString("yyyy-MM-dd"), ""),
        new(11, "Case study", "planned", "Northbridge", false, "—", 0, 2,
            "", "", DateTime.Now.AddDays(9).ToString("yyyy-MM-dd"), ""),
        new(12, "Rewrite the onboarding email", "done", "Northbridge", false, "24 Aug", 1.4, 3,
            "Sent to the client for sign-off.", "2026-08-24", "", "")
    ];

    /// Yadin's actual situation on 18 Aug: his real project archived and invisible, beside the
    /// replacement he had to make because he could not get the first one back.
    private static readonly LocalApiProject ArchivedRealProject = new(
        Id: 1, Name: "PORTA", Archived: true, CreatedAtUtc: DateTimeOffset.Now.AddDays(-30),
        ClientName: "PORTA SYSTEM", UseClientLabels: true, RateMinor: 0, RateCurrency: "",
        Status: "active", StartDay: "2026-08-12", CompletionDay: "",
        Recipients: [], ReportingFrequency: "week",
        TotalTasks: 6, DoneTasks: 5, BlockedTasks: 0, InProgressTasks: 1, Hours: 7.7,
        StatusReason: "", StartDayReason: "", RecipientsReason: "",
        DaysWorked: 4, LastWorkedLabel: "", LastSentLabel: "", NextReportDue: "", Tasks: []);

    private static IReadOnlyList<PreviewState> BuildStates() =>
    [
        // Phase 3 rebuilt — the two states that matter on this page, and the drawer open on each.
        // "no work" is here because a project nobody has touched is the state the page got wrong
        // three builds running: four zeroes and a row of dashes instead of one useful sentence.
        new("tasks upcoming", "Tasks", w => w.PreviewFillTasks(SampleTasks, "upcoming"), Height: 900),
        new("tasks in progress", "Tasks", w => w.PreviewFillTasks(SampleTasks, "prog"), Height: 900),
        new("tasks blocked", "Tasks", w => w.PreviewFillTasks(SampleTasks, "block"), Height: 900),

        // Chat 7 (2026-08-24) — a task can be made from the Control Center now. Every project is
        // offered, uncapped, because a project the user cannot see is one they cannot file against.
        new("tasks CHAT 7 — add a task, with every project offered", "Tasks",
            w => w.PreviewFillTasksAdding(ResultLineSample, PickerProjects), Height: 1000),

        // Chat 5 (2026-08-24) — the line a client reads, and the two dead ends around it.
        // Row 1 has a SAVED line: it is now clickable and reopens the editor prefilled, which was
        // impossible before. Row 2 is UNTAGGED and now offers a project instead of just saying no.
        new("tasks CHAT 5 — saved line is editable, untagged offers a project", "Tasks",
            w => w.PreviewFillTasksWithPanelOpen(ResultLineSample, "all", PickerProjects), Height: 900),
        new("tasks CHAT 5 — the editor reopened on a SAVED line", "Tasks",
            w => w.PreviewFillTasksWithPanelOpen(ResultLineSample, "all", PickerProjects, editingLineFor: 20),
            Height: 1000),
        new("tasks CHAT 5 — the project picker on an untagged task", "Tasks",
            w => w.PreviewFillTasksWithPanelOpen(ResultLineSample, "all", PickerProjects, assigningProjectFor: 21),
            Height: 1000),

        // Chat 4 (2026-08-24) — the move that used to lose a task. "Pricing page" has been moved
        // out of Upcoming to In progress and has NO measured hours yet, which is exactly the state
        // that fell between the two tabs: it could not enter In progress (which gated on hours) and
        // it could not leave Upcoming (whose predicate was date-only). These two shots are the pair
        // to look at — it must appear in the first and be absent from the second.
        new("tasks CHAT 4 — moved task now IN PROGRESS", "Tasks",
            w => w.PreviewFillTasks(MovedTaskSample, "prog"), Height: 900),
        new("tasks CHAT 4 — and gone from UPCOMING", "Tasks",
            w => w.PreviewFillTasks(MovedTaskSample, "upcoming"), Height: 900),
        new("step2 builder from build-a-report", "Reports",
            w => Console.WriteLine("     WALK: " + w.PreviewWalkBuildAReport([ProjectWithWork], SampleDraft)),
            Height: 1560),
        new("projects archived recoverable", "Projects",
            w => w.PreviewFillProjects([ProjectWithWork, ArchivedRealProject]), Height: 1500),
        new("projects with work", "Projects", w => w.PreviewFillProjects([ProjectWithWork]), Height: 1500),
        new("projects with work drawer open", "Projects",
            w => w.PreviewFillProjectsWithDrawer([ProjectWithWork]), Height: 1900),
        new("projects no work", "Projects", w => w.PreviewFillProjects([ProjectWithNoWork]), Height: 1100),
        new("projects no work drawer open", "Projects",
            w => w.PreviewFillProjectsWithDrawer([ProjectWithNoWork]), Height: 1700),
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
