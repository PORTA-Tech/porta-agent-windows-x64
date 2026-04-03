using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using Porta.Agent.Common.Contracts;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

// Step 17 guardrail:
// This adapter only maps backend state to UI visuals. It must not perform any OS-manipulation behavior.
internal enum PortaVisualMode
{
    Unavailable,
    Normal,
    DeepWork,
    Overload
}

internal sealed record PortaModeTheme(
    Color AccentColor,
    Color BadgeColor,
    Color StatusChipColor,
    Color BackgroundTint);

internal sealed record PortaVisualState(
    PortaVisualMode Mode,
    PortaModeTheme Theme,
    string ModeLabel,
    string ConfidenceLabel,
    string CurrentFocusSessionText,
    double? CurrentFocusSessionSeconds,
    bool SnapshotAnalysisReady,
    string FocusStatusText,
    string OperatingSentence,
    bool OverloadActive,
    bool DeepWorkActive,
    bool ShowDeepWorkStreak,
    string DeepWorkStreakText,
    bool ShowRecoveryBanner,
    long RecoveryEventId,
    string RecoveryBannerText,
    string WhySummary);

internal static class VisualStateAdapter
{
    private const string CalibrationStageCold = "cold";
    private const string CalibrationStageProvisional = "provisional";
    private const string CalibrationStageCalibrated = "calibrated";
    private const string TrustTierLow = "low";
    private const string TrustTierMedium = "medium";
    private const string TrustTierHigh = "high";
    private const string NotAvailableText = "Not available";
    private const string WaitingForServiceDataText = "Waiting for service data";
    private static readonly TimeSpan RecoveryBannerWindow = TimeSpan.FromSeconds(90);
    private static readonly IReadOnlyDictionary<PortaVisualMode, PortaModeTheme> ThemeByMode =
        new Dictionary<PortaVisualMode, PortaModeTheme>
        {
            [PortaVisualMode.Unavailable] = new(
                AccentColor: Color.FromRgb(107, 116, 128),
                BadgeColor: Color.FromRgb(71, 85, 105),
                StatusChipColor: Color.FromRgb(51, 65, 85),
                BackgroundTint: Color.FromArgb(0x10, 107, 116, 128)),
            [PortaVisualMode.Normal] = new(
                AccentColor: Color.FromRgb(127, 209, 163),
                BadgeColor: Color.FromRgb(76, 118, 99),
                StatusChipColor: Color.FromRgb(25, 90, 68),
                BackgroundTint: Color.FromArgb(0x10, 127, 209, 163)),
            [PortaVisualMode.DeepWork] = new(
                AccentColor: Color.FromRgb(94, 200, 255),
                BadgeColor: Color.FromRgb(57, 107, 136),
                StatusChipColor: Color.FromRgb(18, 68, 102),
                BackgroundTint: Color.FromArgb(0x12, 94, 200, 255)),
            [PortaVisualMode.Overload] = new(
                AccentColor: Color.FromRgb(255, 180, 84),
                BadgeColor: Color.FromRgb(141, 98, 43),
                StatusChipColor: Color.FromRgb(120, 74, 26),
                BackgroundTint: Color.FromArgb(0x14, 255, 180, 84))
        };

