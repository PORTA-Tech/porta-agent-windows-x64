using System.Globalization;
using System.Text.Json;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;

namespace Porta.Agent.LocalApi;

public enum LocalCurrentExperienceMode
{
    Unavailable,
    Normal,
    DeepWork,
    Overload
}

public sealed record LocalCurrentExperienceState(
    LocalCurrentExperienceMode Mode,
    bool SnapshotAvailable,
    bool SnapshotAnalysisReady,
    bool DeepWorkActive,
    bool OverloadActive,
    bool LiveSessionFresh,
    bool LiveSessionIdle,
    double? FocusEpisodeSeconds,
    string ConfidenceLabel,
    bool PartialData,
    IReadOnlyList<string> MissingMajorChannels,
    bool ShowDeepWorkStreak,
    string DeepWorkStreakText,
    bool HasRecentRecoveryEvent,
    long RecentRecoveryEventId,
    string RecentRecoveryMessage,
    // Human-readable explanation of the current episode state for local UI surfaces.
    // Prefers the attention-segment reason (explains what is happening right now —
    // away, off-task, fragmentation, etc.) and falls back to the focus-episode reason
    // (why the episode is active, paused, decaying, or ended).
    string EpisodeStatusText,
    string FocusEpisodeReasonText,
    string AttentionSegmentReasonText);

public static class LocalCurrentExperienceStateAdapter
{
    private static readonly TimeSpan RecoveryEventWindow = TimeSpan.FromSeconds(90);

