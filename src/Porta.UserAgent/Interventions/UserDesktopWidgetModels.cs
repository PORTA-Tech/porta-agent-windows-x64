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
    PauseFifteenMinutes,
    PauseOneHour,
    Resume,
    Recalibrate,
    ResetPosition
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
    bool CanResume,
    string TertiaryMetricText = "",
    string QuaternaryMetricText = "");
