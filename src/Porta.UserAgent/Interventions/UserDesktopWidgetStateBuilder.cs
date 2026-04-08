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
        _ = signals;
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        IReadOnlyList<LocalApiEventResponse> events = recentEvents ?? [];
        LocalTodaySummaryMetrics todaySummary = LocalTodaySummaryMetricsBuilder.Build(timeline);
        LocalCurrentExperienceState experience = LocalCurrentExperienceStateAdapter.Build(
            status,
            snapshot,
            liveSession,
            events,
            now);
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
                : experience.Mode switch
                {
                    LocalCurrentExperienceMode.DeepWork => UserDesktopWidgetMode.DeepWork,
                    LocalCurrentExperienceMode.Overload => UserDesktopWidgetMode.Overload,
                    _ => UserDesktopWidgetMode.Monitoring
                };

        // Live session state is only for immediate "what is happening now" activity facts.
        // The visible duration comes from the durable focus episode and should not
        // bounce with foreground-app changes.

        return new UserDesktopWidgetState(
            Mode: mode,
            Title: BuildTitle(mode, status),
            StatusText: BuildStatusText(status, snapshot, liveSession, degradedToast, criticalState, mode, statusHttpCode),
            MessageSeverity: BuildMessageSeverity(mode, criticalState),
            FooterText: BuildFooterText(status, snapshot, experience, mode),
            PrimaryMetric: BuildWorkTodayMetric(status, todaySummary, mode),
            SecondaryMetric: BuildLastFocusMetric(status, liveSession, experience, mode),
            TertiaryMetric: BuildOverloadMetric(snapshot, mode),
            CanPause: status?.ServiceRunning == true && status.ConsentActive && !paused,
            CanResume: paused,
            CriticalState: criticalState);
    }

    private static string BuildTitle(UserDesktopWidgetMode mode, LocalApiStatusResponse? status)
    {
        return mode switch
        {
            UserDesktopWidgetMode.Paused => "Paused",
            UserDesktopWidgetMode.Limited => IsUnavailableState(status) ? "Unavailable" : "Tracking limited",
            _ => "Today"
        };
    }

    private static UserDesktopWidgetMessageSeverity BuildMessageSeverity(
        UserDesktopWidgetMode mode,
        UserDesktopWidgetCriticalState? criticalState)
    {
        if (criticalState != null)
        {
            return UserDesktopWidgetMessageSeverity.Critical;
        }

        return mode switch
        {
            UserDesktopWidgetMode.DeepWork => UserDesktopWidgetMessageSeverity.Info,
            UserDesktopWidgetMode.Overload => UserDesktopWidgetMessageSeverity.Warning,
            UserDesktopWidgetMode.Limited => UserDesktopWidgetMessageSeverity.Warning,
            _ => UserDesktopWidgetMessageSeverity.Neutral
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
            _ => "Today is updating normally."
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

    private static UserDesktopWidgetMetric BuildWorkTodayMetric(
        LocalApiStatusResponse? status,
        LocalTodaySummaryMetrics todaySummary,
        UserDesktopWidgetMode mode)
    {
        string value = mode switch
        {
            UserDesktopWidgetMode.Paused => "Paused",
            _ when todaySummary.HasProcessedTimeline => FormatCompactDuration(TimeSpan.FromSeconds(Math.Max(0d, todaySummary.TrackedTodaySeconds))),
            _ when status == null || !status.ServiceRunning || !status.LocalApiAuthReady => "n/a",
            _ => "Filling in"
        };

        return new UserDesktopWidgetMetric("Work today", value);
    }

    private static UserDesktopWidgetMetric BuildLastFocusMetric(
        LocalApiStatusResponse? status,
        LocalApiLiveSessionStateResponse? liveSession,
        LocalCurrentExperienceState experience,
        UserDesktopWidgetMode mode)
    {
        string value;
        if (mode == UserDesktopWidgetMode.Paused)
        {
            value = "Paused";
        }
        else if (experience.CurrentFocusSessionSeconds.HasValue)
        {
            value = FormatCompactDuration(TimeSpan.FromSeconds(experience.CurrentFocusSessionSeconds.Value));
        }
        else if (experience.LiveSessionFresh && liveSession != null)
        {
            value = BuildLivePresenceMetric(liveSession);
        }
        else if (status == null || !status.ServiceRunning || !status.LocalApiAuthReady)
        {
            value = "n/a";
        }
        else
        {
            value = "Filling in";
        }

        return new UserDesktopWidgetMetric("Last focus session", value);
    }

    private static UserDesktopWidgetMetric BuildOverloadMetric(
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        string value;
        if (mode == UserDesktopWidgetMode.Paused)
        {
            value = "Paused";
        }
        else if (snapshot is { analysis_ready: false })
        {
            value = "Pending";
        }
        else
        {
            double? overload = snapshot?.OverloadLikelihood;
            value = overload.HasValue
                ? FormatPercent(overload.Value)
                : "n/a";
        }

        return new UserDesktopWidgetMetric("Overload", value);
    }

    private static string BuildFooterText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalCurrentExperienceState experience,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return string.Empty;
        }

        string confidenceText = BuildConfidenceFooterText(experience);
        string trackingQualityText = BuildTrackingQualityFooterText(status, snapshot, experience);

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
            ? (liveSession.user_active ? "Active now" : "Watching live")
            : "Focused now";
        string typingText = liveSession.keyboard_active ? "typing active" : "typing idle";
        string mouseText = liveSession.mouse_active ? "mouse active" : "mouse idle";
        return $"{focusText} | {typingText} | {mouseText}.";
    }

    private static string BuildLivePresenceMetric(LocalApiLiveSessionStateResponse liveSession)
    {
        if (liveSession.idle)
        {
            return "Idle";
        }

        if (!string.IsNullOrWhiteSpace(liveSession.focus_app_hash))
        {
            return "Active";
        }

        return liveSession.user_active ? "Active" : "Watching";
    }

    private static string BuildConfidenceFooterText(LocalCurrentExperienceState experience)
    {
        if (!experience.SnapshotAvailable)
        {
            return "Confidence unavailable";
        }

        return $"Confidence {experience.ConfidenceLabel}";
    }

    private static string BuildTrackingQualityFooterText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalCurrentExperienceState experience)
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

        if (!experience.SnapshotAvailable || snapshot == null)
        {
            return experience.LiveSessionFresh
                ? "Tracking quality filling in"
                : "Tracking quality unavailable";
        }

        if (experience.PartialData)
        {
            return "Tracking quality limited";
        }

        if (!experience.SnapshotAnalysisReady)
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

    private static bool IsUnavailableState(LocalApiStatusResponse? status) =>
        status == null || !status.ServiceRunning || !status.LocalApiAuthReady;

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
