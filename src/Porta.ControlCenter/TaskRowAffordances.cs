namespace Porta.ControlCenter;

using Porta.Agent.LocalApi;

/// <summary>
/// Chat 5 (2026-08-24) — what one task row lets you DO about the line a client reads.
///
/// Extracted because the page got this wrong in a way that only clicking revealed, and there was no
/// test that could have caught it. Two holes, both of them dead ends:
///
///   * A SAVED line rendered as plain text with no handler. The only route to the editor was the
///     amber prompt shown while the line was still empty, so the moment you saved one the editor
///     became unreachable — no double click, no context menu, nothing. A typo in the one sentence
///     the client reads was permanent.
///   * An UNTAGGED task was told "no project, so it can never appear in a client report" and given
///     nothing to do about it. True, and a dead end.
///
/// The rules read as one sentence each, which is the point: a row always offers a way forward.
/// </summary>
internal readonly record struct TaskRowAffordances(
    bool EditableResultLine,
    bool PromptForMissingLine,
    bool OfferProject)
{
    public static TaskRowAffordances For(LocalApiTaskRow t)
    {
        bool hasLine = t.ResultLine.Trim().Length > 0;

        return new TaskRowAffordances(
            // A line that exists is its own edit control.
            EditableResultLine: hasLine,

            // The amber prompt is for a task that COULD carry a line and does not. An untagged task
            // could not, so prompting for one would be asking for something that goes nowhere —
            // it gets the project offer below instead.
            PromptForMissingLine: !hasLine && !t.IsUntagged,

            // Offered whenever the task is untagged, line or no line. An untagged task WITH a line
            // is the same dead end and arguably the worse one: the writing is already done and the
            // client still cannot see it.
            OfferProject: t.IsUntagged);
    }
}
