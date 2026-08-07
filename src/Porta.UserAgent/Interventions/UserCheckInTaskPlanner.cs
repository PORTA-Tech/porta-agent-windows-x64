namespace Porta.UserAgent.Interventions;

// Step 3 — the pure rule that turns what the user tapped on the morning card into the list of real
// tasks to create. No I/O, no clock, no UI, so the part that is easy to get subtly wrong (dedupe
// against work that is already open, the ceiling, the goal-text projection) is unit-tested on its
// own. The host owns the round-trips; this only decides.
//
// House style: every refusal carries a reason code. A clamped duration is NOT a refusal — the chips
// can only produce legal values, so clamping exists purely to keep a malformed caller from being
// rejected by CreateTask (which returns null under 60s or over 8h, SqliteBrainStateStore.cs:2154).
internal static class UserCheckInTaskPlanner
{
    // Locked 2026-08-07: one box, "+ add" for more, three is the ceiling.
    public const int MaxMorningTasks = 3;
    public const int DefaultPlannedSeconds = 3600;
    public const int MinPlannedSeconds = 60;
    public const int MaxPlannedSeconds = 8 * 3600;

    // The separator that joins the day's task titles into the one goal sentence daily_goals keeps.
    // Readable in an email, unambiguous to the model reading the fact sheet.
    private const string TitleSeparator = "; ";

    public static MorningTaskPlan Plan(
        IReadOnlyList<UserCheckInTaskEntry>? entries,
        IReadOnlyList<string>? openTaskTitles)
    {
        var accepted = new List<PlannedMorningTask>();
        var skipped = new List<SkippedMorningTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var open = new HashSet<string>(
            (openTaskTitles ?? Array.Empty<string>()).Select(t => (t ?? string.Empty).Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (UserCheckInTaskEntry entry in entries ?? Array.Empty<UserCheckInTaskEntry>())
        {
            string title = (entry?.Title ?? string.Empty).Trim();
            if (title.Length == 0)
            {
                // An empty row is the normal state of rows the user never filled in, not an error.
                continue;
            }

            if (!seen.Add(title))
            {
                skipped.Add(new SkippedMorningTask(title, "duplicate_in_list"));
                continue;
            }

            if (accepted.Count >= MaxMorningTasks)
            {
                skipped.Add(new SkippedMorningTask(title, "over_max_tasks"));
                continue;
            }

            // Already open = the carry-forward case: the user re-confirmed work that is still on the
            // list. Accepted (it counts as today's plan) but the host must not create a second copy.
            accepted.Add(new PlannedMorningTask(
                Title: title,
                PlannedSeconds: ClampSeconds(entry!.PlannedSeconds),
                AlreadyOpen: open.Contains(title)));
        }

        return new MorningTaskPlan(accepted, skipped, JoinTitles(accepted.Select(a => a.Title)));
    }

    /// <summary>
    /// The fallback for a response that carries no task entries — an older card, or any path that
    /// only produced the single goal sentence. Keeps the pre-step-3 behaviour intact rather than
    /// dropping the answer on the floor.
    /// </summary>
    public static IReadOnlyList<UserCheckInTaskEntry> SingleEntryFrom(string? goalText)
    {
        string title = (goalText ?? string.Empty).Trim();
        return title.Length == 0
            ? Array.Empty<UserCheckInTaskEntry>()
            : new[] { new UserCheckInTaskEntry(title, DefaultPlannedSeconds) };
    }

    /// <summary>
    /// The projection that makes daily_goals a view over the task list rather than a parallel
    /// record: the day's goal sentence is now derived from the titles, never authored separately.
    /// </summary>
    public static string JoinTitles(IEnumerable<string> titles)
        => string.Join(TitleSeparator, titles.Select(t => (t ?? string.Empty).Trim()).Where(t => t.Length > 0));

    private static int ClampSeconds(int plannedSeconds)
    {
        if (plannedSeconds < MinPlannedSeconds)
        {
            return DefaultPlannedSeconds;
        }

        return plannedSeconds > MaxPlannedSeconds ? MaxPlannedSeconds : plannedSeconds;
    }
}

// What the host should do with the morning list. Accepted is in the order the user entered it, so
// the first entry is the one that gets auto-started.
internal sealed record MorningTaskPlan(
    IReadOnlyList<PlannedMorningTask> Accepted,
    IReadOnlyList<SkippedMorningTask> Skipped,
    string GoalText);

// AlreadyOpen: a task with this exact title is still pending or active, so it is re-confirmed
// rather than duplicated.
internal sealed record PlannedMorningTask(string Title, int PlannedSeconds, bool AlreadyOpen);

internal sealed record SkippedMorningTask(string Title, string ReasonCode);
