using System.Text.Json;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiInputSignalsContractTests
{
    [Fact]
    public void InputSignalsRequest_SerializesWithPinnedSnakeCasePrivacySafeFields()
    {
        var request = new LocalApiInputSignalsRequest(
            SchemaVer: 2,
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
            LastInputEventUtc: new DateTimeOffset(2026, 03, 07, 9, 0, 5, TimeSpan.Zero),
            KeyboardIkiMeanMs: 125d,
            KeyboardIkiStdMs: 14d,
            KeyboardIkiCv: 0.11d,
            KeyboardBurstCount: 2,
            KeyboardBurstRate: 0.4d,
            KeyboardCorrectionSpikeCount: 1,
            KeyboardCorrectionSpikeRate: 0.2d,
            MouseMoveSampleCount: 6,
            MousePathLengthPx: 120d,
            MouseSpeedMeanPxPerSec: 42d,
            MouseJerkP95: 93d,
            MouseMicroCorrectionCount: 2,
            MouseMicroCorrectionRate: 0.4d,
            MouseWheelEventCount: 3,
            MouseScrollBurstCount: 1,
            MouseScrollBurstRate: 0.2d,
            MouseScrollSpeedMean: 360d,
            FocusObservationCount: 5,
            FocusSwitchCount: 2,
            FocusActiveAppCount: 2,
            FocusAppEntropy: 0.68d,
            IdleObservationCount: 5,
            IdleSecondsWithinWindow: 1.2d,
            MicroIdleCount: 1,
            MicroIdleSeconds: 4.5d,
            BreakSecondsWithinWindow: 0d,
            ShortIdleBoundaryObserved: true,
            NotificationToastsEnabled: true);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(request));
        string[] propertyNames = json.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "agent_instance_id",
                "break_seconds_within_window",
                "focus_active_app_count",
                "focus_app_entropy",
                "focus_app_hash",
                "focus_observation_count",
                "focus_switch_count",
                "heartbeat_capture_context",
                "heartbeat_sequence",
                "heartbeat_updated_at_utc",
                "idle_observation_count",
                "idle_seconds",
                "idle_seconds_within_window",
                "keyboard_backspace_count",
                "keyboard_burst_count",
                "keyboard_burst_rate",
                "keyboard_correction_spike_count",
                "keyboard_correction_spike_rate",
                "keyboard_iki_cv",
                "keyboard_iki_mean_ms",
                "keyboard_iki_std_ms",
                "keyboard_keydown_count",
                "last_input_event_utc",
                "micro_idle_count",
                "micro_idle_seconds",
                "mouse_jerk_p95",
                "mouse_micro_correction_count",
                "mouse_micro_correction_rate",
                "mouse_move_dx",
                "mouse_move_dy",
                "mouse_move_sample_count",
                "mouse_path_length_px",
                "mouse_scroll_burst_count",
                "mouse_scroll_burst_rate",
                "mouse_scroll_speed_mean",
                "mouse_speed_mean_px_per_sec",
                "mouse_wheel_delta",
                "mouse_wheel_event_count",
                "notification_toasts_enabled",
                "schema_ver",
                "session_id",
                "short_idle_boundary_observed",
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
              "schema_ver": 2,
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
              "last_input_event_utc": "2026-03-07T09:00:05.000Z",
              "keyboard_iki_mean_ms": 125.0,
              "keyboard_iki_std_ms": 14.0,
              "keyboard_iki_cv": 0.11,
              "keyboard_burst_count": 2,
              "keyboard_burst_rate": 0.4,
              "keyboard_correction_spike_count": 1,
              "keyboard_correction_spike_rate": 0.2,
              "mouse_move_sample_count": 6,
              "mouse_path_length_px": 120.0,
              "mouse_speed_mean_px_per_sec": 42.0,
              "mouse_jerk_p95": 93.0,
              "mouse_micro_correction_count": 2,
              "mouse_micro_correction_rate": 0.4,
              "mouse_wheel_event_count": 3,
              "mouse_scroll_burst_count": 1,
              "mouse_scroll_burst_rate": 0.2,
              "mouse_scroll_speed_mean": 360.0,
              "focus_observation_count": 5,
              "focus_switch_count": 2,
              "focus_active_app_count": 2,
              "focus_app_entropy": 0.68,
              "idle_observation_count": 5,
              "idle_seconds_within_window": 1.2,
              "micro_idle_count": 1,
              "micro_idle_seconds": 4.5,
              "break_seconds_within_window": 0.0,
              "short_idle_boundary_observed": true,
              "notification_toasts_enabled": true
            }
            """;

        LocalApiInputSignalsRequest? request = JsonSerializer.Deserialize<LocalApiInputSignalsRequest>(payload);

        Assert.NotNull(request);
        Assert.Equal(2, request.SchemaVer);
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
        Assert.Equal(125d, request.KeyboardIkiMeanMs);
        Assert.Equal(14d, request.KeyboardIkiStdMs);
        Assert.Equal(0.11d, request.KeyboardIkiCv);
        Assert.Equal(2, request.KeyboardBurstCount);
        Assert.Equal(0.4d, request.KeyboardBurstRate);
        Assert.Equal(1, request.KeyboardCorrectionSpikeCount);
        Assert.Equal(0.2d, request.KeyboardCorrectionSpikeRate);
        Assert.Equal(6, request.MouseMoveSampleCount);
        Assert.Equal(120d, request.MousePathLengthPx);
        Assert.Equal(42d, request.MouseSpeedMeanPxPerSec);
        Assert.Equal(93d, request.MouseJerkP95);
        Assert.Equal(2, request.MouseMicroCorrectionCount);
        Assert.Equal(0.4d, request.MouseMicroCorrectionRate);
        Assert.Equal(3, request.MouseWheelEventCount);
        Assert.Equal(1, request.MouseScrollBurstCount);
        Assert.Equal(0.2d, request.MouseScrollBurstRate);
        Assert.Equal(360d, request.MouseScrollSpeedMean);
        Assert.Equal(5, request.FocusObservationCount);
        Assert.Equal(2, request.FocusSwitchCount);
        Assert.Equal(2, request.FocusActiveAppCount);
        Assert.Equal(0.68d, request.FocusAppEntropy);
        Assert.Equal(5, request.IdleObservationCount);
        Assert.Equal(1.2d, request.IdleSecondsWithinWindow);
        Assert.Equal(1, request.MicroIdleCount);
        Assert.Equal(4.5d, request.MicroIdleSeconds);
        Assert.Equal(0d, request.BreakSecondsWithinWindow);
        Assert.True(request.ShortIdleBoundaryObserved);
        Assert.True(request.NotificationToastsEnabled);
    }
}
