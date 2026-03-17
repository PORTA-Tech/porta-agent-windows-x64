using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Outputs;
using System.Text.Json;

namespace Porta.Agent.Core.Tests;

public sealed class AgentRecordedCaptureExporterTests
{
    [Fact]
    public void Export_WritesRawCaptureFilesAndInventory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-recorded-capture-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(tempDirectory, "core-state.db");
        string outputRoot = Path.Combine(tempDirectory, "exports");
        Directory.CreateDirectory(tempDirectory);

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
                AgentInstanceId: "agent-export-1",
                SessionId: "session-export-1",
                WindowStartUtc: windowStartUtc,
                WindowEndUtc: windowEndUtc,
                KeyboardKeydownCount: 8,
                KeyboardBackspaceCount: 1,
                MouseWheelDelta: 120,
                MouseMoveDx: 30d,
                MouseMoveDy: -16d,
                FocusAppHash: "QWER1234",
                IdleSeconds: 1.0d,
                LastInputEventUtc: windowEndUtc,
                KeyboardIkiMeanMs: 118d,
                KeyboardIkiStdMs: 10d,
                KeyboardIkiCv: 0.084d,
                KeyboardBurstCount: 2,
                KeyboardBurstRate: 0.4d,
                KeyboardCorrectionSpikeCount: 1,
                KeyboardCorrectionSpikeRate: 0.2d,
                MouseMoveSampleCount: 10,
                MousePathLengthPx: 280d,
                MouseSpeedMeanPxPerSec: 56d,
                MouseJerkP95: 92d,
                MouseMicroCorrectionCount: 2,
                MouseMicroCorrectionRate: 0.4d,
                MouseWheelEventCount: 2,
                MouseScrollBurstCount: 1,
                MouseScrollBurstRate: 0.2d,
                MouseScrollSpeedMean: 210d,
                FocusObservationCount: 5,
                FocusSwitchCount: 1,
                FocusActiveAppCount: 1,
                FocusAppEntropy: 0.11d,
                IdleObservationCount: 5,
                IdleSecondsWithinWindow: 1.0d,
                MicroIdleCount: 1,
                MicroIdleSeconds: 0.4d,
                BreakSecondsWithinWindow: 0d,
                ShortIdleBoundaryObserved: false,
                NotificationToastsEnabled: true));

            stateStore.RunFinalizerTick(new DateTimeOffset(2026, 03, 17, 10, 0, 8, TimeSpan.Zero));
            stateStore.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: new DateTimeOffset(2026, 03, 17, 10, 0, 9, TimeSpan.Zero),
                Action: "deep_work_notifications_suppressed",
                TriggerState: "focus",
                TriggerValue: 0.91d,
                Confidence: 0.99d,
                DurationSeconds: 480,
                EscalationLevel: "inform",
                Reversible: true,
                Reason: "Sustained focus crossed the suppression threshold.",
                Contributors: ["focus", "continuity"],
                InterventionId: "int-1",
                PolicyId: "policy-1",
                Domain: "control"));
            stateStore.WriteDiagnostics(new AgentDiagnosticsSnapshot(
                TimestampUtc: new DateTimeOffset(2026, 03, 17, 10, 0, 10, TimeSpan.Zero),
                BackendConnected: true,
                ConsentActive: true,
                Paused: false,
                LastError: null,
                PolicyVersion: "policy-1",
                SchemaVersion: "v1",
                AgentVersion: "1.0.0",
                LastEventTimestampUtc: new DateTimeOffset(2026, 03, 17, 10, 0, 9, TimeSpan.Zero),
                LastBackendOkUtc: null));
            stateStore.WriteConsent(new AgentConsentRecord(
                TimestampUtc: new DateTimeOffset(2026, 03, 17, 9, 59, 0, TimeSpan.Zero),
                Active: true,
                Version: "v2",
                DisclosureHash: "hash-1"));
            stateStore.WriteSettings(new AgentSettingsRecord(
                TimestampUtc: new DateTimeOffset(2026, 03, 17, 9, 59, 30, TimeSpan.Zero),
                PrivacyLevel: "balanced",
                RegulationIntensity: "adaptive",
                DisabledCategories: [],
                NotificationReductionEnabled: true,
                FocusModeEnabled: true,
                AnimationReductionEnabled: false,
                TemporaryAppRestrictionEnabled: false,
                BaselineRecalibrationStartedUtc: null,
                BaselineRecalibrationUntilUtc: null));

            var exporter = new AgentRecordedCaptureExporter(dbPath);
            AgentRecordedCaptureExportResult result = exporter.Export(new AgentRecordedCaptureExportRequest(
                FromUtc: windowStartUtc.AddSeconds(-1),
                ToUtc: windowEndUtc.AddSeconds(10),
                Label: "Focused Session",
                Notes: "Focused coding interval with one real suppression event.",
                OutputRoot: outputRoot));

            string captureDirectoryPath = result.CaptureDirectoryPath;
            Assert.True(Directory.Exists(captureDirectoryPath));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "capture-metadata.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "capture-summary.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "signal-samples.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "input-signal-windows.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "timeline-snapshots.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "events.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "diagnostics-snapshots.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "settings-record.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "consent-record.json")));
            Assert.True(File.Exists(Path.Combine(captureDirectoryPath, "session-notes.txt")));

            AgentRecordedCaptureSummary summary = JsonSerializer.Deserialize<AgentRecordedCaptureSummary>(
                File.ReadAllText(Path.Combine(captureDirectoryPath, "capture-summary.json")))
                ?? throw new InvalidOperationException("Capture summary JSON was invalid.");
            Assert.Equal("Focused Session", summary.Label);
            Assert.Equal(1, summary.InputWindowCount);
            Assert.Equal(1, summary.SignalSampleCount);
            Assert.True(summary.SnapshotCount > 0);
            Assert.Equal(1, summary.EventCount);
            Assert.Equal(1, summary.DiagnosticsSnapshotCount);
            Assert.True(summary.HasInterventionEvents);
            Assert.Equal("input_signal_window", summary.SignalSourceKind);
            Assert.Contains("agent-export-1", summary.AgentInstanceIds);
            Assert.Contains("session-export-1", summary.SessionIds);
            Assert.Equal(1, summary.EventActionCounts["deep_work_notifications_suppressed"]);

            IReadOnlyList<AgentRecordedCaptureInventoryItem> inventory = exporter.ReadInventory(outputRoot);
            AgentRecordedCaptureInventoryItem item = Assert.Single(inventory);
            Assert.Equal(result.CaptureId, item.CaptureId);
            Assert.Equal(summary.InputWindowCount, item.InputWindowCount);
            Assert.Equal(summary.SignalSampleCount, item.SignalSampleCount);
            Assert.Equal(summary.EventCount, item.EventCount);

            string notes = File.ReadAllText(Path.Combine(captureDirectoryPath, "session-notes.txt"));
            Assert.Contains("one real suppression event", notes);
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

    [Fact]
    public void Export_FallsBackToHistoricalInputBucketsWhenRawWindowsUnavailable()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-recorded-capture-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(tempDirectory, "core-state.db");
        string outputRoot = Path.Combine(tempDirectory, "exports");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var innerStore = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            var stateStore = new BucketFinalizerStateStore(
                innerStore,
                () => new DateTimeOffset(2026, 03, 17, 11, 0, 8, TimeSpan.Zero));

            DateTimeOffset windowStartUtc = new(2026, 03, 17, 11, 0, 0, TimeSpan.Zero);
            DateTimeOffset windowEndUtc = new(2026, 03, 17, 11, 0, 5, TimeSpan.Zero);

            stateStore.IngestInputSignals(new InputSignalsPacket(
                SchemaVer: 2,
                AgentInstanceId: "agent-export-2",
                SessionId: "session-export-2",
                WindowStartUtc: windowStartUtc,
                WindowEndUtc: windowEndUtc,
                KeyboardKeydownCount: 11,
                KeyboardBackspaceCount: 2,
                MouseWheelDelta: 80,
                MouseMoveDx: 18d,
                MouseMoveDy: -9d,
                FocusAppHash: "ASDF5678",
                IdleSeconds: 0.8d,
                LastInputEventUtc: windowEndUtc,
                KeyboardIkiMeanMs: 112d,
                KeyboardIkiStdMs: 8d,
                KeyboardIkiCv: 0.07d,
                KeyboardBurstCount: 3,
                KeyboardBurstRate: 0.55d,
                KeyboardCorrectionSpikeCount: 1,
                KeyboardCorrectionSpikeRate: 0.18d,
                MouseMoveSampleCount: 9,
                MousePathLengthPx: 180d,
                MouseSpeedMeanPxPerSec: 36d,
                MouseJerkP95: 78d,
                MouseMicroCorrectionCount: 2,
                MouseMicroCorrectionRate: 0.32d,
                MouseWheelEventCount: 1,
                MouseScrollBurstCount: 1,
                MouseScrollBurstRate: 0.2d,
                MouseScrollSpeedMean: 160d,
                FocusObservationCount: 5,
                FocusSwitchCount: 2,
                FocusActiveAppCount: 2,
                FocusAppEntropy: 0.22d,
                IdleObservationCount: 5,
                IdleSecondsWithinWindow: 0.8d,
                MicroIdleCount: 1,
                MicroIdleSeconds: 0.2d,
                BreakSecondsWithinWindow: 0d,
                ShortIdleBoundaryObserved: false,
                NotificationToastsEnabled: false));

            stateStore.RunFinalizerTick(new DateTimeOffset(2026, 03, 17, 11, 0, 8, TimeSpan.Zero));
            stateStore.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: new DateTimeOffset(2026, 03, 17, 11, 0, 9, TimeSpan.Zero),
                Action: "break_suggestion",
                TriggerState: "focus",
                TriggerValue: 0.48d,
                Confidence: 0.71d,
                DurationSeconds: 300,
                EscalationLevel: "inform",
                Reversible: true,
                Reason: "Ari suggested a short break.",
                Contributors: ["focus", "fatigue"],
                InterventionId: "int-2",
                PolicyId: "policy-2",
                Domain: "control"));

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE input_signal_windows;";
                drop.ExecuteNonQuery();
            }

            var exporter = new AgentRecordedCaptureExporter(dbPath);
            AgentRecordedCaptureExportResult result = exporter.Export(new AgentRecordedCaptureExportRequest(
                FromUtc: windowStartUtc.AddSeconds(-1),
                ToUtc: windowEndUtc.AddSeconds(10),
                Label: "Historical Session",
                OutputRoot: outputRoot));

            AgentRecordedCaptureSummary summary = JsonSerializer.Deserialize<AgentRecordedCaptureSummary>(
                File.ReadAllText(Path.Combine(result.CaptureDirectoryPath, "capture-summary.json")))
                ?? throw new InvalidOperationException("Capture summary JSON was invalid.");
            IReadOnlyList<AgentRecordedSignalSample> signals = JsonSerializer.Deserialize<List<AgentRecordedSignalSample>>(
                File.ReadAllText(Path.Combine(result.CaptureDirectoryPath, "signal-samples.json")))
                ?? throw new InvalidOperationException("Signal sample JSON was invalid.");

            Assert.Equal(0, summary.InputWindowCount);
            Assert.True(summary.SignalSampleCount > 0);
            Assert.Equal("input_bucket", summary.SignalSourceKind);
            AgentRecordedSignalSample signal = Assert.Single(signals);
            Assert.Equal("input_bucket", signal.SignalKind);
            Assert.NotNull(signal.TypingBurstIntensity);
            Assert.NotNull(signal.ScrollVelocityIndex);
            Assert.NotNull(signal.FocusSwitchRate);
            Assert.Equal("asdf5678", signal.FocusAppHash);
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
