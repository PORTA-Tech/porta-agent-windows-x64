using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Porta.Agent.Common.Identity;
using Porta.Agent.Common;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core;

namespace Porta.Agent.Service.Fleet;

internal sealed class MachineSummaryMapper
{
    private const string CaptureFailureReasonNeverStarted = "USER_AGENT_NEVER_STARTED";
    private const string CaptureFailureReasonHeartbeatStale = "USER_AGENT_HEARTBEAT_STALE";
    private const string CaptureFailureReasonSessionSwitched = "USER_AGENT_SESSION_SWITCHED";
    private const string CaptureFailureReasonInstanceMismatch = "USER_AGENT_INSTANCE_MISMATCH";
    private const string OwnershipWarningAgentInstanceMismatch = "USER_AGENT_INSTANCE_MISMATCH";
    private const string OwnershipWarningSessionMismatch = "USER_AGENT_SESSION_MISMATCH";
    private const string StartupStateHealthy = "healthy";
    private const string StartupStateIdle = "idle";
    private const string StartupStateNeverStarted = "never_started";
    private const string StartupStateHeartbeatStale = "heartbeat_stale";
    private const string StartupStateSessionSwitched = "session_switched";
    private const string StartupStateServiceSession = "service_session";
    private const string StartupStateUnknownContext = "unknown_context";
    private readonly string _machineName = Environment.MachineName;

    public string ResolveUserName(AgentServiceStatusSnapshot status)
    {
        if (!TryParseSessionId(status.UserAgentSessionId, out int sessionId))
        {
            return "unknown";
        }

        string? userName = QuerySessionInfo(sessionId, WtsInfoClass.WTSUserName);
        if (string.IsNullOrWhiteSpace(userName))
        {
            return "unknown";
        }

        string? domainName = QuerySessionInfo(sessionId, WtsInfoClass.WTSDomainName);
        return string.IsNullOrWhiteSpace(domainName)
            ? userName.Trim()
            : $"{domainName.Trim()}\\{userName.Trim()}";
    }

