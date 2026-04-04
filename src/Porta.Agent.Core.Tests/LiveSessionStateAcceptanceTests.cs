using System.Diagnostics;
using Porta.Agent.Common.UserAgent;

namespace Porta.Agent.Core.Tests;

public sealed class LiveSessionStateAcceptanceTests
{
    [Fact]
    public void LiveSessionState_ReturnsFreshInMemoryActivityWithoutTouchingStore()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 555,
                    SessionId: 3,
                    UpdatedAtUtc: nowUtc,
                    AgentInstanceId: "agent-3")));

        controller.PublishLiveSessionState(new AgentLiveSessionSignalPacket(
            AgentInstanceId: "agent-3",
            SessionId: "session-3",
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-12),
            LastInputEventUtc: nowUtc.AddMilliseconds(-600),
            KeyboardActive: true,
            MouseActive: false,
            IdleSeconds: 0.8d,
            FocusAppHash: "a1b2c3d4",
            FocusStartedAtUtc: nowUtc.AddMinutes(-3)));

        AgentLiveSessionState state = controller.GetLiveSessionState();

        Assert.Equal("fresh", state.Freshness);
        Assert.Null(state.DegradedReason);
        Assert.Equal("agent-3", state.AgentInstanceId);
        Assert.Equal("session-3", state.SessionId);
        Assert.True(state.KeyboardActive);
        Assert.False(state.MouseActive);
        Assert.True(state.UserActive);
        Assert.False(state.Idle);
        Assert.Equal(0.8d, state.IdleSeconds, 3);
        Assert.Equal("a1b2c3d4", state.FocusAppHash);
        Assert.Equal(nowUtc.AddMinutes(-3), state.FocusStartedAtUtc);
        Assert.Equal(0, store.InputSignalsIngestCount);
    }

    [Fact]
    public void LiveSessionState_MarksStaleWhenUpdateFallsBehind()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var controller = new AgentCoreController(
            new LocalApiContractFakeStore(),
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 777,
                    SessionId: 8,
                    UpdatedAtUtc: nowUtc,
                    AgentInstanceId: "agent-8")));

        controller.PublishLiveSessionState(new AgentLiveSessionSignalPacket(
            AgentInstanceId: "agent-8",
            SessionId: "session-8",
            UpdatedAtUtc: nowUtc.AddSeconds(-10),
            SessionStartedAtUtc: nowUtc.AddMinutes(-4),
            LastInputEventUtc: nowUtc.AddSeconds(-10),
            KeyboardActive: false,
            MouseActive: false,
            IdleSeconds: 14d,
            FocusAppHash: null,
            FocusStartedAtUtc: nowUtc.AddMinutes(-2)));

        AgentLiveSessionState state = controller.GetLiveSessionState();

        Assert.Equal("stale", state.Freshness);
        Assert.Equal("live_session_stale", state.DegradedReason);
        Assert.False(state.UserActive);
        Assert.True(state.Idle);
        Assert.Null(state.FocusStartedAtUtc);
    }

    [Fact]
    public void LiveSessionState_AcceptsFreshRequestBoundHeartbeatProof_WithoutReadingSessionContextStore()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var controller = new AgentCoreController(
            new LocalApiContractFakeStore(),
            new FakeUserAgentSessionContextStore(null));

        controller.PublishLiveSessionState(new AgentLiveSessionSignalPacket(
            AgentInstanceId: "agent-8",
            SessionId: "session-8",
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-4),
            LastInputEventUtc: nowUtc.AddMilliseconds(-500),
            KeyboardActive: true,
            MouseActive: false,
            IdleSeconds: 0.4d,
            FocusAppHash: "a1b2c3d4",
            FocusStartedAtUtc: nowUtc.AddMinutes(-1),
            HeartbeatSequence: 11,
            HeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-1),
            HeartbeatCaptureContext: UserAgentCaptureContexts.UserSession));

        AgentLiveSessionState state = controller.GetLiveSessionState();

        Assert.Equal("fresh", state.Freshness);
        Assert.True(state.KeyboardActive);
        Assert.Equal("agent-8", state.AgentInstanceId);
        Assert.Equal("session-8", state.SessionId);
    }

    [Fact]
    public async Task LiveSessionState_RemainsResponsive_WhileRuntimeStateRefreshReadsStore()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        using var refreshEntered = new ManualResetEventSlim(false);
        using var refreshRelease = new ManualResetEventSlim(false);
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = new AgentBrainSnapshot(
                TimestampUtc: nowUtc,
                TypingBurstIntensity: 0.1d,
                ScrollVelocityIndex: 0.1d,
                FocusSwitchRate: 0.1d,
                SmoothedTypingBurstIntensity: 0.1d,
                SmoothedScrollVelocityIndex: 0.1d,
                SmoothedFocusSwitchRate: 0.1d,
                FocusStrength: 0.6d,
                OverloadLikelihood: 0.2d,
                FatigueProbability: 0.1d,
                CognitiveStability: 0.8d,
                DominantStateValue: 0.6d,
                DominantStateName: "focus",
                Confidence: 0.9d,
                LastAction: "maintain_focus",
                ActivationWindowTriggered: false,
                CooldownTriggered: false,
                ActivationWindowOpen: false,
                Contributors: [],
                SustainedAttentionDurationSec: 90d,
                IdleFragmentationIndex: 0.1d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1}",
                ContributorsJson: "{\"schema_ver\":1}"),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: nowUtc,
                OverloadLikelihood: 0.2d,
                Confidence: 0.9d,
                SustainedAttentionDurationSec: 90d,
                IdleFragmentationIndex: 0.1d,
                FocusAppEntropy: 0.1d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 1d,
                AvgQuality2m: 0.9d,
                ColdStart: false,
                CurrentQualityScore: 0.9d,
                PartialData: false,
                MissingMajorChannels: [])
        };

        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 555,
                    SessionId: 3,
                    UpdatedAtUtc: nowUtc,
                    AgentInstanceId: "agent-3")));

        controller.PublishLiveSessionState(new AgentLiveSessionSignalPacket(
            AgentInstanceId: "agent-3",
            SessionId: "session-3",
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-12),
            LastInputEventUtc: nowUtc.AddMilliseconds(-600),
            KeyboardActive: true,
            MouseActive: false,
            IdleSeconds: 0.8d,
            FocusAppHash: "a1b2c3d4",
            FocusStartedAtUtc: nowUtc.AddMinutes(-3)));

        store.ReadLatestStateDetailsEntered = refreshEntered;
        store.ReadLatestStateDetailsRelease = refreshRelease;
        Task refreshTask = Task.Run(controller.RefreshRuntimeStateCache);
        Assert.True(refreshEntered.Wait(TimeSpan.FromSeconds(2)));

        Stopwatch stopwatch = Stopwatch.StartNew();
        AgentLiveSessionState state = controller.GetLiveSessionState();
        stopwatch.Stop();

        refreshRelease.Set();
        await refreshTask;

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(150), $"live session read blocked for {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal("fresh", state.Freshness);
        Assert.Equal("agent-3", state.AgentInstanceId);
        Assert.Equal("session-3", state.SessionId);
    }

    private sealed class FakeUserAgentSessionContextStore : IUserAgentSessionContextStore
    {
        private readonly UserAgentSessionContextSnapshot? _snapshot;

        public FakeUserAgentSessionContextStore(UserAgentSessionContextSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public UserAgentSessionContextSnapshot? ReadCurrent() => _snapshot;

        public void WriteCurrent(UserAgentSessionContextSnapshot snapshot)
        {
        }
    }
}
