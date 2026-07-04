namespace Porta.UserAgent.Interventions;

internal enum UserDesktopWidgetMode
{
    Monitoring,
    DeepWork,
    Overload,
    Distraction,
    Limited,
    Paused
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
    ResetPosition
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
    UserDesktopWidgetLabelPrompt? LabelPrompt = null);

// Phase 1b — Step 3c: one-tap ground-truth prompt shown in the widget pill.
internal sealed record UserDesktopWidgetLabelPrompt(
    string Target,          // focus_state | distraction | overload
    string PromptKind,      // focused_now | on_task_now | overload_now
    string Question,        // e.g. "Were you focused just now?"
    string PositiveLabel,   // e.g. "Yes" / "Working"
    string PositiveAnswer,  // e.g. "yes" / "working"
    string NegativeLabel,   // e.g. "No" / "Break"
    string NegativeAnswer); // e.g. "no" / "break"