    public static PortaVisualState Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        string fallbackExplanation,
        DateTimeOffset nowUtc)
    {
        if (snapshot == null)
        {
            string waitingReason = string.IsNullOrWhiteSpace(fallbackExplanation)
                ? WaitingForServiceDataText
                : fallbackExplanation;
            return new PortaVisualState(
                Mode: PortaVisualMode.Unavailable,
                Theme: ThemeByMode[PortaVisualMode.Unavailable],
                ModeLabel: "Unavailable",
                ConfidenceLabel: NotAvailableText,
                CurrentFocusSessionText: $"Current focus session: {NotAvailableText}",
                CurrentFocusSessionSeconds: null,
                SnapshotAnalysisReady: false,
                FocusStatusText: waitingReason,
                OperatingSentence: waitingReason,
                OverloadActive: false,
                DeepWorkActive: false,
                ShowDeepWorkStreak: false,
                DeepWorkStreakText: string.Empty,
                ShowRecoveryBanner: false,
                RecoveryEventId: 0,
                RecoveryBannerText: string.Empty,
                WhySummary: NotAvailableText);
        }

        // Source: GET /v1/status -> deep_work_active (fallback: state_flags_json.deep_work_active)
        bool deepWorkActive = status?.DeepWorkActive ?? TryReadDeepWorkActive(snapshot.StateFlagsJson);
        // Source: GET /v1/snapshot/current -> state_flags_json.deep_work_started_at_utc_ms
        long deepWorkStartedAtUtcMs = TryReadDeepWorkStartedAtUtcMs(snapshot.StateFlagsJson);
        // Source: GET /v1/status -> overload_active (fallback: snapshot.overload_active)
        bool overloadActive = status?.OverloadActive ?? snapshot.OverloadActive;
        PortaVisualMode mode = ResolveMode(status?.VisualMode, overloadActive, deepWorkActive);
        PortaModeTheme theme = ThemeByMode[mode];

        string focusStatus = BuildFocusStatus(snapshot);
        string operatingSentence = BuildOperatingSentence(status, snapshot, mode, fallbackExplanation);
        double? currentFocusSessionSeconds = ResolveCurrentFocusSessionSeconds(snapshot, liveSession);
        (bool showRecoveryBanner, long recoveryEventId, string recoveryBannerText) = BuildRecoveryBanner(mode, recentEvents, nowUtc);
        (bool showDeepWorkStreak, string deepWorkStreakText) = BuildDeepWorkStreak(
            deepWorkActive,
            status?.DeepWorkStreakSeconds,
            deepWorkStartedAtUtcMs,
            snapshot.SustainedAttentionDurationSec,
            nowUtc);
        string whySummary = BuildWhySummary(
            status?.LatestContributorsJson,
            status?.LatestWhyJson,
            snapshot,
            recentEvents);

        return new PortaVisualState(
            Mode: mode,
            Theme: theme,
            ModeLabel: ToModeLabel(mode),
            ConfidenceLabel: ResolveConfidenceLabel(snapshot.TrustTier, snapshot.Confidence),
            CurrentFocusSessionText: BuildCurrentFocusSessionText(snapshot, liveSession),
            CurrentFocusSessionSeconds: currentFocusSessionSeconds,
            SnapshotAnalysisReady: snapshot.analysis_ready,
            FocusStatusText: focusStatus,
            OperatingSentence: operatingSentence,
            OverloadActive: overloadActive,
            DeepWorkActive: deepWorkActive,
            ShowDeepWorkStreak: showDeepWorkStreak,
            DeepWorkStreakText: deepWorkStreakText,
            ShowRecoveryBanner: showRecoveryBanner,
            RecoveryEventId: recoveryEventId,
            RecoveryBannerText: recoveryBannerText,
            WhySummary: whySummary);
    }

    // Metric contract:
    // - Current focus session = live-first timer from /v1/session/live.
    // - Focused today remains a durable processed-history metric and lands on the Today page in the next pass.
    private static string BuildCurrentFocusSessionText(
        LocalApiCurrentSnapshotResponse snapshot,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        double? liveFocusSeconds = ResolveLiveCurrentFocusSessionSeconds(liveSession);
        if (liveFocusSeconds.HasValue)
        {
            return $"Current focus session: {FormatFocusedTime(liveFocusSeconds.Value)}";
        }

        if (snapshot.analysis_ready)
        {
            return $"Current focus session: {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}";
        }

        if (IsFreshLiveSession(liveSession) && liveSession!.idle)
        {
            return "Current focus session: 0 sec";
        }

        return $"Current focus session: {NotAvailableText}";
    }

    internal static double? ResolveCurrentFocusSessionSeconds(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        double? liveFocusSeconds = ResolveLiveCurrentFocusSessionSeconds(liveSession);
        if (liveFocusSeconds.HasValue)
        {
            return liveFocusSeconds;
        }

        return snapshot?.analysis_ready == true
            ? Math.Max(0d, snapshot.SustainedAttentionDurationSec)
            : null;
    }

    private static double? ResolveLiveCurrentFocusSessionSeconds(LocalApiLiveSessionStateResponse? liveSession)
    {
        if (!IsFreshLiveSession(liveSession)
            || liveSession!.idle
            || !liveSession.focus_elapsed_seconds.HasValue)
        {
            return null;
        }

        return Math.Max(0d, liveSession.focus_elapsed_seconds.Value);
    }

    private static bool IsFreshLiveSession(LocalApiLiveSessionStateResponse? liveSession)
    {
        return liveSession != null
            && string.Equals(liveSession.freshness_state, "fresh", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildOperatingSentence(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse snapshot,
        PortaVisualMode mode,
        string fallbackExplanation)
    {
        if (status?.Paused == true)
        {
            if (status.PauseUntilUtc.HasValue && status.PauseUntilUtc.Value > DateTimeOffset.UtcNow)
            {
                return $"Paused until {status.PauseUntilUtc.Value.ToLocalTime():h:mm tt}";
            }

            return "Paused.";
        }

        if (status?.ConsentActive == false)
        {
            return "Waiting for consent before collecting.";
        }

        if (mode == PortaVisualMode.DeepWork)
        {
            return "Deep Work mode active: minimizing interruptions.";
        }

        if (mode == PortaVisualMode.Overload)
        {
            return "Overload regulation is active.";
        }

        string calibrationStage = NormalizeCalibrationStage(snapshot.CalibrationStage, snapshot.BaselineReady, snapshot.ColdStart);
        if (string.Equals(calibrationStage, CalibrationStageCold, StringComparison.Ordinal))
        {
            return "Learning your pattern before suggesting actions.";
        }

        if (string.Equals(calibrationStage, CalibrationStageProvisional, StringComparison.Ordinal))
        {
            return "Learning your pattern: suggestions are active while stronger actions stay gated.";
        }

        if (!string.IsNullOrWhiteSpace(fallbackExplanation))
        {
            return fallbackExplanation;
        }

        return "Ready to suggest breaks when needed.";
    }

    private static (bool Show, long EventId, string Message) BuildRecoveryBanner(
        PortaVisualMode mode,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc)
    {
        if (mode == PortaVisualMode.Overload)
        {
            return (false, 0, string.Empty);
        }

        LocalApiEventResponse? recoveryEvent = recentEvents
            .OrderByDescending(static evt => evt.TsUtc)
            .FirstOrDefault(static evt =>
                string.Equals(evt.Action, "overload_recovered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(evt.Type, "OVERLOAD_RECOVERED", StringComparison.OrdinalIgnoreCase));
        if (recoveryEvent == null)
        {
            return (false, 0, string.Empty);
        }

        if ((nowUtc - recoveryEvent.TsUtc) > RecoveryBannerWindow)
        {
            return (false, 0, string.Empty);
        }

        string message = string.IsNullOrWhiteSpace(recoveryEvent.WhatChanged)
            ? "Recovery complete: overload returned to normal."
            : recoveryEvent.WhatChanged.Trim();
        return (true, recoveryEvent.Id, message);
    }

    private static string BuildWhySummary(
        string? latestContributorsJsonFromStatus,
        string? latestWhyJsonFromStatus,
        LocalApiCurrentSnapshotResponse snapshot,
        IReadOnlyList<LocalApiEventResponse> recentEvents)
    {
        if (!string.IsNullOrWhiteSpace(latestContributorsJsonFromStatus)
            || !string.IsNullOrWhiteSpace(snapshot.ContributorsJson))
        {
            return "Live signal explain data is available.";
        }

        if (!string.IsNullOrWhiteSpace(latestWhyJsonFromStatus))
        {
            if (TryExtractReason(latestWhyJsonFromStatus, out string reasonFromStatus))
            {
                return reasonFromStatus;
            }
        }

        LocalApiEventResponse? latest = recentEvents
            .OrderByDescending(static evt => evt.TsUtc)
            .FirstOrDefault();
        if (latest == null || string.IsNullOrWhiteSpace(latest.WhyJson))
        {
            return NotAvailableText;
        }

        if (TryExtractReason(latest.WhyJson, out string reasonFromRecent))
        {
            return reasonFromRecent;
        }

        return NotAvailableText;
    }

    private static bool TryExtractReason(string whyJson, out string reason)
    {
        reason = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(whyJson);
            JsonElement root = doc.RootElement;
            reason = root.TryGetProperty("reason", out JsonElement reasonEl)
                ? reasonEl.GetString() ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                return true;
            }
        }
        catch
        {
        }

        reason = string.Empty;
        return false;
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
            TimeSpan statusStreak = TimeSpan.FromSeconds(deepWorkStreakSecondsFromStatus.Value);
            return (true, $"Deep Work streak: {FormatShortDuration(statusStreak)}");
        }

        TimeSpan fallback = TimeSpan.FromSeconds(Math.Max(0d, sustainedAttentionSeconds));
        return (true, $"Deep Work streak: {FormatShortDuration(fallback)}");
    }

    private static PortaVisualMode ResolveMode(string? visualModeFromStatus, bool overloadActive, bool deepWorkActive)
    {
        if (PresentedVisualModeMapper.TryParseToken(visualModeFromStatus, out PresentedVisualMode fromStatus))
        {
            return MapPresentedMode(fromStatus);
        }

        PresentedVisualMode mapped = PresentedVisualModeMapper.Map(overloadActive, deepWorkActive);
        return MapPresentedMode(mapped);
    }

    private static PortaVisualMode MapPresentedMode(PresentedVisualMode mode)
    {
        return mode switch
        {
            PresentedVisualMode.OVERLOAD => PortaVisualMode.Overload,
            PresentedVisualMode.DEEP_WORK => PortaVisualMode.DeepWork,
            _ => PortaVisualMode.Normal
        };
    }

    private static string ToModeLabel(PortaVisualMode mode)
    {
        return mode switch
        {
            PortaVisualMode.Overload => PresentedVisualModeMapper.ToToken(PresentedVisualMode.OVERLOAD),
            PortaVisualMode.DeepWork => PresentedVisualModeMapper.ToToken(PresentedVisualMode.DEEP_WORK),
            PortaVisualMode.Normal => PresentedVisualModeMapper.ToToken(PresentedVisualMode.NORMAL),
            _ => "UNAVAILABLE"
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

    private static string BuildFocusStatus(LocalApiCurrentSnapshotResponse snapshot)
    {
        if (snapshot.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            string missing = string.Join("/", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            return $"Focus streak paused: missing {missing} signals.";
        }

        if (snapshot.PartialData == true)
        {
            return "Focus streak is conservative while data is partial.";
        }

        if (snapshot.SustainedAttentionDurationSec <= 0.5d)
        {
            return "No continuous focus streak detected yet.";
        }

        return string.Empty;
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

    private static string ResolveConfidenceLabel(string? trustTier, double confidence)
    {
        if (!string.IsNullOrWhiteSpace(trustTier))
        {
            return trustTier.Trim().ToLowerInvariant() switch
            {
                TrustTierHigh => "High",
                TrustTierMedium => "Medium",
                _ => "Low"
            };
        }

        return MapConfidenceLabel(confidence);
    }

    private static string NormalizeCalibrationStage(string? calibrationStage, bool? baselineReady, bool? coldStart)
    {
        if (!string.IsNullOrWhiteSpace(calibrationStage))
        {
            string normalized = calibrationStage.Trim().ToLowerInvariant();
            return normalized switch
            {
                CalibrationStageCalibrated => CalibrationStageCalibrated,
                CalibrationStageProvisional => CalibrationStageProvisional,
                _ => CalibrationStageCold
            };
        }

        if (baselineReady == true)
        {
            return CalibrationStageCalibrated;
        }

        return coldStart == true ? CalibrationStageCold : CalibrationStageProvisional;
    }

    private static string MapConfidenceLabel(double confidence)
    {
        if (confidence >= 0.75d)
        {
            return "High";
        }

        return confidence >= 0.50d ? "Medium" : "Low";
    }

    private static string FormatFocusedTime(double sustainedAttentionSeconds)
    {
        if (sustainedAttentionSeconds < 60d)
        {
            return $"{Math.Max(0d, sustainedAttentionSeconds):F0} sec";
        }

        double minutes = Math.Floor(sustainedAttentionSeconds / 60d);
        return $"{minutes.ToString("F0", CultureInfo.InvariantCulture)} min";
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
