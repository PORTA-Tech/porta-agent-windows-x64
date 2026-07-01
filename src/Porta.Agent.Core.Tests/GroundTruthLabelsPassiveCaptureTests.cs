using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Phase 1b — Step 2: passive capture of break-nudge outcomes as durable ground-truth labels.
public sealed class GroundTruthLabelsPassiveCaptureTests
{
    private static SqliteBrainStateStore NewStore(string dbPath) =>
        new SqliteBrainStateStore(
            new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
            new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
            dbPathOverride: dbPath);

    private static void SeedBreakNudge(SqliteBrainStateStore store, string interventionId, DateTimeOffset atUtc)
    {
        // WriteControlEvent for a break action creates a delivered, "decided" intervention,
        // freezing the decision-time context (trigger/confidence/contributors).
        store.WriteControlEvent(new AgentControlEvent(
            TimestampUtc: atUtc,
            Action: "break_suggestion",
            TriggerState: "fatigue",
            TriggerValue: 0.82d,
            Confidence: 0.91d,
            DurationSeconds: 300,
            EscalationLevel: "Suggest",
            Reversible: false,
            Reason: "test",
            Contributors: ["test"],
            InterventionId: interventionId,
            PolicyId: "break-policy-v1",
            Domain: "fatigue",
            WhatChanged: "Break suggestion displayed to user."));
        _ = store.ReadPendingInterventions("session-lbl", 10);
    }

    private static AgentInterventionAck Ack(string interventionId, string result, DateTimeOffset atUtc) =>
        new AgentInterventionAck(
            InterventionId: interventionId,
            SessionId: "session-lbl",
            AgentInstanceId: "agent-lbl",
            Result: result,
            ErrorCode: null,
            AppliedAtUtc: atUtc);

    private static (string Target, int Value, string Source, string Response, string InterventionId) ReadLatestLabel(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT label_target, label_value, label_source, response_kind, intervention_id
            FROM ground_truth_labels
            ORDER BY id DESC
            LIMIT 1;
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4));
    }

    [Fact]
    public void BreakDismiss_WritesNegativeBreakNeedLabel()
    {
        RunWithTempDb((store, dbPath) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SeedBreakNudge(store, "break-dismiss", now);

            Assert.True(store.AcknowledgeIntervention(Ack("break-dismiss", "dismissed", now.AddSeconds(3))));

            Assert.Equal(1, store.ReadLabelDiagnostics().TotalLabels);
            var label = ReadLatestLabel(dbPath);
            Assert.Equal("break_need", label.Target);
            Assert.Equal(-1, label.Value);
            Assert.Equal("break_nudge", label.Source);
            Assert.Equal("dismissed", label.Response);
            Assert.Equal("break-dismiss", label.InterventionId);
        });
    }

    [Fact]
    public void BreakAccept_WritesPositiveBreakNeedLabel()
    {
        RunWithTempDb((store, dbPath) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SeedBreakNudge(store, "break-accept", now);

            Assert.True(store.AcknowledgeIntervention(Ack("break-accept", "accepted", now.AddSeconds(3))));

            Assert.Equal(1, store.ReadLabelDiagnostics().TotalLabels);
            Assert.Equal(1, ReadLatestLabel(dbPath).Value);
        });
    }

    [Fact]
    public void AppliedResult_WritesNoLabel()
    {
        RunWithTempDb((store, dbPath) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SeedBreakNudge(store, "break-applied", now);

            // "applied" is a delivery acknowledgement, not a user choice -> not ground truth.
            Assert.True(store.AcknowledgeIntervention(Ack("break-applied", "applied", now.AddSeconds(3))));

            Assert.Equal(0, store.ReadLabelDiagnostics().TotalLabels);
        });
    }

    [Fact]
    public void RepeatedAck_DoesNotDoubleLabel()
    {
        RunWithTempDb((store, dbPath) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SeedBreakNudge(store, "break-once", now);

            AgentInterventionAck ack = Ack("break-once", "skipped", now.AddSeconds(3));
            Assert.True(store.AcknowledgeIntervention(ack));
            // Second ack of the already-resolved intervention: no open row -> no new label.
            store.AcknowledgeIntervention(ack);

            Assert.Equal(1, store.ReadLabelDiagnostics().TotalLabels);
        });
    }

    private static void RunWithTempDb(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-gtl-passive-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            body(NewStore(dbPath), dbPath);
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
