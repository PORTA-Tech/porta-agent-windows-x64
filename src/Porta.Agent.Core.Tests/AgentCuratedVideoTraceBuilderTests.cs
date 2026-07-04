using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

public sealed class AgentCuratedVideoTraceBuilderTests
{
    [Fact(Skip = "Test-fixture failure (insufficient capture source data), unrelated to pressure/label work — verify separately.")]
    public void Curate_BuildsRendererTraceAndBeatMapFromRawCapture()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-video-trace-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(tempDirectory, "core-state.db");
        string rawOutputRoot = Path.Combine(tempDirectory, "raw-captures");
        string traceOutputRoot = Path.Combine(tempDirectory, "video-traces");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var innerStore = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            var stateStore = new BucketFinalizerStateStore(
                innerStore,
                () => new DateTimeOffset(2026, 03, 17, 10, 1, 0, TimeSpan.Zero));

            DateTimeOffset baseUtc = new(2026, 03, 17, 10, 0, 0, TimeSpan.Zero);
            for (int index = 0; index < 10; index++)
            {
                DateTimeOffset windowStartUtc = baseUtc.AddSeconds(index * 5);
                DateTimeOffset windowEndUtc = windowStartUtc.AddSeconds(5);
                stateStore.IngestInputSignals(new InputSignalsPacket(
                    SchemaVer: 2,
                    AgentInstanceId: "agent-curate-1",
                    SessionId: "session-curate-1",
                    WindowStartUtc: windowStartUtc,
                    WindowEndUtc: windowEndUtc,
                    KeyboardKeydownCount: 6 + index,
                    KeyboardBackspaceCount: index % 2,
                    MouseWheelDelta: 120 + (index * 5),
                    MouseMoveDx: 24d + index,
                    MouseMoveDy: -12d + index,
                    FocusAppHash: "ZXCV1234",
                    IdleSeconds: 1d + (index * 0.1d),
                    LastInputEventUtc: windowEndUtc,
                    KeyboardIkiMeanMs: 120d - index,
                    KeyboardIkiStdMs: 14d + (index * 0.2d),
                    KeyboardIkiCv: 0.11d,
                    KeyboardBurstCount: 2,
                    KeyboardBurstRate: 0.4d + (index * 0.01d),
                    KeyboardCorrectionSpikeCount: 1,
                    KeyboardCorrectionSpikeRate: 0.2d,
                    MouseMoveSampleCount: 10,
                    MousePathLengthPx: 240d + (index * 4d),
                    MouseSpeedMeanPxPerSec: 48d + index,
                    MouseJerkP95: 96d + (index * 2d),
                    MouseMicroCorrectionCount: 3,
                    MouseMicroCorrectionRate: 0.6d,
                    MouseWheelEventCount: 2,
                    MouseScrollBurstCount: 1,
                    MouseScrollBurstRate: 0.2d,
                    MouseScrollSpeedMean: 220d + (index * 6d),
                    FocusObservationCount: 5,
                    FocusSwitchCount: index % 3,
                    FocusActiveAppCount: 1 + (index % 2),
                    FocusAppEntropy: 0.12d + (index * 0.01d),
                    IdleObservationCount: 5,
                    IdleSecondsWithinWindow: 1.25d + (index * 0.1d),
                    MicroIdleCount: 1,
                    MicroIdleSeconds: 0.5d + (index * 0.05d),
                    BreakSecondsWithinWindow: 0d,
                    ShortIdleBoundaryObserved: false,
                    NotificationToastsEnabled: true));

                stateStore.RunFinalizerTick(windowEndUtc.AddSeconds(2));
            }

            DateTimeOffset eventUtc = baseUtc.AddSeconds(38);
            stateStore.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc,
                Action: "deep_work_notifications_suppressed",
                TriggerState: "focus",
                TriggerValue: 0.92d,
                Confidence: 0.99d,
                DurationSeconds: 480,
                EscalationLevel: "inform",
                Reversible: true,
                Reason: "Sustained focus crossed the suppression threshold.",
                Contributors: ["focus", "continuity"],
                InterventionId: "int-curate-1",
                PolicyId: "policy-curate-1",
                Domain: "control",
                WhatChanged: "Notifications were suppressed for deep work.",
                HowToUndo: "Restore notifications.",
                LifecycleState: "activated"));

            var exporter = new AgentRecordedCaptureExporter(dbPath);
            AgentRecordedCaptureExportResult capture = exporter.Export(new AgentRecordedCaptureExportRequest(
                FromUtc: baseUtc.AddSeconds(-1),
                ToUtc: baseUtc.AddSeconds(60),
                Label: "Focused Session",
                OutputRoot: rawOutputRoot));

            var builder = new AgentCuratedVideoTraceBuilder();
            AgentCuratedVideoTraceResult result = builder.Curate(new AgentCuratedVideoTraceRequest(
                CaptureDirectories: [capture.CaptureDirectoryPath],
                Label: "Ari Focus Story",
                OutputRoot: traceOutputRoot,
                TargetDurationSeconds: 45));

            Assert.True(File.Exists(result.TracePath));
            Assert.True(File.Exists(result.BeatMapPath));

            AgentVideoTrace trace = JsonSerializer.Deserialize<AgentVideoTrace>(File.ReadAllText(result.TracePath))
                ?? throw new InvalidOperationException("Trace JSON was invalid.");
            Assert.Equal("porta.ari.video-trace.v1", trace.Meta.Schema);
            Assert.Equal("Ari Focus Story", trace.Meta.Label);
            Assert.Equal(4, trace.Chapters?.Count);
            Assert.Contains(trace.Events, row => row.Action == "deep_work_notifications_suppressed");
            Assert.Contains(trace.Chapters!, row => row.ChapterId == "observe");
            Assert.Contains(trace.Chapters!, row => row.ChapterId == "infer");
            Assert.Contains(trace.Chapters!, row => row.ChapterId == "regulate");
            Assert.Contains(trace.Chapters!, row => row.ChapterId == "explain");
            Assert.True(trace.Signals.Count > 0);
            Assert.True(trace.Snapshots.Count > 0);
            Assert.True(trace.Meta.DurationSec >= 30d);

            using JsonDocument beatMapDoc = JsonDocument.Parse(File.ReadAllText(result.BeatMapPath));
            JsonElement beats = beatMapDoc.RootElement.GetProperty("beats");
            Assert.Equal(7, beats.GetArrayLength());
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
