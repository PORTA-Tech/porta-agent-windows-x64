namespace Porta.UserAgent.Interventions;

using Porta.Agent.Common;
using Porta.Agent.LocalApi;

// Workstream D — the pure glue between the goal row (from the LocalApi) and the check-in surface.
// No I/O, no clock, no UI, so the mapping (which is easy to get subtly wrong) and the report/prompt
// copy are unit-tested in isolation. The host owns the async round-trips and the STA presenter; it
// calls these to turn a goal row into the scheduler's snapshot and into the card's prompt.
internal static class UserCheckInCoordinator
{
    // Maps the day's goal row into the scheduler's snapshot. A null row = a fresh day with no goal
    // yet (morning is due). MorningRecorded keys off a non-empty goal_text so an end-of-day-only row
    // (recorded without a morning goal) still reads as "no morning goal stated".
    public static DailyGoalSnapshot BuildSnapshot(LocalApiDailyGoal? goal)
    {
        if (goal is null)
        {
            return new DailyGoalSnapshot(
                MorningRecorded: false,
                MiddayRecorded: false,
                EndOfDayRecorded: false,
                MorningAtLocal: null);
        }

        bool morningRecorded = !string.IsNullOrWhiteSpace(goal.GoalText);
        return new DailyGoalSnapshot(
            MorningRecorded: morningRecorded,
            MiddayRecorded: !string.IsNullOrWhiteSpace(goal.MiddayAnswer),
            EndOfDayRecorded: goal.EndOfDayAtUtc.HasValue,
            MorningAtLocal: morningRecorded ? goal.CreatedAtUtc.ToLocalTime() : null);
    }

    // Builds the card frame for the due kind. Reuses the already-aggregated day metrics (never a
    // recompute of capture) for the midday/EOD report line; the morning card asks a fresh question.
    public static UserCheckInPrompt BuildPrompt(
        UserCheckInKind kind,
        LocalApiDailyGoal? goal,
        LocalTodaySummaryMetrics metrics)
    {
        string goalText = (goal?.GoalText ?? string.Empty).Trim();
        return kind switch
        {
            UserCheckInKind.Morning => new UserCheckInPrompt(
                Kind: UserCheckInKind.Morning,
                Title: "What's the one thing today?",
                Message: "Name today's focus and I'll help you protect it."),

            UserCheckInKind.Midday => new UserCheckInPrompt(
                Kind: UserCheckInKind.Midday,
                Title: "How's it going?",
                Message: goalText.Length > 0
                    ? $"This morning you said: “{goalText}”. Did you get to it?"
                    : "Did you get to what mattered so far?",
                GoalText: goalText,
                ReportLine: BuildReportLine(metrics)),

            _ => new UserCheckInPrompt(
                Kind: UserCheckInKind.EndOfDay,
                Title: "Wrapping up",
                Message: goalText.Length > 0
                    ? $"Today's goal was: “{goalText}”. {BuildReportLine(metrics)}".TrimEnd()
                    : BuildReportLine(metrics),
                GoalText: goalText,
                ReportLine: BuildReportLine(metrics)),
        };
    }

    // A short, honest focus summary from the day's aggregated timeline. Empty when there is no
    // processed timeline yet, so the card simply omits the line rather than showing zeros.
    public static string BuildReportLine(LocalTodaySummaryMetrics metrics)
    {
        if (metrics is null || !metrics.HasProcessedTimeline)
        {
            return string.Empty;
        }

        string focused = ReminderTimeParser.FormatDuration((int)Math.Round(metrics.FocusedTodaySeconds / 60d));
        int offTrackMin = (int)Math.Round(metrics.DistractionTodaySeconds / 60d);
        int awayMin = (int)Math.Round(metrics.AwayTodaySeconds / 60d);
        int qualityPct = (int)Math.Round(metrics.FocusQualityPercent);

        string line = $"So far: {focused} focused";
        if (metrics.FocusedTodaySeconds > 0d && qualityPct > 0)
        {
            line += $" · {qualityPct}% focus quality";
        }
        if (offTrackMin > 0)
        {
            line += $" · {offTrackMin}m off-track";
        }

        if (awayMin > 0)
        {
            line += $" · {awayMin}m away";
        }

        return line + ".";
    }
}
