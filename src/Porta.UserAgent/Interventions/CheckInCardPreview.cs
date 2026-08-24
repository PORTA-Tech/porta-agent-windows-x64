namespace Porta.UserAgent.Interventions;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Porta.Agent.LocalApi;

/// <summary>
/// The card preview harness. Renders the REAL check-in card (<see cref="UserCheckInCardWindow"/>)
/// built by the REAL coordinator (<see cref="UserCheckInCoordinator.BuildPrompt"/>), so what you
/// see is pixel-for-pixel what the fleet gets.
///
/// <para><b>Shipped in Release since 1.2.146</b>, behind <c>--preview-checkins</c>. It was
/// DEBUG-only, which meant the only way to look at a card was to wait for the scheduler to fire
/// one. Waiting a day to eyeball a card cost real time twice in the week of 2026-08-11, and the
/// project picker shipped visibly broken partly because nobody could look at it.</para>
///
/// <para><b>What it touches: nothing.</b> Every prompt below is hardcoded literals. No database,
/// no local API, no network, no scheduler. Responses are discarded — answering a preview card
/// creates no task and records no goal. It returns from Program.cs before the single-instance
/// lease and before the host starts, so it cannot disturb a running agent.</para>
///
/// <para><b>Its log is redirected</b> to the temp folder. The card logs <c>checkin_card_focus</c>
/// on every show, and this must never enter the live <c>user-agent.log</c> — that file is the
/// primary instrument for field investigations and has already been misread as field data once.</para>
///
/// <para>Two modes:
/// <list type="bullet">
/// <item><c>--preview-checkins</c> — shows every state in sequence; answer or dismiss (✕ / Esc) to
/// advance. Use this to PRESS things: the picker bug of 2026-08-13 was invisible in a static
/// image.</item>
/// <item><c>--preview-checkins --png</c> — writes every state to PNGs and prints the folder. Use
/// this to judge design, the way the 51 card PNGs are judged.</item>
/// </list></para>
/// </summary>
internal static class CheckInCardPreview
{
    internal const string Flag = "--preview-checkins";
    internal const string PngFlag = "--png";

