using System;
using System.Linq;
using System.Runtime.InteropServices;
using Porta.Agent.Common.Identity;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core;

namespace Porta.Agent.Service.Fleet;

internal sealed class MachineSummaryMapper
{
    private const string CaptureFailureReasonNeverStarted = "USER_AGENT_NEVER_STARTED";
    private const string CaptureFailureReasonHeartbeatStale = "USER_AGENT_HEARTBEAT_STALE";
    private const string CaptureFailureReasonSessionSwitched = "USER_AGENT_SESSION_SWITCHED";
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

        double taskContinuityScore = Math.Clamp(latestState?.TaskContinuityScore ?? 0d, 0d, 1d);
        double taskContinuitySeconds = Math.Max(
            0d,
            latestState?.TaskContinuitySeconds
            ?? latestState?.SustainedAttentionDurationSec
            ?? 0d);
        string continuityState = NormalizeContinuityState(latestState?.ContinuityState);
        bool activityDetected = status.KeyboardSignalActive || status.MouseSignalActive;
        bool userSessionConnected =
            string.Equals(status.CaptureContext, "UserSession", StringComparison.Ordinal)
            && status.UserAgentHeartbeatUpdatedAtUtc.HasValue;
        double? heartbeatAgeSeconds = ComputeAgeSeconds(status.UserAgentObservedHeartbeatUpdatedAtUtc, reportedAtUtc);
        double? lastAcceptedInputAgeSeconds = ComputeAgeSeconds(status.LastInputEventUtc, reportedAtUtc);
        string startupState = DeriveUserAgentStartupState(status, userSessionConnected, activityDetected);
        string? captureFailureReason = DeriveCaptureFailureReason(status, userSessionConnected, startupState);
        string? accountId = IsLinked(identityLink) ? identityLink.AccountId : null;
        string? orgId = IsLinked(identityLink) ? identityLink.OrgId : null;

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
            TaskContinuityScore: taskContinuityScore,
            TaskContinuitySeconds: taskContinuitySeconds,
            ContinuityState: continuityState,
            DeepWorkActive: status.DeepWorkActive,
            OverloadLikelihood: Math.Clamp(status.OverloadLikelihood, 0d, 1d),
            OverloadActive: status.OverloadActive,
            BreakNeeded: !string.IsNullOrWhiteSpace(latestState?.BreakPendingAction),
            BreakPendingAction: NullIfWhiteSpace(latestState?.BreakPendingAction),
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
                ?? Array.Empty<string>());
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

    private static string DeriveUserAgentStartupState(
        AgentServiceStatusSnapshot status,
        bool userSessionConnected,
        bool activityDetected)
    {
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

    private static string? DeriveCaptureFailureReason(
        AgentServiceStatusSnapshot status,
        bool userSessionConnected,
        string startupState)
    {
        if (userSessionConnected)
        {
            return null;
        }

        return startupState switch
        {
            StartupStateNeverStarted => CaptureFailureReasonNeverStarted,
            StartupStateHeartbeatStale => CaptureFailureReasonHeartbeatStale,
            StartupStateSessionSwitched => CaptureFailureReasonSessionSwitched,
            StartupStateServiceSession => "CAPTURE_CONTEXT_SERVICE_SESSION",
            StartupStateUnknownContext => NullIfWhiteSpace(status.CaptureFailureReason) ?? "CAPTURE_CONTEXT_UNKNOWN",
            _ => NullIfWhiteSpace(status.CaptureFailureReason)
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
        WTSDomainName
    }
}
