namespace Porta.UserAgent.Interventions;

using System.Globalization;
using Porta.Agent.LocalApi;

internal static class UserDesktopWidgetStateBuilder
{
    public static UserDesktopWidgetState Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserAgentVisualSignals signals,
        UserCommunicationToast? degradedToast,
        IReadOnlyList<LocalApiEventResponse>? recentEvents = null,
        LocalApiTimelineResponse? timeline = null,
        LocalApiSettingsResponse? settings = null,
        DateTimeOffset? nowUtc = null)
    {
        _ = settings;
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        IReadOnlyList<LocalApiEventResponse> events = recentEvents ?? [];
        UserDesktopWidgetCriticalState? criticalState = UserCommunicationRouter.ResolveCriticalWidgetState(
            status,
            snapshot,
            events,
            now);
        criticalState ??= BuildFallbackCriticalState(degradedToast);

        bool paused = status?.Paused == true;
        bool limited = paused
            ? false
            : criticalState != null
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
            StatusText: BuildStatusText(status, degradedToast, criticalState, mode),
            PrimaryMetricText: BuildFocusMetric(status, snapshot, mode),
            SecondaryMetricText: BuildOverloadMetric(status, snapshot, mode),
            FooterText: string.Empty,
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            CanPause: status?.ServiceRunning == true && status.ConsentActive && !paused,
            CanResume: paused,
            TertiaryMetricText: BuildComputerTimeMetric(snapshot, timeline, mode),
            QuaternaryMetricText: string.Empty,
            CriticalState: criticalState);
    }

    private static string BuildTitle(UserDesktopWidgetMode mode)
    {
        return mode switch
        {
            UserDesktopWidgetMode.Paused => "Ari Paused",
            UserDesktopWidgetMode.Limited => "Ari Limited",
            _ => "Ari"
        };
    }

    private static string BuildStatusText(
        LocalApiStatusResponse? status,
        UserCommunicationToast? degradedToast,
        UserDesktopWidgetCriticalState? criticalState,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            if (status?.PauseUntilUtc is DateTimeOffset untilUtc)
            {
                return $"Paused until {untilUtc.ToLocalTime():h:mm tt}.";
            }

            return "Paused.";
        }

        if (degradedToast != null)
        {
            return degradedToast.Message;
        }

        if (criticalState != null)
        {
            return criticalState.Message;
        }

        if (status == null)
        {
            return "Waiting for the core service.";
        }

        if (!status.ServiceRunning)
        {
            return "Ari cannot guide this session because the core service is unavailable.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "Ari is waiting for local API access.";
        }

        if (!status.ConsentActive)
        {
            return "Ari is waiting for consent.";
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is reconnecting to your live Windows session.";
        }

        if (!status.BackendConnected)
        {
            return "Ari is running locally while backend sync reconnects.";
        }

        return mode switch
        {
            UserDesktopWidgetMode.DeepWork => "Deep work is active.",
            UserDesktopWidgetMode.Overload => "Overload is elevated.",
            _ => string.Empty
        };
    }

    private static UserDesktopWidgetCriticalState? BuildFallbackCriticalState(UserCommunicationToast? degradedToast)
    {
        if (degradedToast == null)
        {
            return null;
        }

        return new UserDesktopWidgetCriticalState(
            Title: degradedToast.Title,
            Message: degradedToast.Message,
            PrimaryAction: UserDesktopWidgetAction.OpenHealth,
            PrimaryLabel: "Open Health",
            SecondaryAction: UserDesktopWidgetAction.RetryCritical,
            SecondaryLabel: "Retry",
            TertiaryAction: UserDesktopWidgetAction.OpenSettings,
            TertiaryLabel: "Settings");
    }

    private static string BuildFocusMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Focus paused";
        }

        if (snapshot != null)
        {
            return $"Focus {FormatCompactDuration(TimeSpan.FromSeconds(Math.Max(0d, snapshot.SustainedAttentionDurationSec)))}";
        }

        if (status?.DeepWorkStreakSeconds > 0)
        {
            return $"Focus {FormatCompactDuration(TimeSpan.FromSeconds(status.DeepWorkStreakSeconds))}";
        }

        return "Focus n/a";
    }

    private static string BuildOverloadMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Overload paused";
        }

        double? overload = snapshot?.OverloadLikelihood ?? status?.OverloadLikelihood;
        return overload.HasValue
            ? $"Overload {FormatPercent(overload.Value)}"
            : "Overload n/a";
    }

    private static string BuildComputerTimeMetric(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiTimelineResponse? timeline,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Today paused";
        }

        if (timeline?.Snapshots is { Count: > 0 } snapshots)
        {
            int bucketSeconds = Math.Max(1, timeline.BucketSeconds);
            TimeSpan activeDuration = TimeSpan.FromSeconds(snapshots.Count * bucketSeconds);
            if (activeDuration > TimeSpan.Zero)
            {
                return $"Today {FormatCompactDuration(activeDuration)}";
            }
        }

        if (snapshot != null)
        {
            return $"Today {FormatCompactDuration(TimeSpan.FromSeconds(Math.Max(0d, snapshot.SustainedAttentionDurationSec)))}";
        }

        return "Today n/a";
    }

    private static string FormatPercent(double value)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;
        return $"{Math.Round(normalized * 100d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";
    }

    private static string FormatCompactDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0m";
        }

        if (duration.TotalMinutes < 1d)
        {
            int seconds = Math.Max(0, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero));
            return $"{seconds.ToString(CultureInfo.InvariantCulture)}s";
        }

        if (duration.TotalHours < 1d)
        {
            int minutes = Math.Max(0, (int)Math.Floor(duration.TotalMinutes));
            return $"{minutes.ToString(CultureInfo.InvariantCulture)}m";
        }

        int hours = Math.Max(0, (int)Math.Floor(duration.TotalHours));
        return $"{hours.ToString(CultureInfo.InvariantCulture)}h {duration.Minutes.ToString("00", CultureInfo.InvariantCulture)}m";
    }
}