    public static void Run(bool renderPngs)
    {
        // Before anything constructs a card: the preview's own noise must not land in the live log.
        string logPath = Path.Combine(Path.GetTempPath(), "porta-preview", "preview.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        UserAgentFileLog.RedirectTo(logPath);

        var thread = new Thread(renderPngs ? RenderAllToPngs : RunStaLoop) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    // ── interactive ──

    private static void RunStaLoop()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        IReadOnlyList<PreviewState> states = BuildStates();

        int index = 0;
        UserCheckInCardWindow? card = null;

        void ShowNext()
        {
            if (index >= states.Count)
            {
                app.Shutdown();
                return;
            }

            PreviewState state = states[index++];
            Console.WriteLine($"[{index}/{states.Count}] {state.Name}");
            card!.ShowPrompt(state.Prompt);
            state.AfterShow?.Invoke(card);
        }

        card = new UserCheckInCardWindow(_ => card!.Dispatcher.BeginInvoke((Action)ShowNext));

        app.Startup += (_, _) => ShowNext();
        app.Run();
    }

    // ── PNG sheet ──

    private static void RenderAllToPngs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-preview", "cards");
        Directory.CreateDirectory(dir);
        foreach (string stale in Directory.EnumerateFiles(dir, "*.png"))
        {
            File.Delete(stale);
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        IReadOnlyList<PreviewState> states = BuildStates();
        int written = 0;

        // One window reused across states, exactly as the live agent reuses it — a fresh window per
        // state would hide anything that only goes wrong on the second show.
        var card = new UserCheckInCardWindow(_ => { });
        UserCheckInCardWindow.SuppressForegroundAcquisition = true;
        try
        {
            for (int i = 0; i < states.Count; i++)
            {
                PreviewState state = states[i];
                card.ShowPrompt(state.Prompt);
                card.UpdateLayout();
                state.AfterShow?.Invoke(card);
                card.UpdateLayout();

                string file = Path.Combine(dir, $"{i + 1:00}-{Slug(state.Name)}.png");
                SavePng(card, file);
                written++;
                Console.WriteLine($"{Path.GetFileName(file)}  {card.ActualWidth:F0}x{card.ActualHeight:F0}");
            }
        }
        finally
        {
            UserCheckInCardWindow.SuppressForegroundAcquisition = false;
            card.Close();
            app.Shutdown();
        }

        Console.WriteLine();
        Console.WriteLine($"{written} card states written to:");
        Console.WriteLine(dir);
    }

    private static void SavePng(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Slug(string name)
        => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Replace("--", "-", StringComparison.Ordinal)
            .Trim('-');

    // ── the states ──

    private sealed record PreviewState(
        string Name,
        UserCheckInPrompt Prompt,
        Action<UserCheckInCardWindow>? AfterShow = null);

    private static IReadOnlyList<PreviewState> BuildStates()
    {
        var goal = new LocalApiDailyGoal(
            LocalDate: "2026-08-13",
            GoalText: "Finish the Q3 board deck",
            TrackingPref: "keep_on_track",
            MiddayAnswer: "",
            PlanTomorrow: "",
            CreatedAtUtc: DateTimeOffset.UtcNow.AddHours(-6),
            MiddayAtUtc: null,
            EndOfDayAtUtc: null);

        var metrics = new LocalTodaySummaryMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: 2 * 3600 + 30 * 60,
            FocusQualityPercent: 62,
            DistractionTodaySeconds: 25 * 60,
            AwayTodaySeconds: 2 * 3600 + 20 * 60,
            ContextExcursionTodaySeconds: 0,
            TrackedTodaySeconds: 5 * 3600,
            DisplayedTrackedTodaySeconds: 5 * 3600,
            MostDistractedHourLabel: "15:00-15:59");

        IReadOnlyList<UserCheckInProject> projects =
        [
            new(1, "Nolan"),
            new(2, "Apollo Rebrand"),
            new(3, "Porta"),
        ];

        IReadOnlyList<TaskBoardLine> oneOpen = [Task(9, "Pricing page", "pending")];
        IReadOnlyList<TaskBoardLine> threeOpen =
        [
            Task(9, "Pricing page", "pending"),
            Task(10, "Client revisions", "pending"),
            Task(11, "Invoice chase", "pending"),
        ];
        // Deliberately MIXED: two tagged, one untagged. Untagged is a permanent valid state and
        // must look like a choice rather than a gap, so the contrast has to be visible here.
        IReadOnlyList<TaskBoardLine> mixedToday =
        [
            Task(9, "Pricing page", "done", measured: 4020, planned: 7200, project: "Nolan"),
            Task(10, "Client revisions", "active", measured: 1140, planned: 3600, project: "Apollo Rebrand"),
            Task(11, "Invoice chase", "pending", measured: 0, planned: 1800),
        ];

        // The width worst case for the row: a long title AND a long project on the same line.
        IReadOnlyList<TaskBoardLine> longNamesToday =
        [
            Task(9, "Rework the pricing page hero and table", "active",
                measured: 4020, planned: 7200, project: "Northgate Property Trust"),
            Task(10, "Client revisions for the Q3 board deck", "pending",
                measured: 1140, planned: 3600, project: "Apollo Rebrand"),
        ];
        IReadOnlyList<TaskBoardLine> yesterday =
        [
            Task(5, "keep improving the system", "done", measured: 6815, planned: 7200),
            Task(6, "writting", "abandoned", measured: 20, planned: 10800),
            Task(7, "working on apollo", "abandoned", measured: 0, planned: 3600),
        ];

        return
        [
            // ── morning ──
            new("morning fresh, no projects yet",
                UserCheckInCoordinator.BuildPrompt(UserCheckInKind.Morning, null, metrics)),

            new("morning fresh, projects available",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics, projects: projects)),

            new("morning proposed from one open task",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics, board: oneOpen, projects: projects)),

            new("morning three tasks and yesterday (height worst case)",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics,
                    board: threeOpen, yesterday: yesterday, projects: projects)),

