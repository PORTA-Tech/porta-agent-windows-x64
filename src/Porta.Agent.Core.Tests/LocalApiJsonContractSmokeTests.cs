using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiJsonContractSmokeTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LiveSessionResponse_DoesNotEmitDuplicateIdleProperty_UnderWebJson()
    {
        var dto = new LocalApiLiveSessionStateResponse(
            UpdatedAtUtc: DateTimeOffset.Parse("2026-04-02T18:00:00Z"),
            SessionStartedAtUtc: DateTimeOffset.Parse("2026-04-02T17:45:00Z"),
            LastInputEventUtc: DateTimeOffset.Parse("2026-04-02T17:59:58Z"),
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            KeyboardActive: true,
            MouseActive: false,
            UserActive: true,
            Idle: true,
            IdleSeconds: 12.5d,
            FocusAppHash: "a1b2c3",
            Freshness: "fresh",
            DegradedReason: null);

        string json = JsonSerializer.Serialize(dto, WebJson);

        Assert.Single(Regex.Matches(json, "\"idle\":", RegexOptions.CultureInvariant).Cast<Match>());

        LocalApiLiveSessionStateResponse? roundTrip =
            JsonSerializer.Deserialize<LocalApiLiveSessionStateResponse>(json, WebJson);

        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.Idle);
        Assert.True(roundTrip.idle);
    }

    [Fact]
    public void HealthStatusAndSnapshotDtos_SerializeUnderWebJson()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-04-02T18:00:00Z");
        LocalApiHealthResponse health = new(
            Alive: true,
            TimestampUtc: nowUtc,
            ProcessId: 1234,
            AgentVersion: "1.0.29");

        AgentServiceStatusSnapshot status = CreateStatus(nowUtc);
        LocalApiStatusResponse statusDto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);
        LocalApiCurrentSnapshotResponse snapshotDto = SnapshotMapper.Map(CreateSnapshot(nowUtc), latestState: null, status: status);

        string healthJson = JsonSerializer.Serialize(health, WebJson);
        string statusJson = JsonSerializer.Serialize(statusDto, WebJson);
        string snapshotJson = JsonSerializer.Serialize(snapshotDto, WebJson);

        Assert.Contains("\"alive\":true", healthJson, StringComparison.Ordinal);
        Assert.Contains("\"freshness\"", statusJson, StringComparison.Ordinal);
        Assert.Contains("\"freshness\"", snapshotJson, StringComparison.Ordinal);
    }

    private static AgentServiceStatusSnapshot CreateStatus(DateTimeOffset nowUtc)
    {
        return new AgentServiceStatusSnapshot(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "1.0",
            ConsentTimestampUtc: nowUtc.AddMinutes(-5),
            ConsentDisclosureHash: "consent-hash",
            BackendConnected: true,
            FocusStrength: 0.72d,
            OverloadLikelihood: 0.14d,
            LastAction: "maintain_focus",
            AgentVersion: "1.0.29",
            SchemaVersion: "1",
            PolicyVersion: "1",
            LastEventTimestampUtc: nowUtc.AddSeconds(-10),
            BackendInstanceId: "backend-1",
            BackendProcessId: 3456,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-2),
            UserAgentInstanceId: "agent-1",
            UserAgentProcessId: 4567,
            UserAgentSessionId: "session-1",
            KeyboardSignalActive: true,
            MouseSignalActive: true,
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            LastInputAgentInstanceId: "agent-1",
            LastInputSessionId: "session-1",
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
            BrightnessSupported: true,
            BrightnessCurrentLevel: 80,
            BrightnessTargetLevel: 80,
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
            DeepWorkStreakSeconds: 600,
            VisualMode: "normal",
            LatestContributorsJson: "{\"schema_ver\":1}",
            LatestWhyJson: "{\"schema_ver\":1}",
            LastUpdatedUtc: nowUtc,
            RuntimeStateFreshness: "fresh",
            RuntimeStateLastSuccessfulRefreshUtc: nowUtc.AddSeconds(-2),
            RuntimeStateLastAttemptUtc: nowUtc.AddSeconds(-1),
            RuntimeStateRefreshError: null);
    }

    private static AgentBrainSnapshot CreateSnapshot(DateTimeOffset nowUtc)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: nowUtc,
            TypingBurstIntensity: 0.10d,
            ScrollVelocityIndex: 0.05d,
            FocusSwitchRate: 0.03d,
            SmoothedTypingBurstIntensity: 0.08d,
            SmoothedScrollVelocityIndex: 0.04d,
            SmoothedFocusSwitchRate: 0.02d,
            FocusStrength: 0.71d,
            OverloadLikelihood: 0.14d,
            FatigueProbability: 0.11d,
            CognitiveStability: 0.83d,
            DominantStateValue: 0.71d,
            DominantStateName: "focus",
            Confidence: 0.88d,
            LastAction: "maintain_focus",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: [],
            SustainedAttentionDurationSec: 620d,
            IdleFragmentationIndex: 0.03d,
            OverloadActive: false,
            StateFlagsJson: "{\"schema_ver\":1}",
            ContributorsJson: "{\"schema_ver\":1}");
    }
}
