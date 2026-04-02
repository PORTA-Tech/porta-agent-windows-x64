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
