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
    private const string NotAvailableText = "Not available";
    private const string WaitingForServiceDataText = "Waiting for service data";
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
        LocalCurrentExperienceState experience = LocalCurrentExperienceStateAdapter.Build(
            status,
            snapshot,
            liveSession,
            recentEvents,
            nowUtc);

        if (!experience.SnapshotAvailable || snapshot == null)
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

        PortaVisualMode mode = MapMode(experience.Mode);
        PortaModeTheme theme = ThemeByMode[mode];

        string focusStatus = BuildFocusStatus(snapshot);
        string operatingSentence = BuildOperatingSentence(status, snapshot, mode, fallbackExplanation);
        string whySummary = BuildWhySummary(
            status?.LatestContributorsJson,
            status?.LatestWhyJson,
            snapshot,
            recentEvents);

        return new PortaVisualState(
            Mode: mode,
            Theme: theme,
            ModeLabel: ToModeLabel(mode),
            ConfidenceLabel: experience.ConfidenceLabel,
            CurrentFocusSessionText: BuildCurrentFocusSessionText(experience),
            CurrentFocusSessionSeconds: experience.CurrentFocusSessionSeconds,
            SnapshotAnalysisReady: experience.SnapshotAnalysisReady,
            FocusStatusText: focusStatus,
            OperatingSentence: operatingSentence,
            OverloadActive: experience.OverloadActive,
            DeepWorkActive: experience.DeepWorkActive,
            ShowDeepWorkStreak: experience.ShowDeepWorkStreak,
            DeepWorkStreakText: experience.DeepWorkStreakText,
            ShowRecoveryBanner: experience.HasRecentRecoveryEvent && mode != PortaVisualMode.Overload,
            RecoveryEventId: experience.HasRecentRecoveryEvent ? experience.RecentRecoveryEventId : 0,
            RecoveryBannerText: experience.HasRecentRecoveryEvent ? experience.RecentRecoveryMessage : string.Empty,
            WhySummary: whySummary);
    }

    // Step 4:
    // The visible focus duration comes from the durable focus episode and only
    // falls back to live presence labels when there is no episode duration yet.
    private static string BuildCurrentFocusSessionText(LocalCurrentExperienceState experience)
    {
        if (experience.CurrentFocusSessionSeconds.HasValue)
        {
            return $"Current focus session: {FormatFocusedTime(experience.CurrentFocusSessionSeconds.Value)}";
        }

        if (experience.LiveSessionFresh && experience.LiveSessionIdle)
        {
            return "Current focus session: 0 sec";
        }

        return $"Current focus session: {NotAvailableText}";
    }

    internal static double? ResolveCurrentFocusSessionSeconds(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        LocalCurrentExperienceState experience = LocalCurrentExperienceStateAdapter.Build(
            status: null,
            snapshot,
            liveSession,
            recentEvents: [],
            nowUtc: DateTimeOffset.UtcNow);
        return experience.CurrentFocusSessionSeconds;
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

    private static PortaVisualMode MapMode(LocalCurrentExperienceMode mode)
    {
        return mode switch
        {
            LocalCurrentExperienceMode.Overload => PortaVisualMode.Overload,
            LocalCurrentExperienceMode.DeepWork => PortaVisualMode.DeepWork,
            LocalCurrentExperienceMode.Normal => PortaVisualMode.Normal,
            _ => PortaVisualMode.Unavailable
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

    private static string BuildFocusStatus(LocalApiCurrentSnapshotResponse snapshot)
    {
        if (snapshot.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            string missing = string.Join("/", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            return $"Focus session paused: missing {missing} signals.";
        }

        if (snapshot.PartialData == true)
        {
            return "Focus session timing is conservative while data is partial.";
        }

        double focusEpisodeSeconds = snapshot.FocusEpisodeSeconds > 0d
            ? snapshot.FocusEpisodeSeconds
            : snapshot.TaskContinuitySeconds;
        if (focusEpisodeSeconds <= 0.5d)
        {
            return "No sustained focus session detected yet.";
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

    private static string FormatFocusedTime(double sustainedAttentionSeconds)
    {
        if (sustainedAttentionSeconds < 60d)
        {
            return $"{Math.Max(0d, sustainedAttentionSeconds):F0} sec";
        }

        double minutes = Math.Floor(sustainedAttentionSeconds / 60d);
        return $"{minutes.ToString("F0", CultureInfo.InvariantCulture)} min";
    }
}
