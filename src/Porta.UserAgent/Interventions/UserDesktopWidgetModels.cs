namespace Porta.UserAgent.Interventions;

internal enum UserDesktopWidgetMode
{
    Monitoring,
    DeepWork,
    Overload,
    Distraction,
    Limited,
    Paused,
    // Phase 2b — attentive-in-meeting: meeting app foreground, measured (not away).
    InMeeting
}

internal enum UserDesktopWidgetAction
{
    OpenNow,
    OpenHistory,
    OpenActions,
    OpenTracking,
    OpenHealth,
    OpenSettings,
    // Phase 4.1 (2026-08-18) — the widget menu lists the pages that exist. Actions and Tracking
    // became sub-sections of Settings in phase 3.1, so the menu offers the four main pages
    // instead. The two old actions stay in this enum: an installed Control Center still answers
    // them, and dropping them would break a widget/Control Center straddle mid-upgrade.
    OpenTasks,
    OpenProjects,
    OpenReports,
    HideWidget,
    // Phase 4.2 (2026-08-18) — timed hides. "Until tomorrow" was the only option, so a
    // ten-minute interruption cost the rest of the day. Both are still bounded: nothing the
    // widget can do makes it permanently invisible — that stays a Control Center setting.
    HideOneHour,
    HideTwoHours,
    RetryCritical,
    PauseFifteenMinutes,
    PauseOneHour,
    Resume,
    Recalibrate,
    ResetPosition,
    LabelAnswerPositive,
    LabelAnswerNegative,
    LabelDismiss,
    // Phase 1c — signup nudge: open the Control Center at the sign-in screen, or dismiss the nudge.
    OpenAccount,
    SignupNudgeAccept,
    SignupNudgeDismiss,
    // Phase 2c — reminders: act on a fired reminder, or open the quick-add input
    // (window-local; never reaches the presenter).
    ReminderDone,
    ReminderSnooze,
    ReminderOpenQuickAdd,
    // Complaints 4 + 5 — the estimate-expiry ask, answered where it appears. The old expiry toast
    // said "…from the widget", which left the trip to the widget — the exact act that was not
    // happening — still to be made.
    TaskAskDone,
    TaskAskExtend,
    TaskAskSwitched
}

internal enum UserDesktopWidgetMessageSeverity
{
    Neutral,
    Info,
    Warning,
    Critical
}

internal sealed record UserDesktopWidgetMetric(string Label, string Value);

internal sealed record UserDesktopWidgetCriticalState(
    string Title,
    string Message,
    UserDesktopWidgetAction PrimaryAction,
    string PrimaryLabel,
    UserDesktopWidgetAction SecondaryAction,
    string SecondaryLabel,
    UserDesktopWidgetAction? TertiaryAction = null,
    string TertiaryLabel = "");

internal sealed record UserDesktopWidgetState(
    UserDesktopWidgetMode Mode,
    string Title,
    string StatusText,
    UserDesktopWidgetMessageSeverity MessageSeverity,
    string FooterText,
    UserDesktopWidgetMetric PrimaryMetric,
    UserDesktopWidgetMetric SecondaryMetric,
    UserDesktopWidgetMetric TertiaryMetric,
    bool CanPause,
    bool CanResume,
    UserDesktopWidgetCriticalState? CriticalState = null,
    // True when tracking is degraded in a routine way (partial capture, stale analysis,
    // session reconnecting). Does NOT change the mode — just shows a small badge.
    // Reserved false for genuine unavailability or action-required states.
    bool ShowLimitedBadge = false,
    // Phase 1b — Step 3c: ground-truth label prompt to render as a one-tap pill (null = none).
    UserDesktopWidgetLabelPrompt? LabelPrompt = null,
    // Phase 1c: signup nudge to render as a two-button toast (null = none).
    UserDesktopWidgetSignupNudge? SignupNudge = null,
    // Phase 2a — pulse line: 0..1 work intensity (spike HEIGHT is the primary channel; beat
    // rate secondary), blended from typing/scroll/switch signals + live input activity.
    double PulseIntensity = 0,
    // Phase 2a — true when the line must freeze entirely (paused / limited / stale).
    // A dead line means "not watching"; the pulse never fakes life.
    bool PulseFrozen = false,
    // Phase 2c — the next reminder to surface (pill at T-60, toast when fired). Null = none.
    UserDesktopWidgetReminderInfo? NextReminder = null,
    // Workstream 4 — today's timed tasks (plan vs. reality). Null/empty = section hidden.
    IReadOnlyList<UserDesktopWidgetTaskInfo>? Tasks = null,
    // Complaints 4 + 5 — the estimate has run out on the active task and Ari is asking about it,
    // with the answer on the same pill. Null = nothing to ask.
    UserDesktopWidgetTaskAsk? TaskAsk = null,
    // Step 9a — the projects the "+ task" compose offers, most-recently-used first. Null/empty =
    // the compose offers only "+ new". Deliberately NOT used to draw anything on a task ROW: the
    // title has 96 DIPs and a "Nolan · " prefix would eat 40% of the thing the user needs to read
    // (Yadin, 2026-08-12). The widget is about what is being worked on, not what it bills to.
    IReadOnlyList<UserDesktopWidgetProject>? Projects = null);

