using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.Identity;
using Porta.Agent.Core;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

public sealed class MachineSummaryMapperTests
{
    [Fact]
    public void Map_UsesStatusAndLatestStateTruthWithoutInventingFields()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: false,
            FocusStrength: 0.82,
            OverloadLikelihood: 0.41,
            LastAction: "break_suggestion",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddSeconds(-5),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 200,
            UserAgentSessionId: "session-2",
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: "session-2",
            UserAgentStartupSource: "run-key",
            UserAgentLaunchReason: "run-key",
            UserAgentHeartbeatWriteFailureCount: 0,
            KeyboardSignalActive: true,
            MouseSignalActive: false,
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            LastInputAgentInstanceId: "ua-1",
            LastInputSessionId: "session-2",
            OwnershipWarning: null,
            CaptureFailureReason: null,
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: true,
            ColdStart: false,
            CalibrationStage: "calibrated",
            SystemActionsEnabled: true,
            TrustTier: "high",
            OverloadActive: false,
            DeepWorkActive: true,
            DeepWorkStreakSeconds: 1200,
            VisualMode: "deep_work",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc);

        var latestState = new AgentLatestStateDetails(
            TimestampUtc: nowUtc,
            OverloadLikelihood: 0.41,
            Confidence: 0.86,
            SustainedAttentionDurationSec: 1800,
            IdleFragmentationIndex: 0.12,
            FocusAppEntropy: 0.22,
            OverloadActive: false,
            StateFlagsJson: "{\"schema_ver\":1}",
            ContributorsJson: "{\"schema_ver\":1}",
            CalibrationStage: "calibrated",
            TrustTier: "high",
            CurrentQualityScore: 0.92,
            PartialData: false,
            MissingMajorChannels: Array.Empty<string>(),
            BreakPendingAction: "stretch_break_suggestion",
            BreakPendingReasonCode: "delivery_boundary_wait",
            TaskContinuityScore: 0.88,
            TaskContinuitySeconds: 1800,
            ContinuityState: "keep");
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Linked,
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            UpdatedAtUtc: nowUtc,
            LinkedAtUtc: nowUtc.AddMinutes(-5));

        MachineSummaryReportPayload payload = mapper.Map(status, latestState, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.Equal("6f390ec2-31b7-4f49-9c11-e508d477b633", payload.DeviceId);
        Assert.Equal("2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b", payload.AccountId);
        Assert.Equal("6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1", payload.OrgId);
        Assert.Equal(Environment.MachineName, payload.MachineId);
        Assert.Equal("DOMAIN\\alice", payload.UserName);
        Assert.True(payload.UserSessionConnected);
        Assert.True(payload.ActivityDetected);
        Assert.Equal(2d, payload.UserAgentHeartbeatAgeSeconds);
        Assert.Equal(1d, payload.LastAcceptedInputAgeSeconds);
        Assert.Equal("run-key", payload.UserAgentStartupSource);
        Assert.Equal("healthy", payload.UserAgentStartupState);
        Assert.Equal("run-key", payload.UserAgentLastLaunchReason);
        Assert.Equal(0, payload.UserAgentHeartbeatWriteFailureCount);
        Assert.Equal(0.88, payload.TaskContinuityScore);
        Assert.Equal(1800, payload.TaskContinuitySeconds);
        Assert.Equal("keep", payload.ContinuityState);
        Assert.Equal(0.88, payload.FocusEpisodeQuality);
        Assert.Equal(1800, payload.FocusEpisodeSeconds);
        Assert.Equal("keep", payload.FocusEpisodeState);
        Assert.True(payload.BreakNeeded);
        Assert.Equal("stretch_break_suggestion", payload.BreakPendingAction);
        Assert.Equal("calibrated", payload.CalibrationStage);
        Assert.Equal("high", payload.TrustTier);
        Assert.Equal(0.86, payload.ModelConfidence);
        Assert.Equal(0.92, payload.SignalQualityScore);
        Assert.False(payload.PartialData);
        Assert.Empty(payload.MissingMajorChannels);
        Assert.True(payload.BackendConnected);
    }

    [Fact]
    public void Map_KeepsUnlinkedDevicesOperationalWithoutIdentityRefs()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: true,
            FocusStrength: 0.3,
            OverloadLikelihood: 0.2,
            LastAction: "monitor_only",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddSeconds(-5),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 200,
            UserAgentSessionId: "session-2",
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: "session-2",
            UserAgentStartupSource: "run-key",
            UserAgentLaunchReason: "run-key",
            UserAgentHeartbeatWriteFailureCount: 1,
            KeyboardSignalActive: false,
            MouseSignalActive: true,
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            LastInputAgentInstanceId: "ua-1",
            LastInputSessionId: "session-2",
            OwnershipWarning: null,
            CaptureFailureReason: null,
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: false,
            ColdStart: true,
            CalibrationStage: "cold",
            SystemActionsEnabled: false,
            TrustTier: "low",
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "normal",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc);
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Unlinked,
            AccountId: null,
            OrgId: null,
            UpdatedAtUtc: nowUtc);

        MachineSummaryReportPayload payload = mapper.Map(status, latestState: null, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.Equal("6f390ec2-31b7-4f49-9c11-e508d477b633", payload.DeviceId);
        Assert.Null(payload.AccountId);
        Assert.Null(payload.OrgId);
        Assert.Equal("DOMAIN\\alice", payload.UserName);
        Assert.True(payload.ActivityDetected);
        Assert.Equal("healthy", payload.UserAgentStartupState);
        Assert.Equal(1, payload.UserAgentHeartbeatWriteFailureCount);
    }

    [Fact]
    public void Map_DerivesSessionSwitchedDiagnostics_WhenObservedHeartbeatBelongsToDifferentSession()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: true,
            FocusStrength: 0.3,
            OverloadLikelihood: 0.2,
            LastAction: "monitor_only",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddMinutes(-2),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "Unknown",
            UserAgentHeartbeatUpdatedAtUtc: null,
            UserAgentInstanceId: null,
            UserAgentProcessId: null,
            UserAgentSessionId: null,
            KeyboardSignalActive: false,
            MouseSignalActive: false,
            LastInputEventUtc: null,
            LastInputAgentInstanceId: null,
            LastInputSessionId: null,
            OwnershipWarning: null,
            CaptureFailureReason: "CAPTURE_CONTEXT_UNKNOWN",
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: false,
            ColdStart: true,
            CalibrationStage: "cold",
            SystemActionsEnabled: false,
            TrustTier: "low",
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "normal",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc,
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-80),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: "session-9999",
            UserAgentStartupSource: "control-center-recovery",
            UserAgentLaunchReason: "stale_heartbeat",
            UserAgentHeartbeatWriteFailureCount: 3);
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Linked,
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            UpdatedAtUtc: nowUtc,
            LinkedAtUtc: nowUtc.AddMinutes(-5));

        MachineSummaryReportPayload payload = mapper.Map(status, latestState: null, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.False(payload.UserSessionConnected);
        Assert.False(payload.ActivityDetected);
        Assert.Equal(80d, payload.UserAgentHeartbeatAgeSeconds);
        Assert.Null(payload.LastAcceptedInputAgeSeconds);
        Assert.Equal("control-center-recovery", payload.UserAgentStartupSource);
        Assert.Equal("session_switched", payload.UserAgentStartupState);
        Assert.Equal("stale_heartbeat", payload.UserAgentLastLaunchReason);
        Assert.Equal(3, payload.UserAgentHeartbeatWriteFailureCount);
        Assert.Equal("USER_AGENT_SESSION_SWITCHED", payload.CaptureFailureReason);
    }

    [Fact]
    public void Map_TreatsFreshOwnershipMismatchAsRecoverableSessionFailure()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Degraded,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: true,
            FocusStrength: 0.3,
            OverloadLikelihood: 0.2,
            LastAction: "monitor_only",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddMinutes(-2),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-5),
            UserAgentInstanceId: "agent-9",
            UserAgentProcessId: 5002,
            UserAgentSessionId: "session-9",
            KeyboardSignalActive: false,
            MouseSignalActive: false,
            LastInputEventUtc: nowUtc.AddMinutes(-2),
            LastInputAgentInstanceId: "agent-1",
            LastInputSessionId: "session-1",
            OwnershipWarning: "USER_AGENT_SESSION_MISMATCH",
            CaptureFailureReason: "USER_AGENT_SESSION_SWITCHED",
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: false,
            ColdStart: true,
            CalibrationStage: "cold",
            SystemActionsEnabled: false,
            TrustTier: "low",
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "normal",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc,
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-5),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: "session-9",
            UserAgentStartupSource: "service-watchdog",
            UserAgentLaunchReason: "session_mismatch",
            UserAgentHeartbeatWriteFailureCount: 0);
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Linked,
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            UpdatedAtUtc: nowUtc,
            LinkedAtUtc: nowUtc.AddMinutes(-5));

        MachineSummaryReportPayload payload = mapper.Map(status, latestState: null, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.False(payload.UserSessionConnected);
        Assert.Equal("session_switched", payload.UserAgentStartupState);
        Assert.Equal("USER_AGENT_SESSION_SWITCHED", payload.CaptureFailureReason);
        Assert.Equal("USER_AGENT_SESSION_MISMATCH", payload.OwnershipWarning);
    }

    [Fact]
    public void Map_DoesNotInventMissingKeyboardFailure_FromLatestStateTruth()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: true,
            FocusStrength: 0.3,
            OverloadLikelihood: 0.2,
            LastAction: "monitor_only",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddSeconds(-5),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 200,
            UserAgentSessionId: "session-2",
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: "session-2",
            UserAgentStartupSource: "run-key",
            UserAgentLaunchReason: "run-key",
            UserAgentHeartbeatWriteFailureCount: 0,
            KeyboardSignalActive: false,
            MouseSignalActive: true,
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            LastInputAgentInstanceId: "ua-1",
            LastInputSessionId: "session-2",
            OwnershipWarning: null,
            CaptureFailureReason: null,
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: false,
            ColdStart: true,
            CalibrationStage: "cold",
            SystemActionsEnabled: false,
            TrustTier: "low",
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "normal",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc);
        var latestState = new AgentLatestStateDetails(
            TimestampUtc: nowUtc,
            OverloadLikelihood: 0.2,
            Confidence: 0.5,
            SustainedAttentionDurationSec: 120d,
            IdleFragmentationIndex: 0.04d,
            FocusAppEntropy: 0.03d,
            OverloadActive: false,
            StateFlagsJson: "{\"schema_ver\":1}",
            ContributorsJson: "{\"schema_ver\":1}",
            BaselineReady: false,
            BaselineReadyPct: 0.3d,
            AvgQuality2m: 0.42d,
            ColdStart: true,
            CurrentQualityScore: 0.44d,
            PartialData: true,
            MissingMajorChannels: ["keyboard"]);
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Unlinked,
            AccountId: null,
            OrgId: null,
            UpdatedAtUtc: nowUtc);

        MachineSummaryReportPayload payload = mapper.Map(status, latestState, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.True(payload.UserSessionConnected);
        Assert.True(payload.ActivityDetected);
        Assert.Null(payload.CaptureFailureReason);
    }

    [Fact]
    public void Map_DoesNotInventMissingMouseFailure_FromLatestStateTruth()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: true,
            FocusStrength: 0.3,
            OverloadLikelihood: 0.2,
            LastAction: "monitor_only",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddSeconds(-5),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 200,
            UserAgentSessionId: "session-2",
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: "session-2",
            UserAgentStartupSource: "run-key",
            UserAgentLaunchReason: "run-key",
            UserAgentHeartbeatWriteFailureCount: 0,
            KeyboardSignalActive: true,
            MouseSignalActive: false,
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            LastInputAgentInstanceId: "ua-1",
            LastInputSessionId: "session-2",
            OwnershipWarning: null,
            CaptureFailureReason: null,
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: false,
            ColdStart: true,
            CalibrationStage: "cold",
            SystemActionsEnabled: false,
            TrustTier: "low",
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "normal",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc);
        var latestState = new AgentLatestStateDetails(
            TimestampUtc: nowUtc,
            OverloadLikelihood: 0.2,
            Confidence: 0.5,
            SustainedAttentionDurationSec: 120d,
            IdleFragmentationIndex: 0.04d,
            FocusAppEntropy: 0.03d,
            OverloadActive: false,
            StateFlagsJson: "{\"schema_ver\":1}",
            ContributorsJson: "{\"schema_ver\":1}",
            BaselineReady: false,
            BaselineReadyPct: 0.3d,
            AvgQuality2m: 0.42d,
            ColdStart: true,
            CurrentQualityScore: 0.44d,
            PartialData: true,
            MissingMajorChannels: ["mouse"]);
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Unlinked,
            AccountId: null,
            OrgId: null,
            UpdatedAtUtc: nowUtc);

        MachineSummaryReportPayload payload = mapper.Map(status, latestState, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.True(payload.UserSessionConnected);
        Assert.True(payload.ActivityDetected);
        Assert.Null(payload.CaptureFailureReason);
    }

    [Fact]
    public void Map_PreservesHeartbeatStaleFailure_FromStatusTruth()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Degraded,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: true,
            FocusStrength: 0.3,
            OverloadLikelihood: 0.2,
            LastAction: "monitor_only",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddMinutes(-2),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "Unknown",
            UserAgentHeartbeatUpdatedAtUtc: null,
            UserAgentInstanceId: null,
            UserAgentProcessId: null,
            UserAgentSessionId: null,
            KeyboardSignalActive: false,
            MouseSignalActive: false,
            LastInputEventUtc: null,
            LastInputAgentInstanceId: null,
            LastInputSessionId: null,
            OwnershipWarning: null,
            CaptureFailureReason: "USER_AGENT_HEARTBEAT_STALE",
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: false,
            ColdStart: true,
            CalibrationStage: "cold",
            SystemActionsEnabled: false,
            TrustTier: "low",
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "normal",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc,
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-50),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: null,
            UserAgentStartupSource: "service-watchdog",
            UserAgentLaunchReason: "stale_heartbeat",
            UserAgentHeartbeatWriteFailureCount: 1);
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Linked,
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            UpdatedAtUtc: nowUtc,
            LinkedAtUtc: nowUtc.AddMinutes(-5));

        MachineSummaryReportPayload payload = mapper.Map(status, latestState: null, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.False(payload.UserSessionConnected);
        Assert.Equal("USER_AGENT_HEARTBEAT_STALE", payload.CaptureFailureReason);
    }

    [Fact]
    public void Map_PreservesSelfTestFailure_FromStatusTruth()
    {
        var mapper = new MachineSummaryMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var status = new AgentServiceStatusSnapshot(
            Status: AgentStatus.Degraded,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0.0",
            ConsentTimestampUtc: nowUtc.AddHours(-1),
            ConsentDisclosureHash: "abc",
            BackendConnected: true,
            FocusStrength: 0.3,
            OverloadLikelihood: 0.2,
            LastAction: "monitor_only",
            AgentVersion: "1.0.0",
            SchemaVersion: "1.0.0",
            PolicyVersion: "policy-v2",
            LastEventTimestampUtc: nowUtc.AddSeconds(-5),
            BackendInstanceId: "backend-1",
            BackendProcessId: 100,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 200,
            UserAgentSessionId: "session-2",
            UserAgentObservedHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentObservedCaptureContext: "UserSession",
            UserAgentObservedSessionId: "session-2",
            UserAgentStartupSource: "run-key",
            UserAgentLaunchReason: "capture_self_test_failed",
            UserAgentHeartbeatWriteFailureCount: 0,
            KeyboardSignalActive: false,
            MouseSignalActive: false,
            LastInputEventUtc: null,
            LastInputAgentInstanceId: null,
            LastInputSessionId: null,
            OwnershipWarning: null,
            CaptureFailureReason: "USER_AGENT_INPUT_SELF_TEST_FAILED",
            LastError: "capture_self_test_failed:no_live_input_after_user_activity",
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: false,
            ColdStart: true,
            CalibrationStage: "cold",
            SystemActionsEnabled: false,
            TrustTier: "low",
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "normal",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: nowUtc);
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Linked,
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            UpdatedAtUtc: nowUtc,
            LinkedAtUtc: nowUtc.AddMinutes(-5));

        MachineSummaryReportPayload payload = mapper.Map(status, latestState: null, identityLink, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

        Assert.True(payload.UserSessionConnected);
        Assert.Equal("USER_AGENT_INPUT_SELF_TEST_FAILED", payload.CaptureFailureReason);
    }
}
