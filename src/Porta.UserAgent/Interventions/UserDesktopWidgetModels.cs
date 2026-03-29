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
    OpenActions,
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
    string PrimaryMetricText,
    string SecondaryMetricText,
    string FooterText,
    bool NotificationsSuppressed,
    bool BrightnessAdjusted,
    bool CanPause,
    bool CanResume,
    string TertiaryMetricText = "",
    string QuaternaryMetricText = "",
    UserDesktopWidgetCriticalState? CriticalState = null);
