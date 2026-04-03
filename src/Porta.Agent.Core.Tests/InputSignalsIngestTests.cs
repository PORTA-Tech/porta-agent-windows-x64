using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Porta.Agent.Core.Tests;

public sealed class InputSignalsIngestTests
{
    [Fact]
    public void IngestInputSignals_RoutesPrivacySafePacketIntoBucketIngress()
    {
        var store = new LocalApiContractFakeStore();
        var controller = CreateController(store);
        DateTimeOffset windowStart = new DateTimeOffset(2026, 03, 07, 10, 0, 0, TimeSpan.FromHours(3));
        DateTimeOffset windowEnd = windowStart.AddSeconds(8);
        DateTimeOffset lastInput = windowEnd.AddSeconds(2);

        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: windowStart,
            WindowEndUtc: windowEnd,
            KeyboardKeydownCount: 3,
            KeyboardBackspaceCount: 2,
            MouseWheelDelta: 120,
            MouseMoveDx: 15d,
            MouseMoveDy: -7d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.75d,
            LastInputEventUtc: lastInput,
            KeyboardIkiMeanMs: 115d,
            KeyboardIkiStdMs: 20d,
            KeyboardIkiCv: 0.17d,
            KeyboardBurstCount: 2,
            KeyboardBurstRate: 0.25d,
            KeyboardCorrectionSpikeCount: 1,
            KeyboardCorrectionSpikeRate: 0.12d,
            MouseMoveSampleCount: 8,
            MousePathLengthPx: 144d,
            MouseSpeedMeanPxPerSec: 52d,
            MouseJerkP95: 130d,
            MouseMicroCorrectionCount: 2,
            MouseMicroCorrectionRate: 0.2d,
            MouseWheelEventCount: 2,
            MouseScrollBurstCount: 1,
            MouseScrollBurstRate: 0.12d,
            MouseScrollSpeedMean: 240d,
            FocusObservationCount: 5,
            FocusSwitchCount: 2,
            FocusActiveAppCount: 2,
            FocusAppEntropy: 0.66d,
            IdleObservationCount: 5,
            IdleSecondsWithinWindow: 1.6d,
            MicroIdleCount: 1,
            MicroIdleSeconds: 4.5d,
            BreakSecondsWithinWindow: 0d,
            ShortIdleBoundaryObserved: true,
            NotificationToastsEnabled: false));

        Assert.Equal(0, store.SnapshotWriteCount);
        Assert.Equal(1, store.InputSignalsIngestCount);

        InputSignalsPacket ingested = Assert.Single(store.IngestedInputSignals);
        Assert.Equal(2, ingested.SchemaVer);
        Assert.Equal("agent-1", ingested.AgentInstanceId);
        Assert.Equal("session-1", ingested.SessionId);
        Assert.Equal(windowStart.ToUniversalTime().UtcDateTime, ingested.WindowStartUtc!.Value.UtcDateTime);
        Assert.Equal(windowEnd.ToUniversalTime().UtcDateTime, ingested.WindowEndUtc!.Value.UtcDateTime);
        Assert.Equal(3, ingested.KeyboardKeydownCount);
        Assert.Equal(2, ingested.KeyboardBackspaceCount);
        Assert.Equal(120, ingested.MouseWheelDelta);
        Assert.Equal(15d, ingested.MouseMoveDx);
        Assert.Equal(-7d, ingested.MouseMoveDy);
        Assert.Equal("a1b2c3d4", ingested.FocusAppHash);
        Assert.Equal(0.75d, ingested.IdleSeconds, precision: 6);
        Assert.Equal(lastInput.ToUniversalTime().UtcDateTime, ingested.LastInputEventUtc!.Value.UtcDateTime);
        Assert.Equal(115d, ingested.KeyboardIkiMeanMs);
        Assert.Equal(20d, ingested.KeyboardIkiStdMs);
        Assert.Equal(0.17d, ingested.KeyboardIkiCv);
        Assert.Equal(2, ingested.KeyboardBurstCount);
        Assert.Equal(0.25d, ingested.KeyboardBurstRate);
        Assert.Equal(1, ingested.KeyboardCorrectionSpikeCount);
        Assert.Equal(0.12d, ingested.KeyboardCorrectionSpikeRate);
        Assert.Equal(8, ingested.MouseMoveSampleCount);
        Assert.Equal(144d, ingested.MousePathLengthPx);
        Assert.Equal(52d, ingested.MouseSpeedMeanPxPerSec);
        Assert.Equal(130d, ingested.MouseJerkP95);
        Assert.Equal(2, ingested.MouseMicroCorrectionCount);
        Assert.Equal(0.2d, ingested.MouseMicroCorrectionRate);
        Assert.Equal(2, ingested.MouseWheelEventCount);
        Assert.Equal(1, ingested.MouseScrollBurstCount);
        Assert.Equal(0.12d, ingested.MouseScrollBurstRate);
        Assert.Equal(240d, ingested.MouseScrollSpeedMean);
        Assert.Equal(5, ingested.FocusObservationCount);
        Assert.Equal(2, ingested.FocusSwitchCount);
        Assert.Equal(2, ingested.FocusActiveAppCount);
        Assert.Equal(0.66d, ingested.FocusAppEntropy);
        Assert.Equal(5, ingested.IdleObservationCount);
        Assert.Equal(1.6d, ingested.IdleSecondsWithinWindow);
        Assert.Equal(1, ingested.MicroIdleCount);
        Assert.Equal(4.5d, ingested.MicroIdleSeconds);
        Assert.Equal(0d, ingested.BreakSecondsWithinWindow);
        Assert.True(ingested.ShortIdleBoundaryObserved);
        Assert.False(ingested.NotificationToastsEnabled);
    }

    [Fact]
    public void IngestInputSignals_RejectsUnsupportedSchema()
    {
        var store = new LocalApiContractFakeStore();
        var controller = CreateController(store);

        var ex = Assert.Throws<ArgumentException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 99,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 0,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: null)));

        Assert.StartsWith("unsupported_schema_ver", ex.Message, StringComparison.Ordinal);
        Assert.Equal("packet", ex.ParamName);
        Assert.Equal(0, store.SnapshotWriteCount);
        Assert.Equal(0, store.InputSignalsIngestCount);
    }

    [Fact]
    public void IngestInputSignals_RejectsMissingIdentifiers()
    {
        var store = new LocalApiContractFakeStore();
        var controller = CreateController(store);

        var ex1 = Assert.Throws<ArgumentException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "",
            SessionId: "session-1",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 0,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: null)));

        Assert.StartsWith("missing_agent_instance_id", ex1.Message, StringComparison.Ordinal);
        Assert.Equal("AgentInstanceId", ex1.ParamName);
        Assert.Equal(0, store.SnapshotWriteCount);
        Assert.Equal(0, store.InputSignalsIngestCount);

        var ex2 = Assert.Throws<ArgumentException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: " ",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 0,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: null)));

        Assert.StartsWith("missing_session_id", ex2.Message, StringComparison.Ordinal);
        Assert.Equal("SessionId", ex2.ParamName);
        Assert.Equal(0, store.InputSignalsIngestCount);
    }

    [Fact]
    public void IngestInputSignals_RejectsInvalidIdentifiersOrHash()
    {
        var store = new LocalApiContractFakeStore();
        var controller = CreateController(store);

        var invalidId = new string('x', 300);
        var ex1 = Assert.Throws<ArgumentException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: invalidId,
            SessionId: "session-1",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 0,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: null)));
        Assert.StartsWith("missing_agent_instance_id", ex1.Message, StringComparison.Ordinal);
        Assert.Equal("AgentInstanceId", ex1.ParamName);

        var invalidSession = "session 1";
        var ex2 = Assert.Throws<ArgumentException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: invalidSession,
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 0,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: null)));
        Assert.StartsWith("missing_session_id", ex2.Message, StringComparison.Ordinal);
        Assert.Equal("SessionId", ex2.ParamName);

        var ex3 = Assert.Throws<ArgumentException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 0,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: "chrome://Window",
            IdleSeconds: 0,
            LastInputEventUtc: null)));
        Assert.StartsWith("invalid_focus_app_hash", ex3.Message, StringComparison.Ordinal);
        Assert.Equal("FocusAppHash", ex3.ParamName);
        Assert.Equal(0, store.InputSignalsIngestCount);
    }

    [Fact]
    public void IngestInputSignals_RejectsPacketsWithoutFreshUserAgentHeartbeat()
    {
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(store, new FakeUserAgentSessionContextStore(null));

        var ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: DateTimeOffset.UtcNow)));

        Assert.Equal("input_signals_source_heartbeat_missing", ex.Message);
        Assert.Equal(0, store.InputSignalsIngestCount);
    }

    [Fact]
    public void IngestInputSignals_AcceptsFreshRequestBoundHeartbeatProof_WithoutReadingSessionContextStore()
    {
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(store, new FakeUserAgentSessionContextStore(null));
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: nowUtc.AddSeconds(-5),
            WindowEndUtc: nowUtc,
            KeyboardKeydownCount: 2,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: nowUtc,
            HeartbeatSequence: 7,
            HeartbeatUpdatedAtUtc: nowUtc.AddSeconds(-1),
            HeartbeatCaptureContext: UserAgentCaptureContexts.UserSession));

        InputSignalsPacket ingested = Assert.Single(store.IngestedInputSignals);
        Assert.Equal(7, ingested.HeartbeatSequence);
        Assert.Equal(UserAgentCaptureContexts.UserSession, ingested.HeartbeatCaptureContext);
    }

    [Fact]
    public void IngestInputSignals_RejectsStaleRequestBoundHeartbeatProof()
    {
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(store, new FakeUserAgentSessionContextStore(null));
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: nowUtc.AddSeconds(-5),
            WindowEndUtc: nowUtc,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: nowUtc,
            HeartbeatSequence: 8,
            HeartbeatUpdatedAtUtc: nowUtc.AddMinutes(-1),
            HeartbeatCaptureContext: UserAgentCaptureContexts.UserSession)));

        Assert.Equal("input_signals_source_heartbeat_missing", ex.Message);
    }

    [Fact]
    public void IngestInputSignals_RejectsPacketsFromNonUserSessionHeartbeat()
    {
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.ServiceSession,
                ProcessId: 4567,
                SessionId: 0,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: "agent-1")));

        var ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-0",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: DateTimeOffset.UtcNow)));

        Assert.Equal("input_signals_source_invalid_context", ex.Message);
        Assert.Equal(0, store.InputSignalsIngestCount);
    }

    [Fact]
    public void IngestInputSignals_RejectsPacketsWithHeartbeatIdentityMismatch()
    {
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 4567,
                SessionId: 2,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: "heartbeat-agent")));

        var ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "packet-agent",
            SessionId: "session-2",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: DateTimeOffset.UtcNow)));

        Assert.Equal("input_signals_source_agent_mismatch", ex.Message);
        Assert.Equal(0, store.InputSignalsIngestCount);

        ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "heartbeat-agent",
            SessionId: "session-9",
            WindowStartUtc: null,
            WindowEndUtc: null,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: DateTimeOffset.UtcNow)));

        Assert.Equal("input_signals_source_session_mismatch", ex.Message);
        Assert.Equal(0, store.InputSignalsIngestCount);
    }

    [Fact]
    public void IngestInputSignals_RejectedPacket_DoesNotOverwriteLastAcceptedOwner()
    {
        var store = new LocalApiContractFakeStore();
        AgentCoreController controller = CreateController(store);
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow;

        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 2,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: lastInputUtc));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "packet-agent",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-3),
            WindowEndUtc: lastInputUtc.AddSeconds(-1),
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: lastInputUtc.AddSeconds(-1))));

        InvalidOperationException ex2 = Assert.Throws<InvalidOperationException>(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-9",
            WindowStartUtc: lastInputUtc.AddSeconds(-2),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: lastInputUtc)));

        AgentServiceStatusSnapshot status = controller.GetStatus();

        Assert.Equal("input_signals_source_agent_mismatch", ex.Message);
        Assert.Equal("input_signals_source_session_mismatch", ex2.Message);
        Assert.Equal(1, store.InputSignalsIngestCount);
        Assert.Equal("agent-1", status.LastInputAgentInstanceId);
        Assert.Equal("session-1", status.LastInputSessionId);
        Assert.Null(status.OwnershipWarning);
    }

    [Fact]
    public async Task StatusPath_DoesNotBlockOnAuthStateUpdate_While_InputIngressIsBusy()
    {
        using var ingestEntered = new ManualResetEventSlim(false);
        using var ingestRelease = new ManualResetEventSlim(false);
        var store = new LocalApiContractFakeStore
        {
            IngestInputSignalsEntered = ingestEntered,
            IngestInputSignalsRelease = ingestRelease
        };
        AgentCoreController controller = CreateController(store);
        DateTimeOffset lastInputUtc = DateTimeOffset.UtcNow;

        Task ingestTask = Task.Run(() => controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: lastInputUtc.AddSeconds(-5),
            WindowEndUtc: lastInputUtc,
            KeyboardKeydownCount: 2,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: lastInputUtc)));

        Assert.True(ingestEntered.Wait(TimeSpan.FromSeconds(2)), "Timed out waiting for fake ingress to block.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        controller.SetLocalApiAuthState(false, "TOKEN_READ_FAILED");
        AgentServiceStatusSnapshot status = controller.GetStatus();
        stopwatch.Stop();

        ingestRelease.Set();
        await ingestTask;

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Expected status path to stay fast while ingress was blocked, but it took {stopwatch.Elapsed.TotalMilliseconds:F0}ms.");
        Assert.False(status.LocalApiAuthReady);
        Assert.Equal("TOKEN_READ_FAILED", status.LocalApiAuthError);
        Assert.Equal("agent-1", status.LastInputAgentInstanceId);
        Assert.Equal("session-1", status.LastInputSessionId);
    }

    private static AgentCoreController CreateController(LocalApiContractFakeStore store)
    {
        return new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 1234,
                SessionId: 1,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: "agent-1")));
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