    public MachineSummaryReportPayload Map(
        AgentServiceStatusSnapshot status,
        AgentLatestStateDetails? latestState,
        LocalIdentityLinkSnapshot identityLink,
        string userName,
        bool backendConnected,
        DateTimeOffset reportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(identityLink);

        double focusEpisodeQuality = ResolveContinuityConfidence(
            latestState?.StateFlagsJson,
            latestState?.Confidence ?? 0d);
        // Canonical focus episode duration. Read the StateFlagsJson key first so the
        // fleet report uses the same source as the local API snapshot; fall back to
        // the pre-computed AgentLatestStateDetails field. SustainedAttentionDurationSec
        // is the old whole-session timer and must not be used as a fallback here.
        double focusEpisodeSeconds = Math.Max(
            0d,
            ReadDoubleFlag(
                latestState?.StateFlagsJson,
                "task_continuity_seconds",
                fallback: latestState?.TaskContinuitySeconds ?? 0d));
        string focusEpisodeState = NormalizeContinuityState(latestState?.ContinuityState);
        bool activityDetected = status.KeyboardSignalActive || status.MouseSignalActive;
        string? baseCaptureFailureReason = DeriveBaseCaptureFailureReason(status);
        bool userSessionConnected = DeriveUserSessionConnected(status, baseCaptureFailureReason);
        double? heartbeatAgeSeconds = ComputeAgeSeconds(status.UserAgentObservedHeartbeatUpdatedAtUtc, reportedAtUtc);
        double? lastAcceptedInputAgeSeconds = ComputeAgeSeconds(status.LastInputEventUtc, reportedAtUtc);
        string startupState = DeriveUserAgentStartupState(status, userSessionConnected, activityDetected, baseCaptureFailureReason);
        string? captureFailureReason = DeriveCaptureFailureReason(status, userSessionConnected, startupState, baseCaptureFailureReason);
        string? accountId = IsLinked(identityLink) ? identityLink.AccountId : null;
        string? orgId = IsLinked(identityLink) ? identityLink.OrgId : null;
        string attentionSegmentKind = ResolveAttentionSegmentKind(latestState?.StateFlagsJson);
        double attentionSegmentSeconds = ResolveAttentionSegmentSeconds(latestState?.StateFlagsJson);
        string attentionSegmentReasonCode = ResolveAttentionSegmentReasonCode(latestState?.StateFlagsJson, attentionSegmentKind);
        string attentionSegmentReasonText = ResolveAttentionSegmentReasonText(
            latestState?.StateFlagsJson,
            attentionSegmentKind,
            attentionSegmentSeconds);
        bool distractionEpisodeActive = ResolveDistractionEpisodeActive(latestState?.StateFlagsJson, attentionSegmentKind);
        double distractionEpisodeSeconds = ResolveDistractionEpisodeSeconds(
            latestState?.StateFlagsJson,
            distractionEpisodeActive,
            attentionSegmentSeconds);
        string focusEpisodeReasonCode = ResolveFocusEpisodeReasonCode(latestState?.StateFlagsJson, focusEpisodeState, attentionSegmentKind);
        string focusEpisodeReasonText = ResolveFocusEpisodeReasonText(
            latestState?.StateFlagsJson,
            focusEpisodeState,
            attentionSegmentKind);
        double pressureLevel = ResolvePressureLevel(latestState?.StateFlagsJson, status.OverloadLikelihood);
        double pressureConfidence = ResolvePressureConfidence(latestState?.StateFlagsJson, latestState?.Confidence ?? 0d);
        string? captureFailureCode = NullIfWhiteSpace(status.UserAgentCaptureFailureCode);
        string? watchdogLaunchFailureCode = NullIfWhiteSpace(status.WatchdogLaunchFailureCode);
        string? selfTestState = NullIfWhiteSpace(status.UserAgentSelfTestState);
        string? selfTestErrorCode = NullIfWhiteSpace(status.UserAgentSelfTestErrorCode);
        string? breakPendingKind = PortaBreakSemantics.ResolveBreakKind(latestState?.BreakPendingKind ?? latestState?.BreakPendingAction);
        string? breakPendingAction = PortaBreakSemantics.ResolveLegacyActionId(latestState?.BreakPendingKind ?? latestState?.BreakPendingAction)
            ?? NullIfWhiteSpace(latestState?.BreakPendingAction);
        bool breakNeeded = breakPendingKind is not null || breakPendingAction is not null;

        // Diagnostic-only: the WTS connect-state of the session the user-agent runs in.
        // "active" => the user is connected and an input desktop exists (so a missing focus
        // channel points to a desktop-attach bug we can fix); "disconnected" => the RDP
        // session is left running with nobody attached, so there is genuinely no foreground
        // to read and the right answer is to represent that time as away. Purely additive;
        // it changes no behavior on any machine.
        string? userAgentSessionConnectState = TryParseSessionId(status.UserAgentSessionId, out int connectStateSessionId)
            ? QuerySessionConnectState(connectStateSessionId)
            : null;

        // Diagnostic-only: whether the latest bucket resolved the foreground app-identity
        // ("resolved") or recorded focus with a blank identity ("blank"). "blank" means focus
        // capture works but the app name couldn't be resolved on this machine — the precise
        // gap behind "only focus missing". Set by BucketAggregators when focus is ingested.
        string? focusAppIdentity = NullIfWhiteSpace(ReadStringFlag(latestState?.StateFlagsJson, "focus_app_identity"));

        return new MachineSummaryReportPayload(
            DeviceId: identityLink.DeviceId,
            AccountId: accountId,
            OrgId: orgId,
            MachineId: _machineName,
            MachineName: _machineName,
            UserName: string.IsNullOrWhiteSpace(userName) ? "unknown" : userName.Trim(),
            AgentVersion: status.AgentVersion,
            SchemaVersion: status.SchemaVersion,
            PolicyVersion: status.PolicyVersion,
            ReportedAtUtc: ToUtcString(reportedAtUtc),
            AgentRunning: status.ServiceRunning,
            UserSessionConnected: userSessionConnected,
            ActivityDetected: activityDetected,
            LastActivityUtc: ToUtcStringOrNull(status.LastInputEventUtc),
            UserAgentHeartbeatUtc: ToUtcStringOrNull(status.UserAgentHeartbeatUpdatedAtUtc),
            UserAgentHeartbeatAgeSeconds: heartbeatAgeSeconds,
            LastAcceptedInputAgeSeconds: lastAcceptedInputAgeSeconds,
            UserAgentStartupSource: NullIfWhiteSpace(status.UserAgentStartupSource),
            UserAgentStartupState: startupState,
            UserAgentLastLaunchReason: NullIfWhiteSpace(status.UserAgentLaunchReason),
            UserAgentHeartbeatWriteFailureCount: status.UserAgentHeartbeatWriteFailureCount,
            CaptureContext: NormalizeCaptureContext(status.CaptureContext),
            KeyboardSignalActive: status.KeyboardSignalActive,
            MouseSignalActive: status.MouseSignalActive,
            OwnershipWarning: NullIfWhiteSpace(status.OwnershipWarning),
            CaptureFailureReason: captureFailureReason,
            LastError: NullIfWhiteSpace(status.LastError),
            BackendConnected: backendConnected || status.BackendConnected,
            LocalApiAuthReady: status.LocalApiAuthReady,
            DbMigrationFailed: status.DbMigrationFailed,
            FocusEpisodeQuality: focusEpisodeQuality,
            FocusEpisodeSeconds: focusEpisodeSeconds,
            FocusEpisodeState: focusEpisodeState,
            FocusEpisodeReasonCode: focusEpisodeReasonCode,
            FocusEpisodeReasonText: focusEpisodeReasonText,
            AttentionSegmentKind: attentionSegmentKind,
            AttentionSegmentSeconds: attentionSegmentSeconds,
            AttentionSegmentReasonCode: attentionSegmentReasonCode,
            AttentionSegmentReasonText: attentionSegmentReasonText,
            DistractionEpisodeActive: distractionEpisodeActive,
            DistractionEpisodeSeconds: distractionEpisodeSeconds,
            TaskContinuityScore: focusEpisodeQuality,
            TaskContinuitySeconds: focusEpisodeSeconds,
            ContinuityState: focusEpisodeState,
            DeepWorkActive: status.DeepWorkActive,
            PressureLevel: pressureLevel,
            PressureConfidence: pressureConfidence,
            OverloadLikelihood: pressureLevel,
            OverloadActive: status.OverloadActive,
            BreakNeeded: breakNeeded,
            BreakPendingAction: breakPendingAction,
            BreakPendingKind: breakPendingKind,
            BreakPendingReasonCode: NullIfWhiteSpace(latestState?.BreakPendingReasonCode),
            CalibrationStage: NormalizeCalibrationStage(status.CalibrationStage),
            TrustTier: NormalizeTrustTier(status.TrustTier),
            ModelConfidence: Math.Clamp(latestState?.Confidence ?? 0d, 0d, 1d),
            SignalQualityScore: latestState?.CurrentQualityScore is double score
                ? Math.Clamp(score, 0d, 1d)
                : null,
            PartialData: latestState?.PartialData == true,
            MissingMajorChannels: latestState?.MissingMajorChannels?
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<string>(),
            UserAgentCaptureFailureCode: captureFailureCode,
            WatchdogLaunchFailureCode: watchdogLaunchFailureCode,
            UserAgentSelfTestState: selfTestState,
            UserAgentSelfTestErrorCode: selfTestErrorCode,
            UserAgentLogTail: UserAgentLogTailReader.ReadTail(),
            WindowsUpdateStatus: WindowsUpdateStatusReader.ReadStatus(),
            UserAgentSessionConnectState: userAgentSessionConnectState,
            FocusAppIdentity: focusAppIdentity);
    }

