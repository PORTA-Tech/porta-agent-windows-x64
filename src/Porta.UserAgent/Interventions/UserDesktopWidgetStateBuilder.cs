namespace Porta.UserAgent.Interventions;

using System.Globalization;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

internal static class UserDesktopWidgetStateBuilder
{
    public static UserDesktopWidgetState Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserAgentVisualSignals signals,
        UserCommunicationToast? degradedToast)
    {
        bool paused = status?.Paused == true;
        bool limited = paused
            ? false
            : degradedToast != null
            || status == null
            || snapshot == null
            || !status.ServiceRunning
            || !status.ConsentActive
            || !string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase);

        UserDesktopWidgetMode mode = paused
            ? UserDesktopWidgetMode.Paused
            : limited
                ? UserDesktopWidgetMode.Limited
                : signals.Mode switch
                {
                    UserAgentVisualMode.DeepWork => UserDesktopWidgetMode.DeepWork,
                    UserAgentVisualMode.Overload => UserDesktopWidgetMode.Overload,
                    _ => UserDesktopWidgetMode.Monitoring
                };

        return new UserDesktopWidgetState(
            Mode: mode,
            Title: BuildTitle(mode),
            StatusText: BuildStatusText(status, snapshot, signals, degradedToast, mode),
            PrimaryMetricText: BuildPrimaryMetric(status, snapshot, mode),
            SecondaryMetricText: BuildSecondaryMetric(status, snapshot, mode),
            FooterText: BuildFooterText(status, snapshot),
            NotificationsSuppressed: status?.NotificationsSuppressed == true || snapshot?.NotificationsSuppressed == true,
            BrightnessAdjusted: status?.BrightnessAdjusted == true || snapshot?.BrightnessAdjusted == true,
            CanPause: status?.ServiceRunning == true && status.ConsentActive && !paused,
            CanResume: paused);
    }

    private static string BuildTitle(UserDesktopWidgetMode mode)
    {
        return mode switch
        {
            UserDesktopWidgetMode.DeepWork => "Deep Work",
            UserDesktopWidgetMode.Overload => "Overload",
            UserDesktopWidgetMode.Limited => "Ari Limited",
            UserDesktopWidgetMode.Paused => "Paused",
            _ => "Monitoring"
        };
    }

    private static string BuildStatusText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserAgentVisualSignals signals,
        UserCommunicationToast? degradedToast,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            if (status?.PauseUntilUtc is DateTimeOffset untilUtc)
            {
                return $"Ari is paused until {untilUtc.ToLocalTime():h:mm tt}.";
            }

            return "Ari is paused.";
        }

        if (degradedToast != null)
        {
            return degradedToast.Message;
        }

        if (status == null || snapshot == null)
        {
            return "Waiting for the core service.";
        }

        if (!status.ServiceRunning)
        {
            return "Ari cannot regulate because the core service is unavailable.";
        }

        if (!status.ConsentActive)
        {
            return "Ari is waiting for consent before it can guide this session.";
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is reconnecting to your live Windows session.";
        }

        if (mode == UserDesktopWidgetMode.DeepWork)
        {
            if (signals.ShowDeepWorkStreak && !string.IsNullOrWhiteSpace(signals.DeepWorkStreakText))
            {
                return $"{signals.DeepWorkStreakText} protected.";
            }

            return "Deep work is active.";
        }

        if (mode == UserDesktopWidgetMode.Overload)
        {
            return "Ari is reducing interruptions while overload stays elevated.";
        }

        string tooltipText = StripTooltipPrefix(signals.TooltipText);
        if (!string.IsNullOrWhiteSpace(tooltipText)
            && !tooltipText.Equals("Watching activity", StringComparison.OrdinalIgnoreCase)
            && !tooltipText.Equals("Waiting for service data", StringComparison.OrdinalIgnoreCase))
        {
            return tooltipText + ".";
        }

        return "Ari is monitoring your session.";
    }

    private static string BuildPrimaryMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Tracking paused";
        }

        if (snapshot != null)
        {
            if (mode == UserDesktopWidgetMode.Overload || snapshot.OverloadLikelihood >= 0.50d)
            {
                return $"Overload {FormatPercent(snapshot.OverloadLikelihood)}";
            }

            double focusMinutes = Math.Max(0d, snapshot.SustainedAttentionDurationSec) / 60d;
            if (focusMinutes >= 1d)
            {
                return $"Focus {Math.Round(focusMinutes, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}m";
            }
        }

        if (status != null && !string.IsNullOrWhiteSpace(status.TrustTier))
        {
            return $"Trust {FormatTrustTier(status.TrustTier)}";
        }

        return "Ari live";
    }

    private static string BuildSecondaryMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return status?.ServiceRunning == true ? "Resume anytime" : "Waiting for service";
        }

        if (snapshot != null)
        {
            return $"Confidence {FormatPercent(snapshot.ModelConfidence ?? snapshot.Confidence)}";
        }

        if (status != null)
        {
            return $"Trust {FormatTrustTier(status.TrustTier)}";
        }

        return "No live data";
    }

    private static string BuildFooterText(LocalApiStatusResponse? status, LocalApiCurrentSnapshotResponse? snapshot)
    {
        bool notificationsSuppressed = status?.NotificationsSuppressed == true || snapshot?.NotificationsSuppressed == true;
        bool brightnessAdjusted = status?.BrightnessAdjusted == true || snapshot?.BrightnessAdjusted == true;

        if (snapshot?.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            return $"Missing {string.Join("/", snapshot.MissingMajorChannels)}";
        }

        if (notificationsSuppressed && brightnessAdjusted)
        {
            return "Notifications muted and brightness reduced";
        }

        if (notificationsSuppressed)
        {
            return "Notifications muted";
        }

        if (brightnessAdjusted)
        {
            return "Brightness reduced";
        }

        if (!string.IsNullOrWhiteSpace(status?.CalibrationStage)
            && !string.Equals(status.CalibrationStage, CalibrationTruth.StageCalibrated, StringComparison.OrdinalIgnoreCase))
        {
            return $"Calibration {FormatTrustTier(status.CalibrationStage)}";
        }

        return string.Empty;
    }

    private static string FormatPercent(double value)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;
        return $"{Math.Round(normalized * 100d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";
    }

    private static string FormatTrustTier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "deep_work" => "Deep Work",
            "user_session" => "User Session",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.Replace('_', ' '))
        };
    }

    private static string StripTooltipPrefix(string? tooltipText)
    {
        if (string.IsNullOrWhiteSpace(tooltipText))
        {
            return string.Empty;
        }

        string trimmed = tooltipText.Trim();
        const string prefix = "PORTA | ";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        return trimmed.Trim().TrimEnd('.');
    }
}