            new("morning carried plan prefill",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics,
                    carriedPlan: "Ship the pricing page", projects: projects)),

            // The state that shipped broken on 2026-08-13: the options must open BENEATH the chip
            // and the chip must visibly change. A static list could not have shown either.
            new("morning PICKER OPEN",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics, board: oneOpen, projects: projects),
                PressProjectChip),

            new("morning PICKER OPEN on a three-task card",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics,
                    board: threeOpen, yesterday: yesterday, projects: projects),
                PressProjectChip),

            new("morning project PICKED",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics, board: oneOpen, projects: projects),
                window =>
                {
                    PressProjectChip(window);
                    PressButton(window, "Nolan");
                }),

            new("morning NEW PROJECT box open",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics, board: oneOpen, projects: projects),
                window =>
                {
                    PressProjectChip(window);
                    PressButton(window, "+ new");
                }),

            new("morning picker with no projects yet (plus-new only)",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics, board: oneOpen),
                PressProjectChip),

            // Chat 2 — the FIRST card of the day carries the day-off tap. Any of the four kinds can
            // be the first one, so both ends of the day are here: the card that usually opens a day
            // and the card that opens a day nobody worked.
            new("morning CHAT 2 — first card of the day, not working today",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Morning, null, metrics, board: threeOpen, projects: projects,
                    offersNotWorkingToday: true)),

            new("evening CHAT 2 — first card of the day, not working today",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                    offersNotWorkingToday: true)),

            // Chat 3 — the day from the fix plan, as the evening card now reports it. The task
            // carries the 40 minutes that were real, NOT the 3h40m the old rule credited, and it
            // is back under STILL OPEN because Ari auto-closed it rather than deciding it was
            // finished. The ask is the point: Ari does not know how it ended, so it asks.
            new("evening CHAT 3 — the film did not become work",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                    board:
                    [
                        Task(31, "Landing page copy", "pending",
                            measured: 2400, planned: 7200, project: "Northbridge"),
                        Task(32, "Send the March invoices", "done",
                            measured: 2400, planned: 2400, project: "Admin"),
                    ])),

            // ── midday ──
            new("midday with today's tasks",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Midday, goal, metrics, narrativeLine: "", board: mixedToday)),

            new("midday with no tasks (chip fallback)",
                UserCheckInCoordinator.BuildPrompt(UserCheckInKind.Midday, goal, metrics, narrativeLine: "")),

            // The width worst case for the project on a row: long title AND long project name.
            // Costs zero height (NoWrap + ellipsis); what it costs is title width, so this is the
            // state to look at before deciding the treatment is quiet enough.
            new("midday with long titles and long project names",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.Midday, goal, metrics, narrativeLine: "", board: longNamesToday)),

            // ── close-out (card four) ──
            new("close-out with open work",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.MiddayClose, goal, metrics, narrativeLine: "", board: mixedToday)),

            new("close-out with nothing open",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.MiddayClose, goal, metrics, narrativeLine: "")),

            new("close-out after a skipped midday",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.MiddayClose, goal, metrics, narrativeLine: "",
                    middaySkipped: true, board: mixedToday)),

            // ── evening ──
            new("evening with today's tasks",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "", board: mixedToday)),

            // Phase 5 — the agreed mockup's day: two Ari thinks are finished, one it thinks is not.
            // This is the state the resume sentence and the correction block are designed against,
            // so it is the one to look at when either changes.
            new("evening PHASE 5 — resume + correction (the mockup's day)",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                    board:
                    [
                        Task(21, "Rewrite the onboarding email", "done",
                            measured: 5100, planned: 5400, project: "Northbridge"),
                        Task(22, "Send the March invoices", "done",
                            measured: 2400, planned: 2400, project: "Admin"),
                        Task(23, "Landing page copy", "active",
                            measured: 7800, planned: 10800, project: "Northbridge"),
                    ])),

            // The LLM line must NOT win here: phase 5 makes the resume the message on this card.
            // Kept beside the state above so a regression shows up as two cards that disagree.
            new("evening PHASE 5 — resume still wins over an LLM line",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics,
                    narrativeLine: "What a day that was — you should be proud of the shape it took.",
                    board:
                    [
                        Task(21, "Rewrite the onboarding email", "done",
                            measured: 5100, planned: 5400, project: "Northbridge"),
                        Task(22, "Send the March invoices", "done",
                            measured: 2400, planned: 2400, project: "Admin"),
                        Task(23, "Landing page copy", "active",
                            measured: 7800, planned: 10800, project: "Northbridge"),
                    ])),

            // The "Good night" branch: tasks existed and every one of them is closed. Distinct from
            // "no tasks at all" below, and it was missing from the preview entirely.
            new("evening with everything finished",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                    board:
                    [
                        Task(9, "Pricing page", "done", measured: 4020, planned: 7200, project: "Nolan"),
                        Task(10, "Client revisions", "done", measured: 3300, planned: 3600, project: "Apollo Rebrand"),
                    ])),

            new("evening normal, no tasks",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                    middaySkipped: false, awayGap: false)),

            new("evening missed midday and away gap",
                UserCheckInCoordinator.BuildPrompt(
                    UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                    middaySkipped: true, awayGap: true)),
        ];
    }

    private static void PressProjectChip(UserCheckInCardWindow window)
        => PressButton(window, "+ project");

    private static void PressButton(UserCheckInCardWindow window, string content)
    {
        Button? button = Descendants<Button>(window)
            .FirstOrDefault(b => (b.Content as string) == content);
        button?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        window.UpdateLayout();
    }

    private static IReadOnlyList<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        void Walk(DependencyObject node)
        {
            foreach (object? child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject d)
                {
                    if (d is T match)
                    {
                        found.Add(match);
                    }

                    Walk(d);
                }
            }
        }

        Walk(root);
        return found;
    }

    private static TaskBoardLine Task(
        long id, string title, string state, double measured = 0d, int planned = 3600,
        string project = "")
        => new(id, title, planned, measured, state, DaysCarried: 0,
            EndedAtLocal: DateTimeOffset.Now.AddDays(-1), DeferredUntilDay: "", MovedOut: false,
            ProjectName: project);
}
