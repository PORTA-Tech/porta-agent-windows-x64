namespace Porta.UserAgent.Interventions;

using System.Globalization;
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

        return new UserDesktopWidgetState(
            Mode: mode,
            Title: BuildTitle(mode),
            StatusText: BuildStatusText(status, snapshot, liveSession, degradedToast, criticalState, mode, statusHttpCode),
            PrimaryMetricText: BuildFocusMetric(status, snapshot, liveSession, mode),
            SecondaryMetricText: BuildOverloadMetric(status, snapshot, mode),
            FooterText: BuildFooterText(liveSession, mode),
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            CanPause: status?.ServiceRunning == true && status.ConsentActive && !paused,
            CanResume: paused,
            TertiaryMetricText: BuildComputerTimeMetric(snapshot, timeline, liveSession, mode),
            QuaternaryMetricText: string.Empty,
            CriticalState: criticalState);
    }

    private static string BuildTitle(UserDesktopWidgetMode mode)
    {
        return mode switch
        {
            UserDesktopWidgetMode.Paused => "Paused",
            UserDesktopWidgetMode.Limited => "Attention",
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
                401 or 403 => "API auth mismatch.",
                int _ => "Waiting for service status.",
                _ => "Local API unreachable."
            };
        }

        if (!status.ServiceRunning)
        {
            return "Service offline.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "API auth mismatch.";
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
            ? (dbBusy ? "Cached snapshot. DB busy, retrying." : "Using cached snapshot.")
            : (dbBusy ? "Status stale. DB busy, retrying." : "Status stale.");
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
            PrimaryLabel: "Open Health",
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
            return "Focus paused";
        }

        if (IsFreshLiveSession(liveSession))
        {
            return BuildLiveFocusMetric(liveSession!);
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
        LocalApiLiveSessionStateResponse? liveSession,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Today paused";
        }

        if (IsFreshLiveSession(liveSession) && liveSession!.last_input_age_seconds.HasValue)
        {
            return $"Input {FormatAgeShort(liveSession.last_input_age_seconds.Value)}";
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

    private static string BuildFooterText(LocalApiLiveSessionStateResponse? liveSession, UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused || !IsFreshLiveSession(liveSession))
        {
            return string.Empty;
        }

        string sessionText = liveSession!.session_elapsed_seconds.HasValue
            ? $"Session {FormatCompactDuration(TimeSpan.FromSeconds(liveSession.session_elapsed_seconds.Value))}"
            : "Session n/a";
        string typingText = liveSession.keyboard_active ? "Typing active" : "Typing idle";
        string mouseText = liveSession.mouse_active ? "Mouse active" : "Mouse idle";
        return $"{sessionText} | {typingText} | {mouseText}";
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
        string typingText = liveSession.keyboard_active ? "typing active" : "typing idle";
        string mouseText = liveSession.mouse_active ? "mouse active" : "mouse idle";
        return $"{focusText} | {typingText} | {mouseText}.";
    }

    private static string BuildLiveFocusMetric(LocalApiLiveSessionStateResponse liveSession)
    {
        if (liveSession.idle)
        {
            return $"Idle {FormatAgeShort(liveSession.idle_seconds)}";
        }

        if (!string.IsNullOrWhiteSpace(liveSession.focus_app_hash))
        {
            return "Focus live";
        }

        return liveSession.user_active ? "Focus active" : "Focus watching";
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
