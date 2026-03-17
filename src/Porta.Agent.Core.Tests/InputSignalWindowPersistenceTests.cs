using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Outputs;
using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Tests;

public sealed class InputSignalWindowPersistenceTests
{
    [Fact]
    public void AcceptedInputWindow_PersistsAfterFinalizerFlush()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-input-window-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string dbPath = Path.Combine(tempDirectory, "core-state.db");

        try
        {
            var innerStore = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            var stateStore = new BucketFinalizerStateStore(
                innerStore,
                () => new DateTimeOffset(2026, 03, 17, 10, 0, 8, TimeSpan.Zero));

            DateTimeOffset windowStartUtc = new(2026, 03, 17, 10, 0, 0, TimeSpan.Zero);
            DateTimeOffset windowEndUtc = new(2026, 03, 17, 10, 0, 5, TimeSpan.Zero);

            stateStore.IngestInputSignals(new InputSignalsPacket(
                SchemaVer: 2,
                AgentInstanceId: "agent-record-1",
                SessionId: "session-record-1",
                WindowStartUtc: windowStartUtc,
                WindowEndUtc: windowEndUtc,
                KeyboardKeydownCount: 6,
                KeyboardBackspaceCount: 1,
                MouseWheelDelta: 120,
                MouseMoveDx: 24d,
                MouseMoveDy: -12d,
                FocusAppHash: "ABCD1234",
                IdleSeconds: 1.25d,
                LastInputEventUtc: windowEndUtc,
                KeyboardIkiMeanMs: 125d,
                KeyboardIkiStdMs: 14d,
                KeyboardIkiCv: 0.112d,
                KeyboardBurstCount: 2,
                KeyboardBurstRate: 0.4d,
                KeyboardCorrectionSpikeCount: 1,
                KeyboardCorrectionSpikeRate: 0.2d,
                MouseMoveSampleCount: 10,
                MousePathLengthPx: 240d,
                MouseSpeedMeanPxPerSec: 48d,
                MouseJerkP95: 96d,
                MouseMicroCorrectionCount: 3,
                MouseMicroCorrectionRate: 0.6d,
                MouseWheelEventCount: 2,
                MouseScrollBurstCount: 1,
                MouseScrollBurstRate: 0.2d,
                MouseScrollSpeedMean: 220d,
                FocusObservationCount: 5,
                FocusSwitchCount: 1,
                FocusActiveAppCount: 1,
                FocusAppEntropy: 0.12d,
                IdleObservationCount: 5,
                IdleSecondsWithinWindow: 1.25d,
                MicroIdleCount: 1,
                MicroIdleSeconds: 0.5d,
                BreakSecondsWithinWindow: 0d,
                ShortIdleBoundaryObserved: false,
                NotificationToastsEnabled: true));

            Assert.Empty(innerStore.ReadInputSignalWindows(windowStartUtc, windowEndUtc.AddSeconds(1)));

            stateStore.RunFinalizerTick(new DateTimeOffset(2026, 03, 17, 10, 0, 8, TimeSpan.Zero));

            AgentInputSignalWindow persisted = Assert.Single(
                innerStore.ReadInputSignalWindows(windowStartUtc.AddSeconds(-1), windowEndUtc.AddSeconds(1)));

            Assert.Equal("agent-record-1", persisted.AgentInstanceId);
            Assert.Equal("session-record-1", persisted.SessionId);
            Assert.Equal(windowStartUtc, persisted.WindowStartUtc);
            Assert.Equal(windowEndUtc, persisted.WindowEndUtc);
            Assert.Equal("abcd1234", persisted.FocusAppHash);
            Assert.Equal(6, persisted.KeyboardKeydownCount);
            Assert.NotNull(persisted.KeyboardIkiMeanMs);
            Assert.NotNull(persisted.MouseScrollSpeedMean);
            Assert.Equal(125d, persisted.KeyboardIkiMeanMs.Value, precision: 6);
            Assert.Equal(220d, persisted.MouseScrollSpeedMean.Value, precision: 6);
            Assert.True(persisted.NotificationToastsEnabled);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