    private static double ResolvePressureLevel(string? stateFlagsJson, double fallback)
    {
        return Math.Clamp(ReadDoubleFlag(stateFlagsJson, "pressure_level", fallback), 0d, 1d);
    }

    private static double ResolveContinuityConfidence(string? stateFlagsJson, double fallback)
    {
        return Math.Clamp(ReadDoubleFlag(stateFlagsJson, "continuity_confidence", fallback), 0d, 1d);
    }

    private static double ResolvePressureConfidence(string? stateFlagsJson, double fallback)
    {
        double explicitConfidence = ReadDoubleFlag(stateFlagsJson, "pressure_confidence", double.NaN);
        if (double.IsFinite(explicitConfidence))
        {
            return Math.Clamp(explicitConfidence, 0d, 1d);
        }

        return Math.Clamp(ReadDoubleFlag(stateFlagsJson, "model_confidence", fallback), 0d, 1d);
    }

    private static bool IsLinked(LocalIdentityLinkSnapshot snapshot) =>
        string.Equals(snapshot.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(snapshot.AccountId)
        && !string.IsNullOrWhiteSpace(snapshot.OrgId);

    private static string NormalizeCaptureContext(string? captureContext) =>
        captureContext?.Trim() switch
        {
            "UserSession" => "UserSession",
            "ServiceSession" => "ServiceSession",
            _ => "Unknown"
        };

    private static string NormalizeContinuityState(string? state) =>
        state?.Trim().ToLowerInvariant() switch
        {
            "keep" => "keep",
            "pause" => "pause",
            "decay" => "decay",
            _ => "reset"
        };

    private static string NormalizeCalibrationStage(string? stage) =>
        stage?.Trim().ToLowerInvariant() switch
        {
            "provisional" => "provisional",
            "calibrated" => "calibrated",
            _ => "cold"
        };

    private static string NormalizeTrustTier(string? trustTier) =>
        trustTier?.Trim().ToLowerInvariant() switch
        {
            "medium" => "medium",
            "high" => "high",
            _ => "low"
        };

    private static string ResolveAttentionSegmentKind(string? stateFlagsJson) =>
        NormalizeAttentionSegmentKind(ReadStringFlag(stateFlagsJson, "attention_segment_kind"));

    private static double ResolveAttentionSegmentSeconds(string? stateFlagsJson) =>
        Math.Max(0d, ReadDoubleFlag(stateFlagsJson, "attention_segment_seconds"));

    private static string ResolveAttentionSegmentReasonCode(string? stateFlagsJson, string attentionSegmentKind)
    {
        string explicitCode = NormalizeReasonCode(ReadStringFlag(stateFlagsJson, "attention_segment_reason_code"));
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            return explicitCode;
        }

        return attentionSegmentKind switch
        {
            "away" => "away",
            "context_excursion" => "context_excursion",
            "off_task_distraction" => "outside_focus_context",
            "fragmentation_distraction" => "fragmentation_distraction",
            _ => "steady_focus"
        };
    }

