namespace Porta.UserAgent.Interventions;

// Workstream D — conversational check-ins. Four surfaces render in the compact bottom-right
// corner card (C2 channel): morning goal, midday progress, the second midday close-out, and the
// end-of-day wrap.
//
// Complaint 3 (2026-08-11) — MiddayClose is the FOURTH card and it must ask a different question
// from Midday. Midday looks backward ("done, or not yet"); MiddayClose looks forward ("what is
// still open, and does it land today or move"). Cloning the first card teaches the user to ignore
// both.
internal enum UserCheckInKind
{
    Morning,
    Midday,
    MiddayClose,
    EndOfDay
}

// One frame of a check-in the card should render. Fields not relevant to a kind are empty.
internal sealed record UserCheckInPrompt(
    UserCheckInKind Kind,
    string Title,
    string Message,
    string GoalText = "",       // midday/EOD: the goal the user stated in the morning
    string ReportLine = "",     // midday: the measured report (top apps + focused/off-track/away)
    string PrefillGoalText = "", // morning: last night's plan, pre-filled so confirm = one click
    // Step 4 — the morning card arrives pre-filled from yesterday's unfinished work: correcting is
    // far easier than composing, and if the user taps nothing at all Ari still has a sensible list.
    // Steps 5 + 7 — on midday/EOD this is today's board, with the measured time already beside each
    // item. Empty = fall back to the older single-goal surfaces, unchanged.
    IReadOnlyList<TaskBoardLine>? Tasks = null,
    // Step 6 — the one plain sentence about work that has carried for days. Empty almost always.
    string CarriedNotice = "",
    // Step 8 — yesterday, on the morning card: the tasks closed during yesterday's goal-day, and
    // the one sentence that scores them. Empty on a first day, or a day when nothing was closed.
    IReadOnlyList<TaskBoardLine>? Yesterday = null,
    string YesterdaySummary = "",
    // Part 2b — a fixed question label the card renders directly above its answer controls, so the
    // Yes/Partly/Not-yet buttons and the EOD plan box always have a question attached even when the
    // LLM line is odd or the narrative call failed. Empty = no controls (morning uses its own box).
    string QuestionLabel = "",
    // Step 9a — the projects the picker offers, most-recently-used first. The card renders at most
    // the first six plus "+ new"; the rest are managed in Control Center. Empty on every card that
    // does not name a task, and on any older caller.
    IReadOnlyList<UserCheckInProject>? Projects = null);

// Step 3 — one line of the morning list: what the user intends to do, and how long they tapped for
// it. The duration is always chosen from the chips, never typed (locked decision 2026-08-07), so a
// number here is a tap, not free text.
/// <summary>
/// Step 9a — ProjectId is the project the user PICKED on this row. 0 = untagged, which is the
/// default, a permanent valid state, and never nagged about. Ari never guesses it.
///
/// ProjectName is set INSTEAD of ProjectId when the user typed a new name into "+ new". The card
/// does no I/O of its own, so it hands the name over and the host resolves it through the store's
/// create-or-get. That is also why a name typed twice cannot make two projects.
/// </summary>
internal sealed record UserCheckInTaskEntry(
    string Title,
    int PlannedSeconds,
    long ProjectId = 0,
    string ProjectName = "");

// Step 9a — one project as the picker sees it. On a card a project is only a name.
internal sealed record UserCheckInProject(long Id, string Name);

// Steps 5 + 7 — what the user tapped against one task on the midday or evening card. Done and
// NotYet are the midday pair; Drop is the evening's "this is not happening" so unfinished work is
// never silently abandoned OR silently carried forever.
internal sealed record UserCheckInTaskOutcome(long TaskId, string Action)
{
    public const string Done = "done";
    public const string NotYet = "not_yet";
    public const string Drop = "drop";

    // Complaint 3 - the close-out card's "Tomorrow". Like NotYet it writes NOTHING to the task: an
    // open task carries by simply staying open, so deferring cannot lose work. It is a separate
    // value only so the INTENT is distinguishable from "still today" in the log and in the copy.
    //
    // NOTE: that intent is not yet persisted anywhere, so the evening card cannot currently see it.
    // Giving "Tomorrow" a durable meaning needs a storage decision (a task column, or the existing
    // plan_tomorrow) and that is Yadin's call, not an implementation detail to invent.
    public const string Defer = "defer";
}

// The user's answer. TextReply carries the morning goal or the EOD plan; Choice carries the
// morning tracking preference (remind_me|keep_on_track|just_track) or the midday answer
// (yes|partly|not_yet). Dismissed = closed without answering.
// Step 3 — Tasks carries the morning list as real task entries. TextReply still carries the same
// list joined into one sentence, so every existing consumer of the morning goal keeps working and
// a card that somehow raises no Tasks degrades to exactly the old single-goal behaviour.
internal sealed record UserCheckInResponse(
    UserCheckInKind Kind,
    string? TextReply,
    string? Choice,
    bool Dismissed,
    IReadOnlyList<UserCheckInTaskEntry>? Tasks = null,
    // Steps 5 + 7 — per-task outcomes from the midday and evening cards. Null on kinds that do not
    // collect them, and on any card built before this existed.
    IReadOnlyList<UserCheckInTaskOutcome>? TaskOutcomes = null)
{
    public static UserCheckInResponse DismissedFor(UserCheckInKind kind)
        => new(kind, TextReply: null, Choice: null, Dismissed: true);
}

// The morning tracking-preference chips and the midday answer chips — the locked copy/values.
internal static class UserCheckInChoices
{
    public static readonly (string Value, string Label)[] MorningTracking =
    {
        ("remind_me", "Remind me"),
        ("keep_on_track", "Keep me on track"),
        ("just_track", "Just track"),
    };

    public static readonly (string Value, string Label)[] MiddayAnswers =
    {
        ("yes", "Yes"),
        ("partly", "Partly"),
        ("not_yet", "Not yet"),
    };

    // Step 3 — Ari proposes the duration and the user taps it. The first entry is the default, so a
    // morning card answered without touching these still produces a valid, honest planned duration.
    // A typed number is deliberately impossible here: the morning card is the three seconds the
    // whole product depends on, and a numeric keypad in it spends them.
    public static readonly (int Seconds, string Label)[] MorningTaskDurations =
    {
        (3600, "1h"),
        (7200, "2h"),
        (10800, "3h"),
    };
}
