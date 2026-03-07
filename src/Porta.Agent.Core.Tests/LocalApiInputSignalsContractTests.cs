using System.Text.Json;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiInputSignalsContractTests
{
    [Fact]
    public void InputSignalsRequest_SerializesWithPinnedSnakeCasePrivacySafeFields()
    {
        var request = new LocalApiInputSignalsRequest(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: new DateTimeOffset(2026, 03, 07, 9, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new DateTimeOffset(2026, 03, 07, 9, 0, 5, TimeSpan.Zero),
            KeyboardKeydownCount: 4,
            KeyboardBackspaceCount: 1,
            MouseWheelDelta: 120,
            MouseMoveDx: 18,
            MouseMoveDy: -6,
            FocusAppHash: "abc123",
            IdleSeconds: 0.5d,
            LastInputEventUtc: new DateTimeOffset(2026, 03, 07, 9, 0, 5, TimeSpan.Zero));

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(request));
        string[] propertyNames = json.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "agent_instance_id",
                "focus_app_hash",
                "idle_seconds",
                "keyboard_backspace_count",
                "keyboard_keydown_count",
                "last_input_event_utc",
                "mouse_move_dx",
                "mouse_move_dy",
                "mouse_wheel_delta",
                "schema_ver",
                "session_id",
                "window_end_utc",
                "window_start_utc"
            ],
            propertyNames);

        string payload = json.RootElement.GetRawText();
        Assert.DoesNotContain("schemaVer", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("agentInstanceId", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionId", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("typed_text", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clipboard", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screen", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InputSignalsRequest_DeserializesPinnedSnakeCaseFields()
    {
        const string payload = """
            {
              "schema_ver": 1,
              "agent_instance_id": "agent-1",
              "session_id": "session-1",
              "window_start_utc": "2026-03-07T09:00:00.000Z",
              "window_end_utc": "2026-03-07T09:00:05.000Z",
              "keyboard_keydown_count": 4,
              "keyboard_backspace_count": 1,
              "mouse_wheel_delta": 120,
              "mouse_move_dx": 18,
              "mouse_move_dy": -6,
              "focus_app_hash": "abc123",
              "idle_seconds": 0.5,
              "last_input_event_utc": "2026-03-07T09:00:05.000Z"
            }
            """;

        LocalApiInputSignalsRequest? request = JsonSerializer.Deserialize<LocalApiInputSignalsRequest>(payload);

        Assert.NotNull(request);
        Assert.Equal(1, request.SchemaVer);
        Assert.Equal("agent-1", request.AgentInstanceId);
        Assert.Equal("session-1", request.SessionId);
        Assert.Equal(4, request.KeyboardKeydownCount);
        Assert.Equal(1, request.KeyboardBackspaceCount);
        Assert.Equal(120, request.MouseWheelDelta);
        Assert.Equal(18d, request.MouseMoveDx);
        Assert.Equal(-6d, request.MouseMoveDy);
        Assert.Equal("abc123", request.FocusAppHash);
        Assert.Equal(0.5d, request.IdleSeconds);
        Assert.Equal(DateTimeOffset.Parse("2026-03-07T09:00:05.000Z"), request.LastInputEventUtc);
    }
}
