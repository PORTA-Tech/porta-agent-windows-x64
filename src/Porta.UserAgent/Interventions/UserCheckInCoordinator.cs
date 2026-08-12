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
    // Part 1a/1b — activeWorkSinceMorning (tracked minus away today) and awayToday come from the
    // day's aggregated metrics and drive the work-based midday trigger and the EOD away-gap framing.
    // Both are optional so callers/tests without metrics stay on the wall-clock fallback (null active
    // work) with no away gap.
    public static DailyGoalSnapshot BuildSnapshot(
        LocalApiDailyGoal? goal,
        TimeSpan? activeWorkSinceMorning = null,
        TimeSpan awayToday = default)
    {
        if (goal is null)
        {
            return new DailyGoalSnapshot(
                MorningRecorded: false,
                MiddayRecorded: false,
                EndOfDayRecorded: false,
                MorningAtLocal: null,
                ActiveWorkSinceMorning: activeWorkSinceMorning,
                AwayToday: awayToday);
        }

        bool morningRecorded = !string.IsNullOrWhiteSpace(goal.GoalText);
        return new DailyGoalSnapshot(
            MorningRecorded: morningRecorded,
            MiddayRecorded: !string.IsNullOrWhiteSpace(goal.MiddayAnswer),
            EndOfDayRecorded: goal.EndOfDayAtUtc.HasValue,
            MorningAtLocal: morningRecorded ? goal.CreatedAtUtc.ToLocalTime() : null,
            ActiveWorkSinceMorning: activeWorkSinceMorning,
            AwayToday: awayToday);
    }

    // Builds the card frame for the due kind. Reuses the already-aggregated day metrics (never a
    // recompute of capture) for the midday/EOD report line; the morning card asks a fresh question.
    // Part 5 — narrativeLine is the LLM-written copy for this moment (already gated: the caller
    // decided to SPEAK before fetching it). Empty/null = fail-soft to the short honest templates
    // below, which are specific to the moment and the goal — never a generic filler line.
    // Part 1b — middaySkipped/awayGap let the EOD fallback copy acknowledge a skipped midday and an
    // away gap (the away-aware wrap), instead of the generic "Today's goal was: …". They default off
    // so morning/midday callers are unaffected.
    // Steps 4-7 — board is today's task list with measured time (empty before step 3 has run, or on
    // a day with no tasks, in which case every surface falls back to its pre-step-3 shape).
    public static UserCheckInPrompt BuildPrompt(
        UserCheckInKind kind,
        LocalApiDailyGoal? goal,
        LocalTodaySummaryMetrics metrics,
        string? carriedPlan = null,
        string? narrativeLine = null,
        bool middaySkipped = false,
        bool awayGap = false,
        IReadOnlyList<TaskBoardLine>? board = null,
        // Step 8 — yesterday's closed tasks, shown quietly above today's list on the morning card.
        IReadOnlyList<TaskBoardLine>? yesterday = null,
        // Step 9a — the projects the picker offers, most-recently-used first. Only the morning
        // card names tasks, so only the morning prompts carry it; the others are untouched.
        IReadOnlyList<UserCheckInProject>? projects = null)
    {
        string goalText = (goal?.GoalText ?? string.Empty).Trim();
        string plan = (carriedPlan ?? string.Empty).Trim();
        string line = (narrativeLine ?? string.Empty).Trim();
        IReadOnlyList<TaskBoardLine> tasks = board ?? Array.Empty<TaskBoardLine>();
        // Complaint 3 — work the close-out card already moved to tomorrow is NOT still to be asked
        // about. Filtering here rather than in each card keeps every surface honest by default: a
        // commitment the user made an hour ago must not be re-asked as though it never happened.
        IReadOnlyList<TaskBoardLine> open = UserCheckInTaskBoard.StillToAsk(tasks);
        string movingNotice = UserCheckInTaskBoard.MovingNotice(tasks);
        string carriedNotice = UserCheckInTaskBoard.CarriedNotice(tasks);
        IReadOnlyList<TaskBoardLine> lastDay = yesterday ?? Array.Empty<TaskBoardLine>();
        string yesterdaySummary = UserCheckInTaskBoard.SummarizeDay(lastDay);

        return kind switch
        {
            // Step 4 — propose, never an empty box. Unfinished work IS the proposal, so the morning
            // opens with something to confirm rather than something to compose. This outranks the
            // carried-plan copy below because a real open task beats a sentence typed last night.
            UserCheckInKind.Morning when open.Count > 0 => new UserCheckInPrompt(
                Kind: UserCheckInKind.Morning,
                Title: open.Count == 1 ? "Still on this today?" : "Today's list — look right?",
                Message: line.Length > 0 ? line : BuildProposalTemplate(open),
                Tasks: open,
                CarriedNotice: carriedNotice,
                Yesterday: lastDay,
                YesterdaySummary: yesterdaySummary,
                Projects: projects),

            // Part 3 — memory: when last night's plan carried over, open with it (confirm/edit)
            // instead of a blank ask. The plan pre-fills the goal box so confirming is one click.
            UserCheckInKind.Morning when plan.Length > 0 => new UserCheckInPrompt(
                Kind: UserCheckInKind.Morning,
                Title: "Still the goal for today?",
                Message: line.Length > 0 ? line : $"Last night you planned: “{plan}”. Confirm it or change it below.",
                PrefillGoalText: plan,
                Yesterday: lastDay,
                YesterdaySummary: yesterdaySummary,
                Projects: projects),

            UserCheckInKind.Morning => new UserCheckInPrompt(
                Kind: UserCheckInKind.Morning,
                Title: "What's the one thing today?",
                Message: line.Length > 0 ? line : "Name today's focus and I'll help you protect it.",
                Yesterday: lastDay,
                YesterdaySummary: yesterdaySummary,
                Projects: projects),

            // Step 5 — the midday card lists today's tasks with the hours already beside them, so
            // "done" is a tap. The question label changes with it: it is no longer about "the goal".
            UserCheckInKind.Midday => new UserCheckInPrompt(
                Kind: UserCheckInKind.Midday,
                Title: "How's it going?",
                Message: line.Length > 0
                    ? line
                    : open.Count > 0
                        ? "Here's where today stands."
                        : goalText.Length > 0
                            ? $"This morning you said: “{goalText}”. Did you get to it?"
                            : "Did you get to what mattered so far?",
                GoalText: goalText,
                ReportLine: BuildReportLine(metrics),
                Tasks: tasks,
                // Part 2b — the fixed question the answer controls hang off, always present.
                QuestionLabel: open.Count > 0 ? "Anything done?" : "Did you get to your goal?"),

            // Complaint 3 — the FOURTH card. It must not clone the first midday. The first looks
            // backward ("done, or not yet"); this one looks forward: what is still open, and does it
            // realistically land today or move to tomorrow.
            //
            // middaySkipped here means the FIRST midday never happened. The card says so plainly
            // rather than pretending the day was tracked — the same honesty rule the EOD wrap
            // already follows for a skipped midday.
            UserCheckInKind.MiddayClose => new UserCheckInPrompt(
                Kind: UserCheckInKind.MiddayClose,
                Title: open.Count > 0 ? "What's still open?" : "Anything left today?",
                Message: line.Length > 0
                    ? line
                    : middaySkipped
                        ? "We didn't check in earlier. Here's where the day actually stands."
                        : open.Count > 0
                            ? "Last stretch. Say what lands today and what moves."
                            : "Nothing's open. Anything you want on tomorrow's list?",
                GoalText: goalText,
                ReportLine: BuildReportLine(metrics),
                Tasks: tasks,
                CarriedNotice: carriedNotice,
                QuestionLabel: "Does this land today?"),

            // Step 7 — the evening closes the day. The card renders ReportLine as its own block
            // (same as midday), so the message stays reflection copy and the measured numbers are
            // never displaced by the LLM.
            UserCheckInKind.EndOfDay => new UserCheckInPrompt(
                Kind: UserCheckInKind.EndOfDay,
                Title: "Wrapping up",
                Message: line.Length > 0
                    ? line
                    : tasks.Count > 0
                        ? UserCheckInTaskBoard.SummarizeDay(tasks)
                        : BuildEndOfDayTemplate(goalText, awayGap),
                GoalText: goalText,
                ReportLine: BuildReportLine(metrics),
                Tasks: tasks,
                // Complaint 3 — when the close-out card already moved work, the evening card SAYS
                // so rather than staying silent about it. The user told Ari something an hour ago;
                // repeating it back is the difference between a commitment and a note. The carried
                // notice keeps priority on the rare day both apply, since it is the older concern.
                CarriedNotice: carriedNotice.Length > 0 ? carriedNotice : movingNotice,
                // Step 7 — no longer "Plan for tomorrow?". The evening asks what to do with what is
                // left, which makes tomorrow's list without opening a new question.
                QuestionLabel: open.Count > 0
                    ? "What moves to tomorrow?"
                    : movingNotice.Length > 0 ? "Anything else?" : "Anything to note?"),
        };
    }

    // Step 4 — the honest proposal copy. Names the work rather than describing it, because the list
    // is right underneath and repeating it in prose would make the card longer for nothing.
    private static string BuildProposalTemplate(IReadOnlyList<TaskBoardLine> open)
    {
        if (open.Count == 1)
        {
            return $"“{open[0].Title}” is still open. Keep it, or change it below.";
        }

        return "Still open from before. Change anything that's wrong, then send.";
    }

    // Part 1b — the honest EOD fallback. When there was a substantial away gap, frame it as a
    // whole-day wrap that acknowledges the time away rather than reading as a full work day. Only the
    // away gap changes the wording; a skipped-but-present midday is left to the LLM line to nuance.
    private static string BuildEndOfDayTemplate(string goalText, bool awayGap)
    {
        if (goalText.Length == 0)
        {
            return "Wrapping up the day.";
        }

        return awayGap
            ? $"You were away for a stretch today — here's how the day landed against “{goalText}”."
            : $"Today's goal was: “{goalText}”.";
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
