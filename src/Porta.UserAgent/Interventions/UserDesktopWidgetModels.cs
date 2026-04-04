namespace Porta.UserAgent.Interventions;

internal enum UserDesktopWidgetMode
{
    Monitoring,
    DeepWork,
    Overload,
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
    UserDesktopWidgetCriticalState? CriticalState = null);
