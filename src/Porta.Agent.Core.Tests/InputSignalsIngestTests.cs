using Porta.Agent.Core;

namespace Porta.Agent.Core.Tests;

public sealed class InputSignalsIngestTests
{
    [Fact]
    public void IngestInputSignals_RoutesPrivacySafePacketIntoBucketIngress()
    {
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(store);
        DateTimeOffset windowStart = new DateTimeOffset(2026, 03, 07, 10, 0, 0, TimeSpan.FromHours(3));
        DateTimeOffset windowEnd = windowStart.AddSeconds(8);
        DateTimeOffset lastInput = windowEnd.AddSeconds(2);

        controller.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
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
            LastInputEventUtc: lastInput));

        Assert.Equal(0, store.SnapshotWriteCount);
        Assert.Equal(1, store.InputSignalsIngestCount);

        InputSignalsPacket ingested = Assert.Single(store.IngestedInputSignals);
        Assert.Equal(1, ingested.SchemaVer);
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
    }

    [Fact]
    public void IngestInputSignals_RejectsUnsupportedSchema()
    {
        var store = new LocalApiContractFakeStore();
        var controller = new AgentCoreController(store);

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
        var controller = new AgentCoreController(store);

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
        var controller = new AgentCoreController(store);

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
}
