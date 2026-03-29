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
    OpenControlCenter,
    PauseFifteenMinutes,
    PauseOneHour,
    Resume,
    Recalibrate
}

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
    bool CanResume);
