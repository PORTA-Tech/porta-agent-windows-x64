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
    HideWidget,
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
    ReminderOpenQuickAdd
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
    IReadOnlyList<UserDesktopWidgetTaskInfo>? Tasks = null);

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
    int ExtendedSeconds);

// Workstream 4 — a task command flowing widget → presenter → local API.
// Kind: create (Title + PlannedSeconds used) | start | done | abandon | extend (Id used).
internal sealed record UserDesktopWidgetTaskCommand(
    string Kind,
    long Id,
    string Title,
    int PlannedSeconds);

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

// Phase 1c: signup nudge shown as a two-button toast in the widget ("Sign in" / "Not now").
internal sealed record UserDesktopWidgetSignupNudge(
    string NudgeId,         // unique per nudge instance (suppresses re-show after resolve)
    string Body,            // "You've tracked 2 days of focus. Sign in to keep it. Takes 30 seconds."
    string PrimaryLabel,    // "Sign in"
    string SecondaryLabel); // "Not now"