    private static string ResolveAttentionSegmentReasonText(
        string? stateFlagsJson,
        string attentionSegmentKind,
        double attentionSegmentSeconds)
    {
        string? explicitText = ReadStringFlag(stateFlagsJson, "attention_segment_reason_text");
        if (!string.IsNullOrWhiteSpace(explicitText))
        {
            return explicitText.Trim();
        }

        return attentionSegmentKind switch
        {
            "away" => $"Away for {FormatEpisodeDuration(attentionSegmentSeconds)}.",
            "context_excursion" => $"Outside focus context for {FormatEpisodeDuration(attentionSegmentSeconds)}.",
            "off_task_distraction" => $"Stayed outside focus context for {FormatEpisodeDuration(attentionSegmentSeconds)}.",
            "fragmentation_distraction" => $"Rapid switching / fragmentation for {FormatEpisodeDuration(attentionSegmentSeconds)}.",
            _ => "Steady focus episode."
        };
    }

    private static bool ResolveDistractionEpisodeActive(string? stateFlagsJson, string attentionSegmentKind)
    {
        if (TryReadBoolFlag(stateFlagsJson, "distraction_episode_active", out bool active))
        {
            return active;
        }

        return string.Equals(attentionSegmentKind, "off_task_distraction", StringComparison.Ordinal)
            || string.Equals(attentionSegmentKind, "fragmentation_distraction", StringComparison.Ordinal);
    }

    private static double ResolveDistractionEpisodeSeconds(
        string? stateFlagsJson,
        bool distractionEpisodeActive,
        double attentionSegmentSeconds)
    {
        double flaggedSeconds = Math.Max(0d, ReadDoubleFlag(stateFlagsJson, "distraction_episode_seconds"));
        if (flaggedSeconds > 0d)
        {
            return flaggedSeconds;
        }

        return distractionEpisodeActive ? attentionSegmentSeconds : 0d;
    }

    private static string ResolveFocusEpisodeReasonCode(string? stateFlagsJson, string focusEpisodeState, string attentionSegmentKind)
    {
        string explicitCode = NormalizeReasonCode(ReadStringFlag(stateFlagsJson, "focus_episode_reason_code"));
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            return explicitCode;
        }

