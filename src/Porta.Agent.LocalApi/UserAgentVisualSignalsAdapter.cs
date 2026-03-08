namespace Porta.Agent.LocalApi;

using System.Text.Json;
using Porta.Agent.Common.Contracts;

public enum UserAgentVisualMode
{
    Unavailable,
    Normal,
    DeepWork,
    Overload
}

public sealed record UserAgentVisualSignals(
    UserAgentVisualMode Mode,
    string TooltipText,
    bool ShowDeepWorkStreak,
    string DeepWorkStreakText,
    bool ShowRecoveryToast,
    long RecoveryEventId,
    string RecoveryMessage);

public static class UserAgentVisualSignalsAdapter
{
    private const string WaitingForServiceDataText = "PORTA | Waiting for service data";
    private static readonly TimeSpan RecoveryToastWindow = TimeSpan.FromSeconds(90);

    public static UserAgentVisualSignals Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc,
        long lastRecoveryEventId)
    {
        if (status == null || snapshot == null)
        {
            return new UserAgentVisualSignals(
                Mode: UserAgentVisualMode.Unavailable,
                TooltipText: WaitingForServiceDataText,
                ShowDeepWorkStreak: false,
                DeepWorkStreakText: string.Empty,
                ShowRecoveryToast: false,
                RecoveryEventId: 0,
                RecoveryMessage: string.Empty);
        }

        bool deepWorkActive = status.DeepWorkActive || snapshot.DeepWorkActive || TryReadDeepWorkActive(snapshot.StateFlagsJson);
        bool overloadActive = status.OverloadActive || snapshot.OverloadActive;
        UserAgentVisualMode mode = ResolveMode(status.VisualMode, overloadActive, deepWorkActive);
        (bool showDeepWorkStreak, string deepWorkStreakText) = BuildDeepWorkStreak(
            deepWorkActive,
            status.DeepWorkStreakSeconds,
            TryReadDeepWorkStartedAtUtcMs(snapshot.StateFlagsJson),
            snapshot.SustainedAttentionDurationSec,
            nowUtc);
        (bool showRecoveryToast, long recoveryEventId, string recoveryMessage) = BuildRecoveryToast(
            mode,
            recentEvents,
            nowUtc,
            lastRecoveryEventId);

        return new UserAgentVisualSignals(
            Mode: mode,
            TooltipText: BuildTooltip(status, snapshot, mode, showDeepWorkStreak, deepWorkStreakText),
            ShowDeepWorkStreak: showDeepWorkStreak,
            DeepWorkStreakText: deepWorkStreakText,
            ShowRecoveryToast: showRecoveryToast,
            RecoveryEventId: recoveryEventId,
            RecoveryMessage: recoveryMessage);
    }

    private static string BuildTooltip(
        LocalApiStatusResponse status,
        LocalApiCurrentSnapshotResponse snapshot,
        UserAgentVisualMode mode,
        bool showDeepWorkStreak,
        string deepWorkStreakText)
    {
        if (!status.ServiceRunning)
        {
            return "PORTA | Core service unavailable";
        }

        if (!status.ConsentActive)
        {
            return "PORTA | Consent needed";
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return "PORTA | User-session capture unavailable";
        }

        if (snapshot.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            string missing = string.Join("/", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            return $"PORTA | Partial capture | Missing {missing}";
        }

        return mode switch
        {
            UserAgentVisualMode.DeepWork when showDeepWorkStreak => $"PORTA | {deepWorkStreakText}",
            UserAgentVisualMode.DeepWork => "PORTA | Deep Work active",
            UserAgentVisualMode.Overload => "PORTA | Overload regulation active",
            UserAgentVisualMode.Normal => "PORTA | Watching activity",
            _ => WaitingForServiceDataText
        };
    }

    private static (bool Show, string Text) BuildDeepWorkStreak(
        bool deepWorkActive,
        int deepWorkStreakSecondsFromStatus,
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

        if (deepWorkStreakSecondsFromStatus > 0)
        {
            return (true, $"Deep Work streak: {FormatShortDuration(TimeSpan.FromSeconds(deepWorkStreakSecondsFromStatus))}");
        }

        TimeSpan fallback = TimeSpan.FromSeconds(Math.Max(0d, sustainedAttentionSeconds));
        return (true, $"Deep Work streak: {FormatShortDuration(fallback)}");
    }

    private static (bool Show, long EventId, string Message) BuildRecoveryToast(
        UserAgentVisualMode mode,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc,
        long lastRecoveryEventId)
    {
        if (mode == UserAgentVisualMode.Overload)
        {
            return (false, 0, string.Empty);
        }

        LocalApiEventResponse? recoveryEvent = recentEvents
            .OrderByDescending(static evt => evt.TsUtc)
            .FirstOrDefault(static evt =>
                string.Equals(evt.Action, "overload_recovered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(evt.Type, "OVERLOAD_RECOVERED", StringComparison.OrdinalIgnoreCase));
        if (recoveryEvent == null || recoveryEvent.Id <= 0 || recoveryEvent.Id == lastRecoveryEventId)
        {
            return (false, 0, string.Empty);
        }

        if ((nowUtc - recoveryEvent.TsUtc) > RecoveryToastWindow)
        {
            return (false, 0, string.Empty);
        }

        string message = string.IsNullOrWhiteSpace(recoveryEvent.WhatChanged)
            ? "Overload eased. Returning to normal."
            : recoveryEvent.WhatChanged.Trim();
        return (true, recoveryEvent.Id, message);
    }

    private static UserAgentVisualMode ResolveMode(string? visualModeFromStatus, bool overloadActive, bool deepWorkActive)
    {
        if (PresentedVisualModeMapper.TryParseToken(visualModeFromStatus, out PresentedVisualMode fromStatus))
        {
            return MapMode(fromStatus);
        }

        return MapMode(PresentedVisualModeMapper.Map(overloadActive, deepWorkActive));
    }

    private static UserAgentVisualMode MapMode(PresentedVisualMode mode)
    {
        return mode switch
        {
            PresentedVisualMode.OVERLOAD => UserAgentVisualMode.Overload,
            PresentedVisualMode.DEEP_WORK => UserAgentVisualMode.DeepWork,
            _ => UserAgentVisualMode.Normal
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

    private static string ToDisplayChannelName(string channel)
    {
        return channel.ToLowerInvariant() switch
        {
            "keyboard" => "Keyboard",
            "mouse" => "Mouse",
            "focus" => "Focus",
            "idle" => "Idle",
            _ => channel
        };
    }

    private static string FormatShortDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1d)
        {
            return $"{Math.Floor(duration.TotalHours):F0}h {duration.Minutes:F0}m";
        }

        if (duration.TotalMinutes >= 1d)
        {
            return $"{Math.Floor(duration.TotalMinutes):F0}m";
        }

        return $"{Math.Max(0d, Math.Floor(duration.TotalSeconds)):F0}s";
    }
}
