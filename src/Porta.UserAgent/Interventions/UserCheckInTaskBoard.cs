namespace Porta.UserAgent.Interventions;

using Porta.Agent.Common;
using Porta.Agent.LocalApi;

// Steps 4-7 — the pure rules behind "propose, don't ask", "one-tap done", carry-forward and the
// evening wrap. No I/O, no clock of its own, no UI: the host supplies today's task rows and the
// local time, and this decides what the card should show and what the day added up to.
//
// It reads LocalApiTask directly rather than mapping to a private type, matching
// UserCheckInCoordinator which already takes LocalApiDailyGoal straight from the client.
internal static class UserCheckInTaskBoard
{
    // Same ceiling as the morning card: proposing more than the card can hold would offer work the
    // user cannot accept.
    public const int MaxProposed = UserCheckInTaskPlanner.MaxMorningTasks;

    // Step 6 — "if something has been carried three days running, Ari says so once — plainly, not
    // naggingly." Three days is the threshold; the copy says it once and never escalates.
    public const int CarriedMentionThreshold = 3;

    private const string OpenPending = "pending";
    private const string OpenActive = "active";
    private const string ClosedDone = "done";

    /// <summary>
    /// Step 4 + 6 — what the morning card should arrive pre-filled with. Open work IS the carry
    /// forward: <c>ReadTasks</c> returns pending/active rows regardless of age, so unfinished work
    /// never has to be copied to a new day — it simply never left. Oldest first, so the thing that
    /// has been waiting longest is the thing the user is asked about first.
    /// </summary>
    public static IReadOnlyList<TaskBoardLine> Propose(
        IReadOnlyList<LocalApiTask>? tasks,
        DateTimeOffset nowLocal)
        => BuildBoard(tasks, nowLocal)
            .Where(line => line.IsOpen)
            .OrderByDescending(line => line.DaysCarried)
            .ThenBy(line => line.Id)
            .Take(MaxProposed)
            .ToList();

    /// <summary>
    /// Steps 5 + 7 — today's list with the measured time already beside each item. Order is the
    /// order the store returns (active first, then pending, then closed), which is the order the
    /// user built the day in.
    /// </summary>
    public static IReadOnlyList<TaskBoardLine> BuildBoard(
        IReadOnlyList<LocalApiTask>? tasks,
        DateTimeOffset nowLocal)
    {
        var board = new List<TaskBoardLine>();
        string todayGoalDay = UserCheckInStalenessPolicy.GoalDayOf(nowLocal);
        foreach (LocalApiTask task in tasks ?? Array.Empty<LocalApiTask>())
        {
            string title = (task.Title ?? string.Empty).Trim();
            if (title.Length == 0)
            {
                continue;
            }

            string deferredUntil = (task.DeferredUntilDay ?? string.Empty).Trim();

            board.Add(new TaskBoardLine(
                Id: task.Id,
                Title: title,
                PlannedSeconds: task.PlannedSeconds,
                MeasuredSeconds: task.ActiveSeconds,
                State: task.State ?? string.Empty,
                DaysCarried: DaysCarried(task.CreatedAtUtc.ToLocalTime(), nowLocal),
                EndedAtLocal: task.EndedAtUtc?.ToLocalTime(),
                DeferredUntilDay: deferredUntil,
                MovedOut: IsMovedOut(deferredUntil, todayGoalDay)));
        }

        return board;
    }

    /// <summary>
    /// Complaint 3 — is this task moved out past today?
    ///
    /// The value MUST parse as a real goal-day before it can hide anything. An earlier version
    /// compared the raw strings ordinally, and a malformed value like "not-a-day" sorts AFTER
    /// "2026-07-16" — so garbage in the column would have hidden real work indefinitely. A test
    /// caught it. Fail toward visible: anything unparseable means "not deferred".
    /// </summary>
    private static bool IsMovedOut(string deferredUntilDay, string todayGoalDay)
        => deferredUntilDay.Length > 0
            && DateOnly.TryParseExact(
                deferredUntilDay,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out DateOnly until)
            && DateOnly.TryParseExact(
                todayGoalDay,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out DateOnly today)
            && until > today;

