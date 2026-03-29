namespace Porta.UserAgent.Interventions;

using System.Globalization;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

internal static class UserDesktopWidgetStateBuilder
{
    private const string CalibrationStageCold = "cold";
    private const string CalibrationStageProvisional = "provisional";
    private const string CalibrationStageCalibrated = "calibrated";
    private const string TrustTierLow = "low";
    private const string TrustTierMedium = "medium";
    private const string TrustTierHigh = "high";

    public static UserDesktopWidgetState Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserAgentVisualSignals signals,
        UserCommunicationToast? degradedToast,
        IReadOnlyList<LocalApiEventResponse>? recentEvents = null,
        LocalApiTimelineResponse? timeline = null,
        LocalApiSettingsResponse? settings = null,
        DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        IReadOnlyList<LocalApiEventResponse> events = recentEvents ?? [];
        bool paused = status?.Paused == true;
        bool limited = paused
            ? false
            : degradedToast != null
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
            StatusText: BuildStatusText(status, snapshot, signals, degradedToast, mode, settings),
            PrimaryMetricText: BuildPrimaryMetric(status, snapshot, mode),
            SecondaryMetricText: BuildSecondaryMetric(status, snapshot, mode),
            FooterText: BuildFooterText(status, snapshot, settings),
            NotificationsSuppressed: status?.NotificationsSuppressed == true || snapshot?.NotificationsSuppressed == true,
            BrightnessAdjusted: status?.BrightnessAdjusted == true || snapshot?.BrightnessAdjusted == true,
            CanPause: status?.ServiceRunning == true && status.ConsentActive && !paused,
            CanResume: paused,
            TertiaryMetricText: BuildTertiaryMetric(status, snapshot, events, timeline, mode, now),
            QuaternaryMetricText: BuildQuaternaryMetric(status, snapshot, mode));
    }

    private static string BuildTitle(UserDesktopWidgetMode mode)
    {
        return mode switch
        {
            UserDesktopWidgetMode.DeepWork => "Deep Work",
            UserDesktopWidgetMode.Overload => "Overload",
            UserDesktopWidgetMode.Limited => "Ari Limited",
            UserDesktopWidgetMode.Paused => "Paused",
            _ => "Monitoring"
        };
    }

    private static string BuildStatusText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserAgentVisualSignals signals,
        UserCommunicationToast? degradedToast,
        UserDesktopWidgetMode mode,
        LocalApiSettingsResponse? settings)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            if (status?.PauseUntilUtc is DateTimeOffset untilUtc)
            {
                return $"Ari is paused until {untilUtc.ToLocalTime():h:mm tt}.";
            }

            return "Ari is paused.";
        }

        if (degradedToast != null)
        {
            return degradedToast.Message;
        }

        if (status == null || snapshot == null)
        {
            return "Waiting for the core service.";
        }

        if (!status.ServiceRunning)
        {
            return "Ari cannot regulate because the core service is unavailable.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "Ari is waiting for local API access.";
        }

        if (!status.ConsentActive)
        {
            return "Ari is waiting for consent before it can guide this session.";
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return "Ari is reconnecting to your live Windows session.";
        }

        if (!status.BackendConnected)
        {
            return "Ari is running locally while backend sync reconnects.";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.BreakPendingAction))
        {
            return snapshot.BreakDeliveryBoundaryReached == true
                ? $"{HumanizeAction(snapshot.BreakPendingAction)} ready."
                : $"{HumanizeAction(snapshot.BreakPendingAction)} pending.";
        }

        if (mode == UserDesktopWidgetMode.DeepWork)
        {
            return settings?.FocusModeEnabled == true
                ? "Deep work is active with focus mode enabled."
                : "Deep work is active.";
        }

        if (mode == UserDesktopWidgetMode.Overload)
        {
            return "Ari is reducing interruptions while overload stays elevated.";
        }

        string tooltipText = StripTooltipPrefix(signals.TooltipText);
        if (!string.IsNullOrWhiteSpace(tooltipText)
            && !tooltipText.Equals("Watching activity", StringComparison.OrdinalIgnoreCase)
            && !tooltipText.Equals("Waiting for service data", StringComparison.OrdinalIgnoreCase))
        {
            return tooltipText + ".";
        }

        return settings?.FocusModeEnabled == true
            ? "Ari is monitoring your session with focus mode enabled."
            : "Ari is monitoring your session.";
    }

    private static string BuildPrimaryMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Tracking paused";
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

    private static string BuildSecondaryMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            if (status?.PauseUntilUtc is DateTimeOffset untilUtc)
            {
                return $"Until {untilUtc.ToLocalTime():h:mm tt}";
            }

            return status?.ServiceRunning == true ? "Resume anytime" : "Waiting for service";
        }

        double? overload = snapshot?.OverloadLikelihood;
        if (!overload.HasValue && status != null)
        {
            overload = status.OverloadLikelihood;
        }

        return overload.HasValue
            ? $"Overload {FormatPercent(overload.Value)}"
            : "Overload n/a";
    }

    private static string BuildTertiaryMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        LocalApiTimelineResponse? timeline,
        UserDesktopWidgetMode mode,
        DateTimeOffset nowUtc)
    {
        if (mode == UserDesktopWidgetMode.Paused)
        {
            return "Break now";
        }

        DateTimeOffset? explicitBoundaryUtc = TryResolveExplicitBreakBoundary(recentEvents);
        DateTimeOffset? timelineBoundaryUtc = TryResolveTimelineBreakBoundary(snapshot, timeline);
        DateTimeOffset? resolvedBoundaryUtc = Max(explicitBoundaryUtc, timelineBoundaryUtc);

        if (resolvedBoundaryUtc.HasValue)
        {
            TimeSpan elapsed = nowUtc - resolvedBoundaryUtc.Value;
            if (elapsed <= TimeSpan.FromMinutes(1))
            {
                return "Break now";
            }

            return $"Break {FormatCompactDuration(elapsed)} ago";
        }

        if (timeline != null)
        {
            TimeSpan coverage = nowUtc - timeline.FromUtc;
            return $"No break {FormatCompactDuration(coverage)}";
        }

        return "Break n/a";
    }

    private static string BuildQuaternaryMetric(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        UserDesktopWidgetMode mode)
    {
        string calibrationStage = NormalizeCalibrationStage(
            snapshot?.CalibrationStage ?? status?.CalibrationStage,
            snapshot?.BaselineReady,
            snapshot?.ColdStart);
        string trustTier = NormalizeTrustTier(
            snapshot?.TrustTier ?? status?.TrustTier,
            snapshot?.ModelConfidence ?? snapshot?.Confidence);

        if (mode == UserDesktopWidgetMode.Paused)
        {
            return $"{BuildServiceMetric(status)}";
        }

        return $"{ToCalibrationStageLabel(calibrationStage)} · {ToTrustLabel(trustTier)} trust";
    }

    private static string BuildFooterText(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiSettingsResponse? settings)
    {
        List<string> segments = new()
        {
            BuildServiceFooter(status)
        };

        string settingsSummary = BuildSettingsSummary(settings);
        if (!string.IsNullOrWhiteSpace(settingsSummary))
        {
            segments.Add(settingsSummary);
        }

        if (snapshot?.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            segments.Add($"Missing {string.Join("/", snapshot.MissingMajorChannels.Select(ToDisplayChannelName))}");
        }

        return string.Join(" · ", segments.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildServiceMetric(LocalApiStatusResponse? status)
    {
        return BuildServiceFooter(status) switch
        {
            "Service healthy" => "Health healthy",
            "" => "Health n/a",
            string text => text
        };
    }

    private static string BuildServiceFooter(LocalApiStatusResponse? status)
    {
        if (status == null)
        {
            return "Service unknown";
        }

        if (!status.ServiceRunning)
        {
            return "Service offline";
        }

        if (!status.LocalApiAuthReady)
        {
            return "API auth error";
        }

        if (status.DbMigrationFailed)
        {
            return "DB migration blocked";
        }

        if (!status.BackendConnected)
        {
            return "Backend disconnected";
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return "Capture waiting for user session";
        }

        if (!status.KeyboardSignalActive || !status.MouseSignalActive)
        {
            return "Capture partial";
        }

        return "Service healthy";
    }

    private static string BuildSettingsSummary(LocalApiSettingsResponse? settings)
    {
        if (settings == null)
        {
            return string.Empty;
        }

        if (settings.BaselineRecalibrationActive)
        {
            return "Recalibrating baseline";
        }

        List<string> segments = new();
        if (settings.FocusModeEnabled)
        {
            segments.Add("Focus mode on");
        }

        if (settings.NotificationReductionEnabled)
        {
            segments.Add("Quiet guidance");
        }

        if (settings.TemporaryAppRestrictionEnabled)
        {
            segments.Add("Protective blocks on");
        }

        return string.Join(", ", segments.Take(2));
    }

    private static DateTimeOffset? TryResolveExplicitBreakBoundary(IReadOnlyList<LocalApiEventResponse> recentEvents)
    {
        return recentEvents
            .OrderByDescending(static evt => evt.TsUtc)
            .FirstOrDefault(static evt =>
                string.Equals(evt.Action, "resume", StringComparison.OrdinalIgnoreCase))
            ?.TsUtc;
    }

    private static DateTimeOffset? TryResolveTimelineBreakBoundary(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiTimelineResponse? timeline)
    {
        if (snapshot == null || timeline?.Snapshots is not { Count: > 0 })
        {
            return null;
        }

        List<LocalApiCurrentSnapshotResponse> orderedSnapshots = timeline.Snapshots
            .OrderBy(static item => item.TimestampUtc)
            .ToList();
        double resetFloorSeconds = Math.Max(30d, timeline.BucketSeconds * 2d);
        DateTimeOffset streakStartUtc = snapshot.TimestampUtc - TimeSpan.FromSeconds(Math.Max(0d, snapshot.SustainedAttentionDurationSec));

        LocalApiCurrentSnapshotResponse? streakStartSnapshot = orderedSnapshots
            .LastOrDefault(item =>
                item.TimestampUtc <= streakStartUtc.AddSeconds(resetFloorSeconds)
                && item.SustainedAttentionDurationSec <= resetFloorSeconds);
        if (streakStartSnapshot != null)
        {
            return streakStartSnapshot.TimestampUtc;
        }

        for (int index = orderedSnapshots.Count - 1; index > 0; index--)
        {
            LocalApiCurrentSnapshotResponse previous = orderedSnapshots[index - 1];
            LocalApiCurrentSnapshotResponse current = orderedSnapshots[index];
            if (previous.SustainedAttentionDurationSec >= TimeSpan.FromMinutes(5).TotalSeconds
                && current.SustainedAttentionDurationSec <= resetFloorSeconds)
            {
                return current.TimestampUtc;
            }
        }

        return streakStartUtc > timeline.FromUtc.AddSeconds(resetFloorSeconds)
            ? streakStartUtc
            : null;
    }

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset? second)
    {
        return (first, second) switch
        {
            ({ } a, { } b) => a >= b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null
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

    private static string NormalizeTrustTier(string? trustTier, double? confidence)
    {
        if (!string.IsNullOrWhiteSpace(trustTier))
        {
            return trustTier.Trim().ToLowerInvariant() switch
            {
                TrustTierHigh => TrustTierHigh,
                TrustTierMedium => TrustTierMedium,
                _ => TrustTierLow
            };
        }

        if (!confidence.HasValue)
        {
            return TrustTierLow;
        }

        if (confidence.Value >= 0.75d)
        {
            return TrustTierHigh;
        }

        return confidence.Value >= 0.50d ? TrustTierMedium : TrustTierLow;
    }

    private static string ToCalibrationStageLabel(string calibrationStage)
    {
        return calibrationStage switch
        {
            CalibrationStageCalibrated => "Ready",
            CalibrationStageProvisional => "Provisional",
            _ => "Bootstrapping"
        };
    }

    private static string ToTrustLabel(string trustTier)
    {
        return trustTier switch
        {
            TrustTierHigh => "High",
            TrustTierMedium => "Medium",
            _ => "Low"
        };
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

    private static string HumanizeAction(string? action)
    {
        return action?.Trim().ToLowerInvariant() switch
        {
            "break_suggestion" => "Break suggestion",
            "stretch_break_suggestion" => "Stretch break",
            "focus_lock_in_suggestion" => "Focus lock-in",
            _ => "Suggestion"
        };
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

    private static string StripTooltipPrefix(string? tooltipText)
    {
        if (string.IsNullOrWhiteSpace(tooltipText))
        {
            return string.Empty;
        }

        string trimmed = tooltipText.Trim();
        const string prefix = "PORTA | ";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[prefix.Length..];
        }

        return trimmed.Trim().TrimEnd('.');
    }
}
