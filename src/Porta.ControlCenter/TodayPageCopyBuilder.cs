using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal enum TodayTrackingState
{
    StillTracking,
    TrackingLimited,
    Paused,
    Unavailable
}

internal static class TodayPageCopyBuilder
{
    public static TodayTrackingState ResolveTrackingState(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        if (status?.Paused == true)
        {
            return TodayTrackingState.Paused;
        }

        if (status == null || !status.ServiceRunning || !status.LocalApiAuthReady)
        {
            return TodayTrackingState.Unavailable;
        }

        if (snapshot?.PartialData == true || snapshot?.MissingMajorChannels is { Count: > 0 })
        {
            return TodayTrackingState.TrackingLimited;
        }

        bool liveFresh = IsFreshState(liveSession?.freshness_state);
        if (snapshot == null)
        {
            return liveFresh ? TodayTrackingState.StillTracking : TodayTrackingState.TrackingLimited;
        }

        bool statusFresh = IsFreshState(status.freshness_state);
        bool snapshotFresh = IsFreshState(snapshot.freshness_state);
        return liveFresh && statusFresh && snapshotFresh
            ? TodayTrackingState.StillTracking
            : TodayTrackingState.TrackingLimited;
    }

    public static string BuildStateLabel(TodayTrackingState state, DateTimeOffset? pauseUntilUtc)
    {
        return state switch
        {
            TodayTrackingState.StillTracking => "Still tracking",
            TodayTrackingState.TrackingLimited => "Tracking limited",
            TodayTrackingState.Paused when pauseUntilUtc.HasValue && pauseUntilUtc.Value > DateTimeOffset.UtcNow
                => $"Paused until {pauseUntilUtc.Value.ToLocalTime():h:mm tt}",
            TodayTrackingState.Paused => "Paused",
            _ => "Unavailable"
        };
    }

    public static string BuildReadableExplanation(TodayTrackingState state, string? rawExplanation)
    {
        string normalized = rawExplanation?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return state switch
            {
                TodayTrackingState.StillTracking => "Ari is tracking normally.",
                TodayTrackingState.TrackingLimited => "Ari is still tracking, but some fresh details are delayed.",
                TodayTrackingState.Paused => "Ari is not collecting while tracking is paused.",
                _ => "Ari cannot load fresh tracking data right now."
            };
        }

        if (normalized.Contains("using cached snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("DB busy", StringComparison.OrdinalIgnoreCase)
                ? "Ari is still tracking, but this page is using a cached snapshot while the local database catches up."
                : "Ari is still tracking, but this page is using a cached snapshot while fresh analysis catches up.";
        }

        if (normalized.Contains("status stale", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("DB busy", StringComparison.OrdinalIgnoreCase)
                ? "Ari is still tracking, but live status is delayed while the local database catches up."
                : "Ari is still tracking, but live status is delayed right now.";
        }

        if (normalized.Contains("using current snapshot while status refresh retries", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is still tracking, and the newest status details are catching up.";
        }

        if (normalized.Contains("waiting for current snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is connected, and today's processed totals are still filling in.";
        }

        if (string.Equals(normalized, "API auth mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Control Center cannot verify access to the local service. Open System to reconnect it.";
        }

        if (string.Equals(normalized, "Service offline", StringComparison.OrdinalIgnoreCase))
        {
            return "The desktop service is offline. Open System to start it again.";
        }

        if (string.Equals(normalized, "Local API unreachable", StringComparison.OrdinalIgnoreCase))
        {
            return "Control Center cannot reach the local service right now. Open System and try refresh again.";
        }

        if (normalized.Contains("Consent needed", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is waiting for consent before it can resume tracking.";
        }

        if (normalized.StartsWith("Ownership mismatch:", StringComparison.OrdinalIgnoreCase))
        {
            return "Tracking is limited because the active capture session does not match this Windows session.";
        }

        if (normalized.StartsWith("Not collecting:", StringComparison.OrdinalIgnoreCase))
        {
            string detail = normalized["Not collecting:".Length..].Trim();
            return $"Ari is not collecting right now: {detail}.";
        }

        if (normalized.StartsWith("Waiting for live capture:", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is connected, but live capture is still warming up.";
        }

        return normalized;
    }

    public static string BuildEmptyStateGuidance(TodayTrackingState state)
    {
        return state switch
        {
            TodayTrackingState.StillTracking => "Keep working for a few minutes and today's totals will fill in.",
            TodayTrackingState.TrackingLimited => "Keep working for a minute or open System for tracking details.",
            TodayTrackingState.Paused => "Resume tracking when you are ready to continue.",
            _ => "Open System to check the service, permissions, and connection."
        };
    }

    private static bool IsFreshState(string? freshnessState) =>
        string.Equals(freshnessState, "fresh", StringComparison.OrdinalIgnoreCase);
}