    /// <summary>
    /// Complaint 3 (2026-08-11) — the open work the EVENING card should still ask about: everything
    /// except what the close-out card already moved to a later day.
    ///
    /// "A card that asks me what moves and then forgets is the exact thing this project exists to
    /// fix." Asking again about work the user moved an hour earlier is precisely that forgetting.
    /// </summary>
    public static IReadOnlyList<TaskBoardLine> StillToAsk(IReadOnlyList<TaskBoardLine> board)
        => board.Where(line => line.IsOpen && !line.MovedOut).ToList();

    /// <summary>
    /// Complaint 3 — what the evening card SAYS about the work already moved. One plain sentence,
    /// named rather than counted when it is one or two items, because "1 item moved" tells the user
    /// nothing he did not already know. Empty when nothing was moved.
    /// </summary>
    public static string MovingNotice(IReadOnlyList<TaskBoardLine> board)
    {
        List<TaskBoardLine> moved = board.Where(line => line.IsOpen && line.MovedOut).ToList();

        if (moved.Count == 0)
        {
            return string.Empty;
        }

        return moved.Count <= 2
            ? $"Moving to tomorrow: {string.Join(", ", moved.Select(m => m.Title))}."
            : $"Moving to tomorrow: {moved.Count} items.";
    }

    /// <summary>
    /// Step 6 — how many goal-days this task has survived. Counted in goal-days (the 05:00
    /// boundary), not in 24-hour blocks, so a task created at 23:00 and still open at 06:00 the
    /// next morning has carried exactly one day rather than zero.
    /// </summary>
    public static int DaysCarried(DateTimeOffset createdAtLocal, DateTimeOffset nowLocal)
    {
        DateOnly created = DateOnly.Parse(UserCheckInStalenessPolicy.GoalDayOf(createdAtLocal));
        DateOnly today = DateOnly.Parse(UserCheckInStalenessPolicy.GoalDayOf(nowLocal));
        int days = today.DayNumber - created.DayNumber;
        return days < 0 ? 0 : days;
    }

    /// <summary>
    /// Step 7 — "You planned three things. You finished two." The evening card's opening line, and
    /// the only place the day is scored. Empty when there was nothing planned, so a day with no
    /// tasks gets no invented summary.
    /// </summary>
    public static string SummarizeDay(IReadOnlyList<TaskBoardLine> board)
    {
        int planned = board.Count;
        if (planned == 0)
        {
            return string.Empty;
        }

        int finished = board.Count(line => line.IsDone);
        string plannedPart = planned == 1 ? "You planned one thing." : $"You planned {Spell(planned)} things.";
        string finishedPart = (planned, finished) switch
        {
            // The one-task day is the common case, and "You finished one" reads oddly straight
            // after "You planned one thing".
            (1, 1) => "You finished it.",
            (1, _) => "You didn't get to it.",
            (_, 0) => "You finished none of them.",
            _ when finished == planned => "You finished them all.",
            (_, 1) => "You finished one.",
            _ => $"You finished {Spell(finished)}."
        };

        return $"{plannedPart} {finishedPart}";
    }

    /// <summary>
    /// Step 5 — the midday yes/partly/not_yet answer, derived from what the user ticked rather than
    /// asked as a second question. Keeps daily_goals a view over the tasks and keeps the existing
    /// ground-truth label and MiddayRecorded gate working untouched.
    /// </summary>
    public static string MiddayAnswerFor(IReadOnlyList<UserCheckInTaskOutcome>? outcomes)
    {
        if (outcomes is null || outcomes.Count == 0)
        {
            return string.Empty;
        }

        int done = outcomes.Count(o => string.Equals(o.Action, UserCheckInTaskOutcome.Done, StringComparison.Ordinal));
        if (done == 0)
        {
            return "not_yet";
        }

        return done == outcomes.Count ? "yes" : "partly";
    }