// Phase 2c — the next reminder as the widget renders it. State: pending | fired.
internal sealed record UserDesktopWidgetReminderInfo(
    long Id,
    string Title,
    DateTimeOffset DueAtUtc,
    string State,
    int SnoozeCount);

// Phase 2c — a reminder command flowing widget → presenter → local API.
// Kind: create (Title + DueAtUtc used) | done | snooze (Id used).
internal sealed record UserDesktopWidgetReminderCommand(
    string Kind,
    long Id,
    string Title,
    DateTimeOffset DueAtUtc);

// Workstream 4 — a task as the widget renders it. State: pending | active | done | abandoned.
internal sealed record UserDesktopWidgetTaskInfo(
    long Id,
    string Title,
    int PlannedSeconds,
    string State,
    double ActiveSeconds,
    double FocusedSeconds,
    double OfftaskSeconds,
    int ExtendedSeconds,
    // Chat 7 (2026-08-24) — which project this belongs to, shown quietly on the row. Empty =
    // untagged, which shows nothing at all rather than the word "untagged": on a 200 DIP row that
    // would be a label for a non-event on most rows.
    //
    // It already rides along on LocalApiTask for exactly this ("the name rides along so the widget,
    // repainting every 4s, never needs a second call") and was simply never read. Defaulted so
    // every existing caller is unaffected.
    string ProjectName = "");

// Workstream 4 — a task command flowing widget → presenter → local API.
// Kind: create (Title + PlannedSeconds used) | start | done | abandon | extend (Id used).
internal sealed record UserDesktopWidgetTaskCommand(
    string Kind,
    long Id,
    string Title,
    int PlannedSeconds,
    // Step 9a — the project picked in the compose. ProjectId names one that exists; ProjectName
    // carries one typed into "+ new", resolved by the presenter through create-or-get. Both empty
    // = untagged, which is the default and never nagged about.
    long ProjectId = 0,
    string ProjectName = "");

// Step 9a — one project as the widget's picker sees it. On a compose a project is only a name.
internal sealed record UserDesktopWidgetProject(long Id, string Name);

// Phase 1b — Step 3c: one-tap ground-truth prompt shown in the widget pill.
internal sealed record UserDesktopWidgetLabelPrompt(
    string PromptId,        // unique per prompt instance (suppresses re-show after resolve)
    string Target,          // focus_state | distraction | overload
    string PromptKind,      // focused_now | on_task_now | overload_now
    string Question,        // e.g. "Were you focused just now?"
    string PositiveLabel,   // e.g. "Yes" / "Working"
    string PositiveAnswer,  // e.g. "yes" / "working"
    string NegativeLabel,   // e.g. "No" / "Break"
    string NegativeAnswer); // e.g. "no" / "break"

// Phase 1b — Step 3c: a resolved one-tap answer routed from the widget to the label endpoint.
internal sealed record UserDesktopWidgetLabelAnswer(
    string Target,
    string PromptKind,
    string Answer,
    bool IsDismiss);

/// <summary>
/// Complaints 4 + 5 — the estimate-expiry ask, shown as a three-button pill on the widget.
///
/// <para><b>AskId</b> is the expiry key, "<c>taskId:budgetSeconds</c>", so it changes on every +15
/// and the pill can therefore ask again about the same task on a new estimate — while a resolved
/// ask stays resolved until the estimate actually moves.</para>
///
/// <para>Answering never reassigns measured time. "Switched" opens the task box so the user can say
/// what he is on now; whatever was already credited stays on the task it was measured against, and
/// only the future changes (Yadin, 2026-08-12).</para>
/// </summary>
internal sealed record UserDesktopWidgetTaskAsk(
    string AskId,      // "{taskId}:{plannedSeconds+extendedSeconds}"
    long TaskId,
    string TaskTitle,
    int PlannedSeconds,
    string Body);      // "Pricing page has had the 2h you gave it."

// Phase 1c: signup nudge shown as a two-button toast in the widget ("Sign in" / "Not now").
internal sealed record UserDesktopWidgetSignupNudge(
    string NudgeId,         // unique per nudge instance (suppresses re-show after resolve)
    string Body,            // "You've tracked 2 days of focus. Sign in to keep it. Takes 30 seconds."
    string PrimaryLabel,    // "Sign in"
    string SecondaryLabel); // "Not now"