    public static LocalCurrentExperienceState Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        IReadOnlyList<LocalApiEventResponse>? recentEvents,
        DateTimeOffset nowUtc)
    {
        if (snapshot == null)
        {
            return new LocalCurrentExperienceState(
                Mode: LocalCurrentExperienceMode.Unavailable,
                SnapshotAvailable: false,
                SnapshotAnalysisReady: false,
                DeepWorkActive: false,
                OverloadActive: false,
                LiveSessionFresh: IsFreshLiveSession(liveSession),
                LiveSessionIdle: liveSession?.idle == true,
                FocusEpisodeSeconds: null,
                ConfidenceLabel: "Not available",
                PartialData: false,
                MissingMajorChannels: [],
                ShowDeepWorkStreak: false,
                DeepWorkStreakText: string.Empty,
                HasRecentRecoveryEvent: false,
                RecentRecoveryEventId: 0,
                RecentRecoveryMessage: string.Empty,
                EpisodeStatusText: string.Empty,
                FocusEpisodeReasonText: string.Empty,
                AttentionSegmentReasonText: string.Empty);
        }

        bool deepWorkIndicator = status?.DeepWorkActive == true
            || snapshot.DeepWorkActive
            || TryReadDeepWorkActive(snapshot.StateFlagsJson);
        bool overloadIndicator = status?.OverloadActive == true || snapshot.OverloadActive;
        LocalCurrentExperienceMode mode = ResolveMode(status?.VisualMode, overloadIndicator, deepWorkIndicator);
        bool deepWorkActive = mode == LocalCurrentExperienceMode.DeepWork;
        bool overloadActive = mode == LocalCurrentExperienceMode.Overload;
        bool liveSessionFresh = IsFreshLiveSession(liveSession);
        bool liveSessionIdle = liveSession?.idle == true;
        double? focusEpisodeSeconds = ResolveFocusEpisodeSeconds(snapshot, liveSession, nowUtc);
        bool partialData = snapshot.PartialData == true || snapshot.MissingMajorChannels is { Count: > 0 };
        (bool showDeepWorkStreak, string deepWorkStreakText) = BuildDeepWorkStreak(
            deepWorkActive,
            status?.DeepWorkStreakSeconds,
            TryReadDeepWorkStartedAtUtcMs(snapshot.StateFlagsJson),
            snapshot.SustainedAttentionDurationSec,
            nowUtc);
        (bool hasRecentRecoveryEvent, long recentRecoveryEventId, string recentRecoveryMessage) =
            BuildRecentRecoveryEvent(recentEvents ?? [], nowUtc);
        string attentionSegmentReasonText = snapshot.AttentionSegmentReasonText?.Trim() ?? string.Empty;
        string focusEpisodeReasonText = snapshot.FocusEpisodeReasonText?.Trim() ?? string.Empty;
        string episodeStatusText = attentionSegmentReasonText.Length > 0
            ? attentionSegmentReasonText
            : focusEpisodeReasonText;

        return new LocalCurrentExperienceState(
            Mode: mode,
            SnapshotAvailable: true,
            SnapshotAnalysisReady: snapshot.analysis_ready,
            DeepWorkActive: deepWorkActive,
            OverloadActive: overloadActive,
            LiveSessionFresh: liveSessionFresh,
            LiveSessionIdle: liveSessionIdle,
            FocusEpisodeSeconds: focusEpisodeSeconds,
            ConfidenceLabel: ResolveConfidenceLabel(snapshot.TrustTier, snapshot.Confidence),
            PartialData: partialData,
            MissingMajorChannels: snapshot.MissingMajorChannels ?? [],
            ShowDeepWorkStreak: showDeepWorkStreak,
            DeepWorkStreakText: deepWorkStreakText,
            HasRecentRecoveryEvent: hasRecentRecoveryEvent,
            RecentRecoveryEventId: recentRecoveryEventId,
            RecentRecoveryMessage: recentRecoveryMessage,
            EpisodeStatusText: episodeStatusText,
            FocusEpisodeReasonText: focusEpisodeReasonText,
            AttentionSegmentReasonText: attentionSegmentReasonText);
    }

    private static LocalCurrentExperienceMode ResolveMode(string? visualModeFromStatus, bool overloadActive, bool deepWorkActive)
    {
        if (PresentedVisualModeMapper.TryParseToken(visualModeFromStatus, out PresentedVisualMode fromStatus))
        {
            return MapMode(fromStatus);
        }

        return MapMode(PresentedVisualModeMapper.Map(overloadActive, deepWorkActive));
    }

    private static LocalCurrentExperienceMode MapMode(PresentedVisualMode mode)
    {
        return mode switch
        {
            PresentedVisualMode.OVERLOAD => LocalCurrentExperienceMode.Overload,
            PresentedVisualMode.DEEP_WORK => LocalCurrentExperienceMode.DeepWork,
            _ => LocalCurrentExperienceMode.Normal
        };
    }

    private static bool TryReadDeepWorkActive(string? stateFlagsJson)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("deep_work_active", out JsonElement value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.TryGetInt32(out int n) && n == 1,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static long TryReadDeepWorkStartedAtUtcMs(string? stateFlagsJson)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return 0;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("deep_work_started_at_utc_ms", out JsonElement value))
            {
                return 0;
            }

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long startedAtUtcMs)
                ? Math.Max(0, startedAtUtcMs)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static (bool Show, string Text) BuildDeepWorkStreak(
        bool deepWorkActive,
        int? deepWorkStreakSecondsFromStatus,
        long deepWorkStartedAtUtcMs,
        double sustainedAttentionSeconds,
        DateTimeOffset nowUtc)
    {
        if (!deepWorkActive)
        {
            return (false, string.Empty);
        }

        if (deepWorkStartedAtUtcMs > 0)
        {
            DateTimeOffset startedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(deepWorkStartedAtUtcMs);
            TimeSpan streak = nowUtc - startedAtUtc;
            if (streak < TimeSpan.Zero)
            {
                streak = TimeSpan.Zero;
            }

            return (true, $"Deep Work streak: {FormatShortDuration(streak)}");
        }

        if (deepWorkStreakSecondsFromStatus is > 0)
        {
            return (true, $"Deep Work streak: {FormatShortDuration(TimeSpan.FromSeconds(deepWorkStreakSecondsFromStatus.Value))}");
        }

        TimeSpan fallback = TimeSpan.FromSeconds(Math.Max(0d, sustainedAttentionSeconds));
        return (true, $"Deep Work streak: {FormatShortDuration(fallback)}");
    }

    private static (bool Show, long EventId, string Message) BuildRecentRecoveryEvent(
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc)
    {
        LocalApiEventResponse? recoveryEvent = recentEvents
            .OrderByDescending(static evt => evt.TsUtc)
            .FirstOrDefault(static evt =>
                string.Equals(evt.Action, "overload_recovered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(evt.Type, "OVERLOAD_RECOVERED", StringComparison.OrdinalIgnoreCase));
        if (recoveryEvent == null || recoveryEvent.Id <= 0)
        {
            return (false, 0, string.Empty);
        }

        if ((nowUtc - recoveryEvent.TsUtc) > RecoveryEventWindow)
        {
            return (false, 0, string.Empty);
        }

        string message = string.IsNullOrWhiteSpace(recoveryEvent.WhatChanged)
            ? "Recovery complete: overload returned to normal."
            : recoveryEvent.WhatChanged.Trim();
        return (true, recoveryEvent.Id, message);
    }

    // Step 4:
    // The displayed focus duration is a durable focus-episode timer.
    // Live session freshness is only for presence/idle facts and must not
    // restart or replace the visible duration with foreground-app age.
    private static double? ResolveFocusEpisodeSeconds(
        LocalApiCurrentSnapshotResponse snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        DateTimeOffset nowUtc)
    {
        double focusEpisodeSeconds = snapshot.FocusEpisodeSeconds > 0d
            ? snapshot.FocusEpisodeSeconds
            : snapshot.TaskContinuitySeconds;
        if (focusEpisodeSeconds <= 0d)
        {
            return null;
        }

        double projectedSeconds = focusEpisodeSeconds + ResolveLiveProjectionSeconds(snapshot, liveSession, nowUtc);
        return Math.Max(0d, projectedSeconds);
    }

    private static double ResolveLiveProjectionSeconds(
        LocalApiCurrentSnapshotResponse snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        DateTimeOffset nowUtc)
    {
        if (!IsFreshLiveSession(liveSession)
            || liveSession!.idle
            || !liveSession.user_active)
        {
            return 0d;
        }

        string focusEpisodeState = (snapshot.FocusEpisodeState ?? snapshot.ContinuityState ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        if (focusEpisodeState is "reset" or "pause" or "decay")
        {
            return 0d;
        }

        // Use wall-clock time, not liveSession.UpdatedAtUtc, so the projection amount
        // is decoupled from live session timing. The live session gates the projection
        // (active / idle / fresh); the amount is snapshot staleness only.
        if (nowUtc <= snapshot.TimestampUtc)
        {
            return 0d;
        }

        double projectionSeconds = (nowUtc - snapshot.TimestampUtc).TotalSeconds;
        return Math.Clamp(projectionSeconds, 0d, 90d);
    }

    private static bool IsFreshLiveSession(LocalApiLiveSessionStateResponse? liveSession)
    {
        return liveSession != null
            && string.Equals(liveSession.freshness_state, "fresh", StringComparison.OrdinalIgnoreCase);
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

    private static string FormatShortDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1d)
        {
            int hours = (int)Math.Floor(duration.TotalHours);
            int minutes = duration.Minutes;
            return $"{hours.ToString(CultureInfo.InvariantCulture)}h {minutes.ToString("00", CultureInfo.InvariantCulture)}m";
        }

        int mins = (int)Math.Floor(duration.TotalMinutes);
        return $"{Math.Max(0, mins).ToString(CultureInfo.InvariantCulture)}m";
    }
}