    /// <summary>
    /// Step 8 — what yesterday came to: the tasks CLOSED during yesterday's goal-day, newest first.
    /// </summary>
    /// <remarks>
    /// Closed only, on purpose. Anything still open is carried and already appears in today's rows
    /// as a proposal, so including it here would show the same title twice on one card.
    /// </remarks>
    public static IReadOnlyList<TaskBoardLine> Yesterday(
        IReadOnlyList<LocalApiTask>? tasks,
        DateTimeOffset nowLocal)
    {
        string yesterday = UserCheckInStalenessPolicy.GoalDayOf(nowLocal.AddDays(-1));
        return BuildBoard(tasks, nowLocal)
            .Where(line => !line.IsOpen)
            .Where(line => line.EndedAtLocal is { } ended
                && UserCheckInStalenessPolicy.GoalDayOf(ended) == yesterday)
            .OrderByDescending(line => line.EndedAtLocal)
            .ThenBy(line => line.Id)
            .ToList();
    }

    /// <summary>
    /// Step 6 — the one plain sentence about work that keeps sliding. Said once, about the single
    /// worst offender, and never escalated; empty when nothing has crossed the threshold.
    /// </summary>
    public static string CarriedNotice(IReadOnlyList<TaskBoardLine> board)
    {
        TaskBoardLine? worst = board
            .Where(line => line.IsOpen && line.DaysCarried >= CarriedMentionThreshold)
            .OrderByDescending(line => line.DaysCarried)
            .FirstOrDefault();

        return worst is null
            ? string.Empty
            : $"“{worst.Title}” has moved forward {worst.DaysCarried} days running.";
    }

    // "three" reads better than "3" in a sentence; past four the digit is fine and the ceiling is
    // three anyway, so this never has to be clever.
    private static string Spell(int n) => n switch
    {
        1 => "one",
        2 => "two",
        3 => "three",
        _ => n.ToString()
    };
}

// One row of the day's board. MeasuredSeconds is present-and-working time, not wall clock:
// AttributeBucketToActiveTask credits nothing while the user is away, so this will always read
// lower than the clock on the wall. That is the right number to put in front of someone — but the
// card has to say what it means, or it reads as undercounting.
internal sealed record TaskBoardLine(
    long Id,
    string Title,
    int PlannedSeconds,
    double MeasuredSeconds,
    string State,
    int DaysCarried,
    DateTimeOffset? EndedAtLocal = null,
    // Complaint 3 — the goal-day this task was moved to by the close-out card. Empty = not moved.
    string DeferredUntilDay = "",
    // Stamped by BuildBoard, which is the one place that knows what "today" is. Carrying the
    // resolved answer means the card renderers never need a clock of their own.
    bool MovedOut = false)
{
    public bool IsOpen => State is "pending" or "active";

    /// <summary>
    /// Complaint 3 (2026-08-11) — has the user already said this moves to a later day? The evening
    /// card uses this to stop asking about work he has just moved.
    ///
    /// Resolved by BuildBoard, which is the one place that knows what "today" is; see MovedOut.
    /// </summary>
    public bool IsDeferredBeyond(string todayGoalDay) => MovedOut;

    public bool IsDone => string.Equals(State, "done", StringComparison.Ordinal);

    /// <summary>
    /// The measured time. Sub-minute work is rendered in seconds rather than rounded to "0m":
    /// twenty seconds against a planned three hours is the most eloquent number on the card, and
    /// rounding it away would delete exactly the fact worth seeing.
    /// </summary>
    public string MeasuredLabel
        => MeasuredSeconds > 0d && MeasuredSeconds < 60d
            ? $"{(int)Math.Round(MeasuredSeconds)}s"
            : ReminderTimeParser.FormatDuration((int)Math.Round(MeasuredSeconds / 60d));

    /// <summary>
    /// Measured against planned, clamped to 0..1 — what the row's bar fills to. Planned is the
    /// commitment, so overrunning it reads as full rather than as an error.
    /// </summary>
    public double ProgressFraction
        => PlannedSeconds <= 0 ? 0d : Math.Clamp(MeasuredSeconds / PlannedSeconds, 0d, 1d);
}
