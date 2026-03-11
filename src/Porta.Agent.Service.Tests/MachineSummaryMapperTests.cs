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
        Assert.Equal(0.88, payload.TaskContinuityScore);
        Assert.Equal(1800, payload.TaskContinuitySeconds);
        Assert.Equal("keep", payload.ContinuityState);
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
    }
}
