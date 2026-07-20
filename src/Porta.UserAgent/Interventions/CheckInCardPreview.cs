#if DEBUG
namespace Porta.UserAgent.Interventions;

using System.Collections.Generic;
using System.Threading;
using System.Windows;
using Porta.Agent.LocalApi;

// DEBUG-ONLY smoke harness. Compiled out of the shipped (Release) agent entirely — there is no
// production surface. Renders the REAL check-in card (UserCheckInCardWindow) built by the REAL
// coordinator (UserCheckInCoordinator.BuildPrompt), so what you see is pixel-for-pixel what the
// fleet gets. Used by `Porta.UserAgent.exe --preview-checkins` to eyeball, without fighting the live
// scheduler/timeline: (1) the fixed question label sits above the buttons/box on midday + EOD, and
// (2) the away-day EOD reads as the combined wrap, not the generic "Today's goal was …" line.
//
// Cards show in sequence; answer or dismiss (✕ / Esc) each to advance to the next. Order:
//   1) Morning   2) Midday   3) End-of-day (normal)   4) End-of-day (missed-midday + away wrap)
internal static class CheckInCardPreview
{
    public static void Run()
    {
        var thread = new Thread(RunStaLoop) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void RunStaLoop()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        List<UserCheckInPrompt> prompts = BuildPreviewPrompts();

        int index = 0;
        UserCheckInCardWindow? card = null;

        void ShowNext()
        {
            if (index >= prompts.Count)
            {
                app.Shutdown();
                return;
            }

            card!.ShowPrompt(prompts[index++]);
        }

        // The card hides itself on answer/dismiss and raises the response; we just advance.
        card = new UserCheckInCardWindow(_ => card!.Dispatcher.BeginInvoke((Action)ShowNext));

        app.Startup += (_, _) => ShowNext();
        app.Run();
    }

    private static List<UserCheckInPrompt> BuildPreviewPrompts()
    {
        var goal = new LocalApiDailyGoal(
            LocalDate: "2026-07-21",
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

        return new List<UserCheckInPrompt>
        {
            // 1) Morning — fresh ask (no fixed label; morning owns its own goal box).
            UserCheckInCoordinator.BuildPrompt(UserCheckInKind.Morning, goal: null, metrics),

            // 2) Midday — report line + "Did you get to your goal?" above Yes / Partly / Not-yet.
            UserCheckInCoordinator.BuildPrompt(UserCheckInKind.Midday, goal, metrics, narrativeLine: ""),

            // 3) End-of-day (normal) — generic "Today's goal was …" + "Plan for tomorrow?" above the box.
            UserCheckInCoordinator.BuildPrompt(
                UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                middaySkipped: false, awayGap: false),

            // 4) End-of-day (missed midday + away gap) — the combined away-aware wrap fallback copy.
            UserCheckInCoordinator.BuildPrompt(
                UserCheckInKind.EndOfDay, goal, metrics, narrativeLine: "",
                middaySkipped: true, awayGap: true),
        };
    }
}
#endif
