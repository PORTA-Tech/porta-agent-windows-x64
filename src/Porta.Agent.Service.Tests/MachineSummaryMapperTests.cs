using Porta.Agent.Common.Contracts;
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

        MachineSummaryReportPayload payload = mapper.Map(status, latestState, "DOMAIN\\alice", backendConnected: true, reportedAtUtc: nowUtc);

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
}
