namespace Porta.Agent.Core;

/// <summary>
/// One task line in the weekly digest — a thing that finished, or a thing that carried.
///
/// <c>Seconds</c> is the time measured against this task INSIDE the week, never its lifetime
/// total: the weekly reports on a week, and a task that has run for a month must not arrive
/// claiming a month of hours against seven days.
/// </summary>
public sealed record AgentWeeklyTaskLine(
    string Title,
    /// <summary>The project's name, or empty for untagged work. Empty is a first-class state:
    /// the email prints no project suffix and never invents an "Untagged" category.</summary>
    string ProjectName,
    double Seconds,
    /// <summary>
    /// How many days this has been open, counted from the day it was added to the board up to the
    /// end of the week. Zero on a finished line, where it means nothing.
    ///
    /// This is the only number that separates STUCK from SLOW, which is the whole reason the
    /// "what carried" block exists as something other than a grey half of "what finished".
    /// </summary>
    int DaysOpen);

/// <summary>Measured time against one project across the week. Untagged work is never a row.</summary>
public sealed record AgentWeeklyProjectLine(string Name, double Seconds);

/// <summary>
/// Everything the weekly digest needs that comes out of the task store. The day shape and the
/// week's tracked/focused/away totals do NOT live here — they come from the timeline, through the
/// same path the daily digest already trusts, so the two cannot disagree.
/// </summary>
public sealed record AgentWeeklyDigestData(
    /// <summary>
    /// The week's board and its score, counted through the SAME rule as the evening card and the
    /// daily digest — <c>UserCheckInTaskBoard.SummarizeDay</c>, where planned is every line on the
    /// board and finished is <c>state == "done"</c>. Sent as numbers precisely so the weekly and
    /// the daily can never disagree about what "finished" means.
    /// </summary>
    int PlannedCount,
    int FinishedCount,
    /// <summary>The evidence, longest first. May be SHORTER than <see cref="FinishedCount"/> —
    /// the count is the score, the list is what fits on a page.</summary>
    IReadOnlyList<AgentWeeklyTaskLine> Finished,
    IReadOnlyList<AgentWeeklyTaskLine> Carried,
    IReadOnlyList<AgentWeeklyProjectLine> Projects)
{
    public static AgentWeeklyDigestData Empty { get; } = new(
        0, 0,
        Array.Empty<AgentWeeklyTaskLine>(),
        Array.Empty<AgentWeeklyTaskLine>(),
        Array.Empty<AgentWeeklyProjectLine>());
}
