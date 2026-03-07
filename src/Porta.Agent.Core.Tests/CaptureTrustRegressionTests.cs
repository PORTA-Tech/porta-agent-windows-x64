using Porta.Agent.Common.UserAgent;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

public sealed class CaptureTrustRegressionTests
{
    [Fact]
    public void ServiceSessionCapture_RemainsPartial_AndKeepsTrustCapped()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.08d, confidence: 0.20d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.12d,
                Confidence: 0.41d,
                SustainedAttentionDurationSec: 0d,
                IdleFragmentationIndex: 0.02d,
                FocusAppEntropy: 0.01d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.04d,
                AvgQuality2m: 0.28d,
                ColdStart: true,
                CurrentQualityScore: 0.32d,
                PartialData: true,
                MissingMajorChannels: ["keyboard", "mouse"]),
        };

        var sessionContextStore = new FakeUserAgentSessionContextStore(
            new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.ServiceSession,
                ProcessId: 3210,
                SessionId: 0,
                UpdatedAtUtc: DateTimeOffset.UtcNow));

        var controller = new AgentCoreController(store, sessionContextStore);
        var statusDto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);
        var snapshotDto = SnapshotMapper.Map(controller.GetCurrentSnapshot(), controller.GetLatestStateDetails());

        Assert.Equal("ServiceSession", statusDto.capture_context);
        Assert.Equal("CAPTURE_CONTEXT_SERVICE_SESSION", statusDto.capture_failure_reason);
        Assert.False(statusDto.keyboard_signal_active);
        Assert.False(statusDto.mouse_signal_active);

        Assert.True(snapshotDto.PartialData ?? false);
        Assert.Equal(["keyboard", "mouse"], snapshotDto.MissingMajorChannels);
        Assert.False(snapshotDto.BaselineReady ?? true);
        Assert.True(snapshotDto.ColdStart ?? false);
        Assert.InRange(snapshotDto.Confidence, 0d, 0.45d);
    }

    [Fact]
    public void UserSessionRecovery_ClearsPartialState_AndUnlocksTrust()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.14d, confidence: 0.84d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.18d,
                Confidence: 0.86d,
                SustainedAttentionDurationSec: 930d,
                IdleFragmentationIndex: 0.11d,
                FocusAppEntropy: 0.08d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 1.0d,
                AvgQuality2m: 0.91d,
                ColdStart: false,
                CurrentQualityScore: 0.93d,
                PartialData: false,
                MissingMajorChannels: []),
        };

        var controller = new AgentCoreController(store);
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow;
        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 6,
            KeyboardBackspaceCount: 1,
            MouseWheelDelta: 120,
            MouseMoveDx: 14d,
            MouseMoveDy: 6d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.2d,
            LastInputEventUtc: lastInputUtc));

        var statusDto = StatusMapper.Map(controller.GetStatus(), localApiAuthReady: true, localApiAuthError: null);
        var snapshotDto = SnapshotMapper.Map(controller.GetCurrentSnapshot(), controller.GetLatestStateDetails());

        Assert.Equal("UserSession", statusDto.capture_context);
        Assert.Null(statusDto.capture_failure_reason);
        Assert.True(statusDto.keyboard_signal_active);
        Assert.True(statusDto.mouse_signal_active);
        Assert.NotNull(statusDto.last_input_event_utc);

        Assert.False(snapshotDto.PartialData ?? true);
        Assert.Empty(snapshotDto.MissingMajorChannels ?? []);
        Assert.True(snapshotDto.BaselineReady ?? false);
        Assert.False(snapshotDto.ColdStart ?? true);
        Assert.True(snapshotDto.Confidence > 0.65d);
    }

    private static AgentBrainSnapshot CreateSnapshot(double overload, double confidence)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: 0.05d,
            ScrollVelocityIndex: 0.06d,
            FocusSwitchRate: 0.03d,
            SmoothedTypingBurstIntensity: 0.05d,
            SmoothedScrollVelocityIndex: 0.06d,
            SmoothedFocusSwitchRate: 0.03d,
            FocusStrength: 0.52d,
            OverloadLikelihood: overload,
            FatigueProbability: 0.11d,
            CognitiveStability: 0.82d,
            DominantStateValue: 0.52d,
            DominantStateName: "focus",
            Confidence: confidence,
            LastAction: "maintain_focus",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: []);
    }

    private sealed class FakeUserAgentSessionContextStore : IUserAgentSessionContextStore
    {
        private UserAgentSessionContextSnapshot? _snapshot;

        public FakeUserAgentSessionContextStore(UserAgentSessionContextSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public UserAgentSessionContextSnapshot? ReadCurrent()
        {
            return _snapshot;
        }

        public void WriteCurrent(UserAgentSessionContextSnapshot snapshot)
        {
            _snapshot = snapshot;
        }
    }
}