        return (focusEpisodeState, attentionSegmentKind) switch
        {
            ("reset", "away") => "long_break",
            ("reset", "off_task_distraction") or ("reset", "context_excursion") => "major_context_shift",
            ("reset", "fragmentation_distraction") => "instability_reset",
            ("reset", _) => "focus_episode_reset",
            ("pause", "away") => "break_pause",
            ("pause", "context_excursion") => "context_excursion",
            ("pause", "off_task_distraction") => "outside_focus_context",
            ("pause", "fragmentation_distraction") => "focus_unstable",
            ("pause", _) => "focus_episode_paused",
            ("decay", "away") => "extended_break_decay",
            ("decay", "context_excursion") => "context_excursion_decay",
            ("decay", "off_task_distraction") => "outside_focus_context_decay",
            ("decay", "fragmentation_distraction") => "focus_instability",
            ("decay", _) => "focus_episode_decay",
            _ => "focus_episode_active"
        };
    }

    private static string ResolveFocusEpisodeReasonText(string? stateFlagsJson, string focusEpisodeState, string attentionSegmentKind)
    {
        string? explicitText = ReadStringFlag(stateFlagsJson, "focus_episode_reason_text");
        if (!string.IsNullOrWhiteSpace(explicitText))
        {
            return explicitText.Trim();
        }

        return (focusEpisodeState, attentionSegmentKind) switch
        {
            ("reset", "away") => "Long break ended the focus episode.",
            ("reset", "off_task_distraction") or ("reset", "context_excursion") => "Major context shift ended the focus episode.",
            ("reset", "fragmentation_distraction") => "Sustained instability ended the focus episode.",
            ("reset", _) => "Focus episode ended.",
            ("pause", "away") => "Focus episode is on hold during a break.",
            ("pause", "context_excursion") => "Focus episode is preserved during a context excursion.",
            ("pause", "off_task_distraction") => "Focus episode is on hold while away from the focus context.",
            ("pause", "fragmentation_distraction") => "Focus episode is paused due to switching instability.",
            ("pause", _) => "Focus episode is paused.",
            ("decay", "away") => "Focus episode is fading during an extended break.",
            ("decay", "context_excursion") => "Focus episode is decaying during an extended context excursion.",
            ("decay", "off_task_distraction") => "Focus episode is decaying after extended absence from the focus context.",
            ("decay", "fragmentation_distraction") => "Focus episode is decaying due to sustained switching instability.",
            ("decay", _) => "Focus episode is fading.",
            _ => "Focus episode active."
        };
    }

    private static string NormalizeAttentionSegmentKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "context_excursion" => "context_excursion",
            "off_task_distraction" => "off_task_distraction",
            "fragmentation_distraction" => "fragmentation_distraction",
            "away" => "away",
            _ => "focus"
        };

    private static string NormalizeReasonCode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('-', '_').ToLowerInvariant();

    private static string FormatEpisodeDuration(double seconds)
    {
        int totalSeconds = Math.Max(0, (int)Math.Round(seconds, MidpointRounding.AwayFromZero));
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int secs = totalSeconds % 60;
        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        return secs > 0 && minutes < 3 ? $"{minutes}m {secs}s" : $"{minutes}m";
    }

    private static bool TryReadBoolFlag(string? json, string name, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(name, out JsonElement node))
            {
                return false;
            }

            switch (node.ValueKind)
            {
                case JsonValueKind.True:
                    value = true;
                    return true;
                case JsonValueKind.False:
                    value = false;
                    return true;
                case JsonValueKind.Number when node.TryGetInt32(out int numeric):
                    value = numeric != 0;
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static double ReadDoubleFlag(string? json, string name, double fallback = 0d)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out JsonElement node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetDouble(out double numeric))
            {
                return numeric;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string? ReadStringFlag(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out JsonElement node)
                && node.ValueKind == JsonValueKind.String)
            {
                return node.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool DeriveUserSessionConnected(AgentServiceStatusSnapshot status, string? captureFailureReason)
    {
        if (!string.Equals(status.CaptureContext, UserAgentCaptureContexts.UserSession, StringComparison.Ordinal)
            || !status.UserAgentHeartbeatUpdatedAtUtc.HasValue)
        {
            return false;
        }

        return captureFailureReason switch
        {
            CaptureFailureReasonHeartbeatStale => false,
            CaptureFailureReasonSessionSwitched => false,
            CaptureFailureReasonInstanceMismatch => false,
            _ => true
        };
    }

    private static string DeriveUserAgentStartupState(
        AgentServiceStatusSnapshot status,
        bool userSessionConnected,
        bool activityDetected,
        string? captureFailureReason)
    {
        if (string.Equals(captureFailureReason, CaptureFailureReasonSessionSwitched, StringComparison.Ordinal)
            || string.Equals(captureFailureReason, CaptureFailureReasonInstanceMismatch, StringComparison.Ordinal))
        {
            return StartupStateSessionSwitched;
        }

        if (userSessionConnected)
        {
            return activityDetected ? StartupStateHealthy : StartupStateIdle;
        }

        string observedCaptureContext = NormalizeCaptureContext(status.UserAgentObservedCaptureContext);
        if (string.Equals(observedCaptureContext, UserAgentCaptureContexts.ServiceSession, StringComparison.Ordinal))
        {
            return StartupStateServiceSession;
        }

        if (!status.UserAgentObservedHeartbeatUpdatedAtUtc.HasValue)
        {
            return StartupStateNeverStarted;
        }

        if (IsObservedSessionDifferentFromActiveSession(status.UserAgentObservedSessionId))
        {
            return StartupStateSessionSwitched;
        }

        if (string.Equals(observedCaptureContext, UserAgentCaptureContexts.Unknown, StringComparison.Ordinal))
        {
            return StartupStateUnknownContext;
        }

        return StartupStateHeartbeatStale;
    }

    private static string? DeriveBaseCaptureFailureReason(AgentServiceStatusSnapshot status)
    {
        string? statusReason = NullIfWhiteSpace(status.CaptureFailureReason);
        if (!string.IsNullOrWhiteSpace(statusReason))
        {
            return statusReason;
        }

        if (string.Equals(status.OwnershipWarning, OwnershipWarningSessionMismatch, StringComparison.Ordinal))
        {
            return CaptureFailureReasonSessionSwitched;
        }

        if (string.Equals(status.OwnershipWarning, OwnershipWarningAgentInstanceMismatch, StringComparison.Ordinal))
        {
            return CaptureFailureReasonInstanceMismatch;
        }

        return null;
    }

    private static string? DeriveCaptureFailureReason(
        AgentServiceStatusSnapshot status,
        bool userSessionConnected,
        string startupState,
        string? fallbackReason)
    {
        if (userSessionConnected)
        {
            return fallbackReason;
        }

        return startupState switch
        {
            StartupStateNeverStarted => CaptureFailureReasonNeverStarted,
            StartupStateHeartbeatStale => CaptureFailureReasonHeartbeatStale,
            StartupStateSessionSwitched => CaptureFailureReasonSessionSwitched,
            StartupStateServiceSession => "CAPTURE_CONTEXT_SERVICE_SESSION",
            StartupStateUnknownContext => fallbackReason ?? "CAPTURE_CONTEXT_UNKNOWN",
            _ => fallbackReason
        };
    }

    private static bool IsObservedSessionDifferentFromActiveSession(string? observedSessionId)
    {
        if (!TryParseSessionId(observedSessionId, out int observedSessionNumeric))
        {
            return false;
        }

        int activeSessionId = WTSGetActiveConsoleSessionId();
        return activeSessionId >= 0 && activeSessionId != observedSessionNumeric;
    }

    private static double? ComputeAgeSeconds(DateTimeOffset? timestampUtc, DateTimeOffset referenceUtc)
    {
        if (!timestampUtc.HasValue)
        {
            return null;
        }

        double ageSeconds = (referenceUtc.ToUniversalTime() - timestampUtc.Value.ToUniversalTime()).TotalSeconds;
        return ageSeconds < 0d ? 0d : Math.Round(ageSeconds, 3);
    }

    private static string ToUtcString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string? ToUtcStringOrNull(DateTimeOffset? value) =>
        value.HasValue ? ToUtcString(value.Value) : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseSessionId(string? sessionId, out int parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        const string prefix = "session-";
        if (!sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(sessionId[prefix.Length..], out parsed) && parsed >= 0;
    }

    private static string? QuerySessionInfo(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out IntPtr buffer, out int bytesReturned)
            || buffer == IntPtr.Zero
            || bytesReturned <= 1)
        {
            return null;
        }

        try
        {
            string? value = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // Reads WTS_INFO_CLASS.WTSConnectState (an int / WTS_CONNECTSTATE_CLASS) for a session and
    // maps it to a stable lowercase token. Diagnostic-only; never throws into the caller.
    private static string? QuerySessionConnectState(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.WTSConnectState, out IntPtr buffer, out int bytesReturned)
            || buffer == IntPtr.Zero
            || bytesReturned < sizeof(int))
        {
            return null;
        }

        try
        {
            int state = Marshal.ReadInt32(buffer);
            return state switch
            {
                0 => "active",
                1 => "connected",
                2 => "connect_query",
                3 => "shadow",
                4 => "disconnected",
                5 => "idle",
                6 => "listen",
                7 => "reset",
                8 => "down",
                9 => "init",
                _ => "unknown_" + state.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("Wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        WtsInfoClass wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private enum WtsInfoClass
    {
        WTSInitialProgram,
        WTSApplicationName,
        WTSWorkingDirectory,
        WTSOEMId,
        WTSSessionId,
        WTSUserName,
        WTSWinStationName,
        WTSDomainName,
        WTSConnectState
    }
}
