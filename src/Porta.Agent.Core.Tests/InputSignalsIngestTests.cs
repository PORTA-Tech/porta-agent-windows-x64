using Porta.Agent.Core;

namespace Porta.Agent.Core.Tests;

public sealed class InputSignalsIngestTests
{
    [Fact]
    public void IngestInputSignals_StoresPrivacySafeAggregatesAsRawContributors()
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

        Assert.Equal(9, store.SnapshotWriteCount);
        Assert.All(store.WrittenSnapshots, snapshot =>
        {
            Assert.Equal("input_signals_ingest", snapshot.LastAction);
            Assert.Equal("focus", snapshot.DominantStateName);
        });

        Assert.Contains(store.WrittenSnapshots, s => s.Contributors.Contains("raw_input:keyboard:keydown"));
        Assert.Contains(store.WrittenSnapshots, s => s.Contributors.Contains("raw_input:keyboard:backspace"));
        Assert.Contains(store.WrittenSnapshots, s => s.Contributors[0].StartsWith("raw_input:mouse_move:"));
        Assert.Contains(store.WrittenSnapshots, s => s.Contributors[0].StartsWith("raw_input:mouse_wheel:"));
        Assert.Contains(store.WrittenSnapshots, s => s.Contributors[0].StartsWith("raw_input:focus:"));
        Assert.Contains(store.WrittenSnapshots, s => s.Contributors[0].StartsWith("raw_input:idle:"));
        Assert.Equal(TimeSpan.Zero, store.WrittenSnapshots[0].TimestampUtc.Offset);
        Assert.Equal(lastInput.UtcDateTime, store.WrittenSnapshots[0].TimestampUtc.UtcDateTime);
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
    }
}
