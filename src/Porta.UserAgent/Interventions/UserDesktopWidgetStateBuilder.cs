namespace Porta.UserAgent.Interventions;

using System.Globalization;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

internal static class UserDesktopWidgetStateBuilder
{
    public static UserDesktopWidgetState Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        UserAgentVisualSignals signals,
        UserCommunicationToast? degradedToast,
        IReadOnlyList<LocalApiEventResponse>? recentEvents = null,
        LocalApiTimelineResponse? timeline = null,
        LocalApiSettingsResponse? settings = null,
        int? statusHttpCode = null,
        DateTimeOffset? nowUtc = null)
    {
        _ = settings;
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        IReadOnlyList<LocalApiEventResponse> events = recentEvents ?? [];
        UserDesktopWidgetCriticalState? criticalState = UserCommunicationRouter.ResolveCriticalWidgetState(
            status,
            snapshot,
            events,
            now,
            statusHttpCode);
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

        // Live session state is only for immediate "what is happening now" activity facts.
        // Analyzed state such as overload stays on status/snapshot/timeline/event sources.

        return new UserDesktopWidgetState(
            Mode: mode,
            Title: BuildTitle(mode, status),
            StatusText: BuildStatusText(status, snapshot, liveSession, degradedToast, criticalState, mode, statusHttpCode),
            PrimaryMetricText: BuildFocusMetric(status, snapshot, liveSession, mode),
            SecondaryMetricText: BuildOverloadMetric(status, snapshot, mode),
            FooterText: BuildFooterText(status, snapshot, liveSession, mode),
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            CanPause: status?.ServiceRunning == true && status.ConsentActive && !paused,
            CanResume: paused,
            TertiaryMetricText: BuildComputerTimeMetric(snapshot, timeline, liveSession, mode),
            QuaternaryMetricText: string.Empty,
            CriticalState: criticalState);
    }

    private static string BuildTitle(UserDesktopWidgetMode mode, LocalApiStatusResponse? status)
    {
        return mode switch
        {
            UserDesktopWidgetMode.Paused => "Paused",
            UserDesktopWidgetMode.Limited => IsUnavailableState(status) ? "Unavailable" : "Tracking limited",
            _ => string.Empty
        };
    }

    private static string BuildStatusText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        UserCommunicationToast? degradedToast,
        UserDesktopWidgetCriticalState? criticalState,
        UserDesktopWidgetMode mode,
        int? statusHttpCode)
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
            return statusHttpCode switch
            {
                401 or 403 => "Current status is unavailable until System access reloads.",
                int _ => "Current status is still loading.",
                _ => "Current status is unavailable."
            };
        }

        if (!status.ServiceRunning)
        {
            return "Current status is unavailable while the service reconnects.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "Current status is unavailable until System access reloads.";
        }

        if (!IsFresh(status.freshness_state))
        {
            return BuildFreshnessStatusText(status, snapshot);
        }

        if (snapshot != null && !IsFresh(snapshot.freshness_state))
        {
            return BuildFreshnessStatusText(status, snapshot);
        }

        if (!status.ConsentActive)
        {
            return "Tracking is waiting for consent.";
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return "Tracking quality is limited while Ari reconnects to your desktop session.";
        }

        if (!status.BackendConnected)
        {
            return "Current status is local while backend sync reconnects.";
        }

        if (IsFreshLiveSession(liveSession))
        {
            return BuildLiveSessionStatusText(liveSession!);
        }

        return mode switch
        {
            UserDesktopWidgetMode.DeepWork => "Deep work is active.",
            UserDesktopWidgetMode.Overload => "Overload is elevated.",
            _ => string.Empty
        };
    }

    private static string BuildFreshnessStatusText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot)
    {
        bool dbBusy = string.Equals(status?.runtime_state_refresh_error, "db_busy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot?.runtime_state_refresh_error, "db_busy", StringComparison.OrdinalIgnoreCase);
        return snapshot != null
            ? (dbBusy
                ? "Current status is using a cached snapshot while the local database catches up."
                : "Current status is using a cached snapshot while fresh analysis catches up.")
            : (dbBusy
                ? "Current status is delayed while the local database catches up."
                : "Current status is delayed right now.");
    }

    private static bool IsFresh(string? freshnessState)
    {
        return string.Equals(freshnessState, "fresh", StringComparison.OrdinalIgnoreCase);
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
            PrimaryLabel: "Open System",
            SecondaryAction: UserDesktopWidgetAction.RetryCritical,
            SecondaryLabel: "Retry",
            TertiaryAction: UserDesktopWidgetAction.OpenSettings,
            TertiaryLabel: "Settings");
    }

    private static string BuildFocusMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Focus session paused";
        }

        if (IsFreshLiveSession(liveSession))
        {
            return BuildLiveFocusMetric(liveSession!);
        }

        if (snapshot is { analysis_ready: true })
        {
            return $"Focus session {FormatCompactDuration(TimeSpan.FromSeconds(Math.Max(0d, snapshot.SustainedAttentionDurationSec)))}";
        }

        if (status?.DeepWorkStreakSeconds > 0)
        {
            return $"Focus session {FormatCompactDuration(TimeSpan.FromSeconds(status.DeepWorkStreakSeconds))}";
        }

        return "Focus session n/a";
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

        if (snapshot is { analysis_ready: false })
        {
            return "Overload pending";
        }

        double? overload = snapshot?.OverloadLikelihood;
        return overload.HasValue
            ? $"Overload {FormatPercent(overload.Value)}"
            : "Overload n/a";
    }

    private static string BuildComputerTimeMetric(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiTimelineResponse? timeline,
        LocalApiLiveSessionStateResponse? liveSession,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Focused today paused";
        }

        if (timeline?.Snapshots is { Count: > 0 } snapshots)
        {
            double focusedTodaySeconds = ComputeFocusedTodaySeconds(timeline);
            if (focusedTodaySeconds > 0d)
            {
                return $"Focused today {FormatCompactDuration(TimeSpan.FromSeconds(focusedTodaySeconds))}";
            }
        }

        if (snapshot != null)
        {
            return $"Focused today {FormatCompactDuration(TimeSpan.FromSeconds(Math.Max(0d, snapshot.SustainedAttentionDurationSec)))}";
        }

        return "Focused today n/a";
    }

    private static string BuildFooterText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return string.Empty;
        }

        string confidenceText = BuildConfidenceFooterText(snapshot);
        string trackingQualityText = BuildTrackingQualityFooterText(status, snapshot, liveSession);

        return string.IsNullOrWhiteSpace(confidenceText)
            ? trackingQualityText
            : $"{confidenceText} | {trackingQualityText}";
    }

    private static bool IsFreshLiveSession(LocalApiLiveSessionStateResponse? liveSession)
    {
        return liveSession != null
            && string.Equals(liveSession.freshness_state, "fresh", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLiveSessionStatusText(LocalApiLiveSessionStateResponse liveSession)
    {
        if (liveSession.idle)
        {
            return $"Idle {FormatAgeShort(liveSession.idle_seconds)}.";
        }

        string focusText = string.IsNullOrWhiteSpace(liveSession.focus_app_hash)
            ? "Focus watching"
            : "Focused now";
        string liveFocusText = liveSession.focus_elapsed_seconds.HasValue
            ? $"live {FormatAgeShort(liveSession.focus_elapsed_seconds.Value)}"
            : "live";
        string typingText = liveSession.keyboard_active ? "typing active" : "typing idle";
        string mouseText = liveSession.mouse_active ? "mouse active" : "mouse idle";
        return $"{focusText} | {liveFocusText} | {typingText} | {mouseText}.";
    }

    private static string BuildLiveFocusMetric(LocalApiLiveSessionStateResponse liveSession)
    {
        if (liveSession.idle)
        {
            return "Focus session idle";
        }

        if (liveSession.focus_elapsed_seconds.HasValue)
        {
            return $"Focus session {FormatCompactDuration(TimeSpan.FromSeconds(Math.Max(0d, liveSession.focus_elapsed_seconds.Value)))}";
        }

        if (!string.IsNullOrWhiteSpace(liveSession.focus_app_hash))
        {
            return "Focus session live";
        }

        return liveSession.user_active ? "Focus session active" : "Focus session watching";
    }

    private static string BuildConfidenceFooterText(LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (snapshot == null)
        {
            return "Confidence unavailable";
        }

        return $"Confidence {ResolveConfidenceLabel(snapshot.TrustTier, snapshot.Confidence)}";
    }

    private static string BuildTrackingQualityFooterText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        if (status?.Paused == true)
        {
            return "Tracking quality paused";
        }

        if (status?.ConsentActive == false)
        {
            return "Tracking quality waiting for consent";
        }

        if (status == null || !status.ServiceRunning || !status.LocalApiAuthReady)
        {
            return "Tracking quality unavailable";
        }

        if (!IsFresh(status.freshness_state) || (snapshot != null && !IsFresh(snapshot.freshness_state)))
        {
            return "Tracking quality delayed";
        }

        if (snapshot == null)
        {
            return IsFreshLiveSession(liveSession)
                ? "Tracking quality filling in"
                : "Tracking quality unavailable";
        }

        if (snapshot.PartialData == true || snapshot.MissingMajorChannels is { Count: > 0 })
        {
            return "Tracking quality limited";
        }

        if (snapshot.analysis_ready == false)
        {
            return "Tracking quality pending";
        }

        double? score = snapshot.CurrentQualityScore;
        if (!score.HasValue)
        {
            return "Tracking quality filling in";
        }

        double normalizedScore = Math.Clamp(score.Value, 0d, 1d);
        if (normalizedScore >= 0.85d)
        {
            return "Tracking quality strong";
        }

        if (normalizedScore >= 0.60d)
        {
            return "Tracking quality usable";
        }

        return "Tracking quality limited";
    }

    private static string ResolveConfidenceLabel(string? trustTier, double confidence)
    {
        string normalizedTrustTier = (trustTier ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedTrustTier == CalibrationTruth.TrustHigh)
        {
            return "High";
        }

        if (normalizedTrustTier == CalibrationTruth.TrustMedium)
        {
            return "Medium";
        }

        if (normalizedTrustTier == CalibrationTruth.TrustLow)
        {
            return "Low";
        }

        if (confidence >= 0.75d)
        {
            return "High";
        }

        return confidence >= 0.50d ? "Medium" : "Low";
    }

    private static bool IsUnavailableState(LocalApiStatusResponse? status) =>
        status == null || !status.ServiceRunning || !status.LocalApiAuthReady;

    private static double ComputeFocusedTodaySeconds(LocalApiTimelineResponse timeline)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline.Snapshots ?? [];
        if (snapshots.Count == 0)
        {
            return 0d;
        }

        double bucketSeconds = Math.Max(1d, timeline.BucketSeconds);
        double focusedTodaySeconds = 0d;
        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            focusedTodaySeconds += bucketSeconds * Math.Clamp(snapshot.FocusStrength, 0d, 1d);
        }

        return focusedTodaySeconds;
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

    private static string FormatAgeShort(double seconds)
    {
        double value = double.IsFinite(seconds) ? Math.Max(0d, seconds) : 0d;
        if (value < 60d)
        {
            return $"{Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}s";
        }

        if (value < 3600d)
        {
            return $"{(value / 60d).ToString("0.#", CultureInfo.InvariantCulture)}m";
        }

        return $"{(value / 3600d).ToString("0.#", CultureInfo.InvariantCulture)}h";
    }
}
