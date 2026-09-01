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

    /// <summary>
    /// Task attribution (2026-09-01) — the one line that stops this change reading as lost work.
    ///
    /// When Ari measures real work it cannot place on a task, it holds the seconds rather than
    /// crediting them to whichever task happened to be open. That is the correct behaviour and it
    /// is the whole point of the change — but from the user's side the only VISIBLE effect is that
    /// a task's hours are lower than they expected. Without this line the honest answer and a bug
    /// look identical, and the user has no way to tell which one they are looking at.
    ///
    /// Deliberately one line and nothing else. No card, no prompt, no resolution flow: asking
    /// "was that still this task?" is a separate piece of work and a much bigger one, because an
    /// ask that cannot act on the answer is worse than no ask at all.
    ///
    /// Returns null when there is nothing to say. Zero unassigned time is the normal case and a
    /// line reading "0m not assigned" would be noise on every good day.
    /// </summary>
    public static string? BuildUnassignedLine(double unassignedSeconds)
    {
        if (!double.IsFinite(unassignedSeconds) || unassignedSeconds < 60d)
        {
            // Under a minute is a rounding artefact, not a finding. The gate needs ten continuous
            // minutes to hold anything, so a real hold is never this small.
            return null;
        }

        return $"{LocalMetricDurationFormatter.FormatMetricDuration(unassignedSeconds)} worked, not assigned to a task";
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
    /// State machine: Cold → Morning → Active → Pressure → Recovery → End of day.
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
        int contextSwitchesRecent,
        string? sessionStartLabel = null,
        string? dayName = null,
        double? dayAvgFocusMin = null)
    {
        int currentHour = DateTime.Now.Hour;
        string currentTimeLabel = DateTime.Now.ToString("h:mm tt");
        string name = string.IsNullOrWhiteSpace(context?.FirstName) ? string.Empty : context!.FirstName!.Trim();
        int daysOfData = context?.DaysOfData ?? 0;

        // --- Cold / calibrating (< 3 days of data) ---
        if (daysOfData < 3 && todayFocusMin < 5)
        {
            return $"Ari has {Math.Max(daysOfData, 1)} day{(daysOfData == 1 ? "" : "s")} of data from you.\nKeep showing up — the picture gets sharper with every session.";
        }

        // --- Morning / session not started yet ---
        if (todayFocusMin < 5)
        {
            if (context?.StrongWindowStart is { } swStart && context.StrongWindowEnd is { } swEnd)
            {
                int minsLeft = (swEnd * 60) - (currentHour * 60 + DateTime.Now.Minute);
                if (minsLeft > 0 && currentHour >= swStart)
                {
                    // Inside the window, time remaining
                    string winLabel = context.StrongWindowLabel ?? $"{swStart}–{swEnd}";
                    return string.IsNullOrEmpty(name)
                        ? $"Your strongest window is {winLabel}.\nIt's {currentTimeLabel}. You've got {minsLeft}min of it left."
                        : $"Your strongest window is {winLabel}, {name}.\nIt's {currentTimeLabel}. You've got {minsLeft}min of it left.";
                }
                if (currentHour < swStart && swStart - currentHour <= 2)
                {
                    // Window opens soon
                    string winLabel = context.StrongWindowLabel ?? $"{swStart}–{swEnd}";
                    return $"Your strongest window — {winLabel} — opens in {(swStart - currentHour) * 60 - DateTime.Now.Minute}min.\nNo focus yet today.";
                }
            }
            if (!string.IsNullOrEmpty(dayName))
                return $"No focus yet today. Your best {dayName}s usually start before 10am.\nStill time.";
            return "No focus yet today. Ari is ready when you are.";
        }

        // --- Pressure building ---
        if (isPressureHigh || contextSwitchesRecent >= 3)
        {
            if (!string.IsNullOrEmpty(name))
                return $"{name}, the pressure reading is climbing.\n{contextSwitchesRecent} context switches recently. Consider a break before your next block.";
            return $"Pressure building — {contextSwitchesRecent} context switches recently.\nAri suggests a short stop now.";
        }

        // --- Active session going well with distraction recovery ---
        if (distractionCount > 0 && avgRecoveryMin > 0 && avgRecoveryMin < 5)
        {
            return $"You've had {distractionCount} distraction episodes but recovered from each in under {(int)Math.Ceiling(avgRecoveryMin)}min.\nThat's unusually fast for you.";
        }

        // --- Active session going well ---
        if (snapshot?.FocusStrength > 0.6 || todayFocusMin >= 20)
        {
            if (dayAvgFocusMin is > 0 && !string.IsNullOrEmpty(dayName) && !string.IsNullOrEmpty(sessionStartLabel))
                return $"Strong start — {(int)todayFocusMin}min of focus since {sessionStartLabel}.\nAbove your {dayName} average of {(int)dayAvgFocusMin.Value}min.";
            if (!string.IsNullOrEmpty(sessionStartLabel))
                return $"Strong start — {(int)todayFocusMin}min of focus since {sessionStartLabel}.\nAri is watching.";
            return $"Strong start — {(int)todayFocusMin}min of focus so far today.\nAri is watching.";
        }

        // --- End of day / review ---
        if (todayTrackedMin >= 180 && currentHour >= 18)
        {
            int focusPct = todayTrackedMin > 0 ? (int)(todayFocusMin / todayTrackedMin * 100) : 0;
            double focusScore = snapshot?.TaskContinuityScore is > 0 ? Math.Round(snapshot!.TaskContinuityScore * 100) : 0;
            if (context?.StrongWindowLabel is { } swLabel && focusScore > 0)
                return $"Strong day — {focusScore} focus score, {focusPct}% of tracked time in focus.\nYour {swLabel} window produced your best work. Digest fires at 23:30.";
            return $"{(int)todayFocusMin}min focused out of {(int)todayTrackedMin}min tracked — {focusPct}%.\nDigest fires at 23:30.";
        }

        // --- Default active state ---
        return $"{(int)todayFocusMin}min of focus today. Ari is watching.";
    }

    private static bool IsFreshState(string? freshnessState) =>
        string.Equals(freshnessState, "fresh", StringComparison.OrdinalIgnoreCase);
}
