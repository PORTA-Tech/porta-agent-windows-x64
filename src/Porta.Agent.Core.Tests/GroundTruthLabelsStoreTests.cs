using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Phase 1b — Step 1: the durable, prune-exempt ground_truth_labels store.
public sealed class GroundTruthLabelsStoreTests
{
    private static SqliteBrainStateStore NewStore(string dbPath) =>
        new SqliteBrainStateStore(
            new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
            new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
            dbPathOverride: dbPath);

    private static GroundTruthLabel SampleLabel(DateTimeOffset createdAt) =>
        new GroundTruthLabel(
            CreatedAtUtc: createdAt,
            BucketSeq: 42,
            BucketTimestampUtc: createdAt.ToString("O"),
            LabelSource: "focus_check",
            PromptKind: "focused_now",
            ResponseKind: "focused",
            LabelTarget: "focus_state",
            LabelValue: 1,
            ResponseLatencyMs: 1200,
            InterventionId: string.Empty,
            FeaturesJson: "{\"typing_burst_intensity\":0.5}",
            PressureLevel: 0.4,
            OverloadLikelihood: 0.2,
            Confidence: 0.6,
            DominantState: "focus",
            CalibrationStage: "warm",
            TrustTier: "high",
            SchemaVer: 1);

    [Fact]
    public void RecordGroundTruthLabel_PersistsAndCounts()
    {
        RunWithTempDb(store =>
        {
            Assert.Equal(0, store.ReadLabelDiagnostics().TotalLabels);

            store.RecordGroundTruthLabel(SampleLabel(DateTimeOffset.UtcNow));

            AgentLabelDiagnostics diag = store.ReadLabelDiagnostics();
            Assert.Equal(1, diag.TotalLabels);
            Assert.Equal(1, diag.LabelsLast24h);
            Assert.Equal(1, diag.LabelsLast7d);
        });
    }

    [Fact]
    public void GroundTruthLabels_SurviveThe14DayPrune()
    {
        RunWithTempDb(store =>
        {
            // A label dated 30 days ago — well outside the 14-day retention window.
            store.RecordGroundTruthLabel(SampleLabel(DateTimeOffset.UtcNow.AddDays(-30)));
            Assert.Equal(1, store.ReadLabelDiagnostics().TotalLabels);

            // The first write after construction always triggers PruneIfNeeded
            // (_lastPruneUtc starts at MinValue), which deletes rows older than 14 days
            // from every pruned table.
            store.WriteDiagnostics(new AgentDiagnosticsSnapshot(
                TimestampUtc: DateTimeOffset.UtcNow,
                BackendConnected: true,
                ConsentActive: true,
                Paused: false,
                LastError: null,
                PolicyVersion: "1",
                SchemaVersion: "1",
                AgentVersion: "1",
                LastEventTimestampUtc: null,
                LastBackendOkUtc: null));

            // The 30-day-old label MUST still be present — ground_truth_labels is exempt.
            Assert.Equal(1, store.ReadLabelDiagnostics().TotalLabels);
        });
    }

    [Fact]
    public void GroundTruthLabels_ClearedByDeleteLocalData()
    {
        RunWithTempDb(store =>
        {
            store.RecordGroundTruthLabel(SampleLabel(DateTimeOffset.UtcNow));
            Assert.Equal(1, store.ReadLabelDiagnostics().TotalLabels);

            // A user-initiated full data reset SHOULD clear labels (consent-correct).
            store.DeleteLocalData();

            Assert.Equal(0, store.ReadLabelDiagnostics().TotalLabels);
        });
    }

    [Fact]
    public void ReadLabelPromptCadence_CountsShownPromptsFromPromptEvents()
    {
        RunWithTempDb(store =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            // Two prompts shown — both count toward the cadence cap whether or not they were answered.
            store.RecordPromptShown("focus_state", "focused_now", now.AddMinutes(-10));
            store.RecordPromptShown("distraction", "on_task_now", now.AddMinutes(-5));

            AgentLabelPromptCadence cadence = store.ReadLabelPromptCadence();
            Assert.Equal(2, cadence.ActivePromptsLast24h);
            Assert.Equal(2, cadence.UnansweredPromptsLast24h);
            Assert.NotNull(cadence.LastActivePromptAtUtc);
        });
    }

    [Fact]
    public void ResolvePromptAnswered_ClearsUnansweredStreakForTarget()
    {
        RunWithTempDb(store =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            store.RecordPromptShown("focus_state", "focused_now", now.AddMinutes(-20));
            store.RecordPromptShown("focus_state", "focused_now", now.AddMinutes(-10));

            // User answers: the most recent shown prompt for the target flips to 'answered'.
            store.ResolvePromptAnswered("focus_state", now);

            AgentLabelPromptCadence cadence = store.ReadLabelPromptCadence();
            Assert.Equal(2, cadence.ActivePromptsLast24h);      // still two shown (cap counts shown)
            Assert.Equal(1, cadence.UnansweredPromptsLast24h);  // one now answered
        });
    }

    [Fact]
    public void ReadLabelPromptCadence_IgnoresPromptsOlderThan24h()
    {
        RunWithTempDb(store =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            store.RecordPromptShown("focus_state", "focused_now", now.AddDays(-2));
            store.RecordPromptShown("focus_state", "focused_now", now.AddMinutes(-5));

            AgentLabelPromptCadence cadence = store.ReadLabelPromptCadence();
            Assert.Equal(1, cadence.ActivePromptsLast24h);
            Assert.Equal(1, cadence.UnansweredPromptsLast24h);
        });
    }

    private static void RunWithTempDb(Action<SqliteBrainStateStore> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-gtl-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            body(NewStore(dbPath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
