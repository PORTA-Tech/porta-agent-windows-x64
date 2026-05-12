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
        LocalTodaySummaryMetrics todaySummary = LocalTodaySummaryMetricsBuilder.Build(
            timeline,
            snapshot,
            liveSession,
            now);
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

        bool paused = status?.Paused == true;
        bool staleOrPending = HasRoutineUpdatingState(status, snapshot, experience);

        // Action-required: consent gate or a diagnosed critical failure.
        // These are the only cases where the widget enters Limited mode and shows
        // a takeover message — the user must act to restore full function.
        bool actionRequired = !paused && (
            criticalState != null
            || status?.ConsentActive == false);

        // Routine degraded: capture is partially missing, analysis is still pending,
        // or the local DB is catching up. Show a small badge and a subtle status line;
        // keep the 3 useful metrics visible with last-known values.
        bool routineDegraded = !paused && !actionRequired && (
            degradedToast != null
            || (status != null
                && !string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
            || staleOrPending);

        UserDesktopWidgetMode mode = paused
            ? UserDesktopWidgetMode.Paused
            : actionRequired
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
            TertiaryMetric: BuildOverloadMetric(status, snapshot, mode),
            CanPause: status?.ServiceRunning == true && status.ConsentActive && !paused,
            CanResume: paused,
            CriticalState: criticalState,
            ShowLimitedBadge: routineDegraded);
    }

    private static string BuildTitle(UserDesktopWidgetMode mode, LocalApiStatusResponse? status)
    {
        _ = status;
        // Limited mode keeps "Today" — the critical state card explains what's wrong.
        // Routine degraded states also keep "Today" and use ShowLimitedBadge instead.
        return mode == UserDesktopWidgetMode.Paused ? "Paused" : "Today";
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
            // Limited with no critical state means consent is missing — warn, do not critical.
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
            return BuildRoutineDegradedStatusText(degradedToast);
        }

        if (criticalState != null)
        {
            return criticalState.Message;
        }

        if (status == null)
        {
            return statusHttpCode switch
            {
                401 or 403 => "Reloading local service access.",
                int _ => "Starting Ari locally.",
                _ => "Starting Ari locally."
            };
        }

        if (!status.ServiceRunning)
        {
            return "Reconnecting to the local service.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "Reloading local service access.";
        }

        if (!IsFresh(status.freshness_state))
        {
            return BuildFreshnessStatusText(status, snapshot);
        }

        if (snapshot != null && !IsFresh(snapshot.freshness_state))
        {
            return BuildFreshnessStatusText(status, snapshot);
        }

        if (snapshot is { AnalyzedStateAvailable: false })
        {
            return "Updating fresh analysis.";
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
            return "Working locally while backend sync reconnects.";
        }

        if (IsFreshLiveSession(liveSession))
        {
            return BuildLiveSessionStatusText(liveSession!);
        }

        if (snapshot != null && LocalAttentionSegmentAnalytics.IsDistractionEpisode(snapshot))
        {
            string distractionText = LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(snapshot);
            if (!string.IsNullOrWhiteSpace(distractionText))
            {
                return distractionText;
            }
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
                ? "Updating local view while analysis catches up."
                : "Updating local view.")
            : (dbBusy
                ? "Starting local analysis."
                : "Starting Ari locally.");
    }

    private static bool IsFresh(string? freshnessState)
    {
        return string.Equals(freshnessState, "fresh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRoutineUpdatingState(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalCurrentExperienceState experience)
    {
        if (status == null)
        {
            return false;
        }

        if (!IsFresh(status.freshness_state))
        {
            return true;
        }

        if (snapshot != null && !IsFresh(snapshot.freshness_state))
        {
            return true;
        }

        if (snapshot?.PartialData == true || experience.PartialData)
        {
            return true;
        }

        return snapshot is { AnalyzedStateAvailable: false };
    }

    private static string BuildRoutineDegradedStatusText(UserCommunicationToast degradedToast)
    {
        if (string.Equals(degradedToast.Key, "capture:partial", StringComparison.Ordinal)
            || degradedToast.Key.StartsWith("capture:partial:", StringComparison.Ordinal))
        {
            return "Limited: partial capture. Retrying full input.";
        }

        return degradedToast.Key switch
        {
            "capture:heartbeat_stale" => "Limited: live capture is reconnecting.",
            "capture:session_handoff" => "Limited: session capture is reconnecting.",
            "capture:self_test_failed" => "Limited: live input capture is retrying.",
            "capture:keyboard_missing" => "Limited: keyboard capture is retrying.",
            "capture:mouse_missing" => "Limited: mouse capture is retrying.",
            "capture:ownership_warning" => "Limited: session ownership is reconciling.",
            "capture:non_user_session" => "Limited: Ari is reconnecting to your desktop session.",
            _ => degradedToast.Message
        };
    }

    private static UserDesktopWidgetMetric BuildWorkTodayMetric(
        LocalApiStatusResponse? status,
        LocalTodaySummaryMetrics todaySummary,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return new UserDesktopWidgetMetric("Work today", "Paused");
        }

        // Show last-known timeline value during routine degraded/limited states
        // (stale analysis, partial capture, etc.) — but only when the service is
        // reachable (status != null). During a complete API outage the cached
        // timeline would be misleadingly stale, so fall through to "—".
        if (status != null && todaySummary.HasProcessedTimeline)
        {
            return new UserDesktopWidgetMetric(
                "Work today",
                LocalMetricDurationFormatter.FormatMetricDuration(todaySummary.DisplayedTrackedTodaySeconds));
        }

        return new UserDesktopWidgetMetric("Work today", "—");
    }

    private static UserDesktopWidgetMetric BuildLastFocusMetric(
        LocalApiStatusResponse? status,
        LocalApiLiveSessionStateResponse? liveSession,
        LocalCurrentExperienceState experience,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return new UserDesktopWidgetMetric("Last focus session", "Paused");
        }

        // Show episode duration whenever available — this is the durable focus episode,
        // not a live timer, so it stays stable across degraded and stale states.
        if (experience.FocusEpisodeSeconds.HasValue)
        {
            return new UserDesktopWidgetMetric(
                "Last focus session",
                LocalMetricDurationFormatter.FormatMetricDuration(experience.FocusEpisodeSeconds.Value));
        }

        // Live session is fresh but no analyzed episode yet (warming up).
        // Show the live session elapsed time so the user sees something real immediately;
        // this is replaced by the durable episode duration once the first bucket analyzes.
        if (experience.LiveSessionFresh && liveSession?.SessionStartedAtUtc.HasValue == true)
        {
            double sessionElapsed = (DateTimeOffset.UtcNow - liveSession.SessionStartedAtUtc!.Value).TotalSeconds;
            if (sessionElapsed >= 10d)
            {
                return new UserDesktopWidgetMetric(
                    "Last focus session",
                    LocalMetricDurationFormatter.FormatMetricDuration(sessionElapsed));
            }
        }

        return new UserDesktopWidgetMetric("Last focus session", "—");
    }

    private static UserDesktopWidgetMetric BuildOverloadMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return new UserDesktopWidgetMetric("Pressure", "Paused");
        }

        // Show last-known pressure whenever a snapshot is available — do not hide it
        // during degraded or stale states. pressure_level already prefers the expressive
        // signal and falls back to overload_likelihood for older agent versions.
        double? pressure = snapshot?.pressure_level;
        return new UserDesktopWidgetMetric(
            "Pressure",
            pressure.HasValue ? FormatPercent(pressure.Value) : "—");
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

        string confidenceText = BuildConfidenceFooterText(status, experience);
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

        string activityText = liveSession.user_active ? "Active now" : "Watching live";
        string typingText = liveSession.keyboard_active ? "typing active" : "typing idle";
        string mouseText = liveSession.mouse_active ? "mouse active" : "mouse idle";
        return $"{activityText} | {typingText} | {mouseText}.";
    }

    private static string BuildConfidenceFooterText(
        LocalApiStatusResponse? status,
        LocalCurrentExperienceState experience)
    {
        if (!experience.SnapshotAvailable)
        {
            return "Confidence updating";
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
            return "Tracking quality updating";
        }

        if (!IsFresh(status.freshness_state) || (snapshot != null && !IsFresh(snapshot.freshness_state)))
        {
            return "Tracking quality delayed";
        }

        if (!experience.SnapshotAvailable || snapshot == null)
        {
            return experience.LiveSessionFresh
                ? "Tracking quality updating"
                : "Tracking quality updating";
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
            return "Tracking quality updating";
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
    private static string FormatPercent(double value)
    {
        double normalized = double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;
        return $"{Math.Round(normalized * 100d, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";
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
