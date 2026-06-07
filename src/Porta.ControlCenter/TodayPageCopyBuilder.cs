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
                TodayTrackingState.StillTracking => "Today is updating normally.",
                TodayTrackingState.TrackingLimited => "Today is still updating, but some fresh details are delayed.",
                TodayTrackingState.Paused => "Tracking is paused, so Today will not update.",
                _ => "Today cannot load fresh tracking data right now."
            };
        }

        if (normalized.Contains("using cached snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("DB busy", StringComparison.OrdinalIgnoreCase)
                ? "Today is showing the last confirmed update while the app catches up with your recent activity."
                : "Today is showing the last confirmed update while your recent activity catches up.";
        }

        if (normalized.Contains("status stale", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("DB busy", StringComparison.OrdinalIgnoreCase)
                ? "Today is still updating, but the newest activity is delayed while the app catches up."
                : "Today is still updating, but the newest activity is delayed right now.";
        }

        if (normalized.Contains("using current snapshot while status refresh retries", StringComparison.OrdinalIgnoreCase))
        {
            return "Today is still updating, and the newest status details are catching up.";
        }

        if (normalized.Contains("waiting for current snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return "Today is connected, and today's totals are still updating.";
        }

        if (string.Equals(normalized, "API auth mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Today cannot verify access to the local service. Open System to reconnect it.";
        }

        if (string.Equals(normalized, "Service offline", StringComparison.OrdinalIgnoreCase))
        {
            return "The desktop service is offline. Open System to start it again.";
        }

        if (string.Equals(normalized, "Local API unreachable", StringComparison.OrdinalIgnoreCase))
        {
            return "Today cannot reach the local service right now. Open System and try refresh again.";
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
            return $"Ari is not tracking right now: {detail}.";
        }

        if (normalized.StartsWith("Waiting for live capture:", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is connected, but activity is still warming up.";
        }

        return normalized;
    }

    public static string BuildEmptyStateGuidance(TodayTrackingState state)
    {
        return state switch
        {
            TodayTrackingState.StillTracking => "Keep working for a few minutes and Today will fill in.",
            TodayTrackingState.TrackingLimited => "Keep working for a minute, open Tracking to review tracking quality, or open System for recovery details.",
            TodayTrackingState.Paused => "Resume tracking when you are ready to continue.",
            _ => "Open System to check the service, permissions, and connection."
        };
    }

    /// <summary>
    /// Returns a contextual Ari status bubble message for the Today page.
    /// All parameters are nullable / zero-safe — always returns a non-null string.
    /// </summary>
    public static string BuildAriStatusMessage(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiAriContextResponse? context,
        double todayFocusMin,
        double todayTrackedMin,
        int distractionCount,
        double avgRecoveryMin,
        bool isPressureHigh,
        int contextSwitchesRecent)
    {
        int currentHour = DateTime.Now.Hour;

        // Morning — no focus yet
        if (todayFocusMin < 5)
        {
            if (context?.StrongWindowStart is { } swStart && context.StrongWindowEnd is { } swEnd)
            {
                int timeLeft = swStart - currentHour;
                if (timeLeft is > 0 and <= 3)
                    return $"Your best window — {context.StrongWindowLabel} — starts in {timeLeft}h. Get ready.";
                if (timeLeft <= 0 && currentHour <= swEnd)
                    return $"Your strongest window is open now — {context.StrongWindowLabel}. No focus yet today.";
            }
            return "No focus yet today. Ari is ready when you are.";
        }

        // Pressure state
        if (isPressureHigh || contextSwitchesRecent >= 3)
        {
            return $"Pressure building — {contextSwitchesRecent} context switches recently. Consider slowing down.";
        }

        // Active session going well
        if (snapshot?.FocusStrength > 0.6)
        {
            string namePrefix = string.IsNullOrWhiteSpace(context?.FirstName)
                ? "Strong start —"
                : $"{context.FirstName}, strong start —";
            return $"{namePrefix} {(int)todayFocusMin}min of focus so far today.";
        }

        // Has had distraction but recovering fast
        if (distractionCount > 0 && avgRecoveryMin > 0 && avgRecoveryMin < 5)
        {
            return $"{distractionCount} distraction episodes — recovered from each in under {(int)avgRecoveryMin}min. Unusually fast.";
        }

        // End of day (tracked > 3h and current time > 18:00)
        if (todayTrackedMin >= 180 && currentHour >= 18)
        {
            int focusPct = (int)(todayFocusMin / todayTrackedMin * 100);
            return $"{(int)todayFocusMin}min focused out of {(int)todayTrackedMin}min tracked — {focusPct}%. Digest fires at 23:30.";
        }

        // Default active state
        return $"{(int)todayFocusMin}min of focus today. Ari is watching.";
    }

    private static bool IsFreshState(string? freshnessState) =>
        string.Equals(freshnessState, "fresh", StringComparison.OrdinalIgnoreCase);
}
