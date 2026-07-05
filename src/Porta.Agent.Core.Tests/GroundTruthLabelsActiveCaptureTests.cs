using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Phase 1b — Step 3d: the active one-tap path. controller.RecordFocusLabel (what POST /v1/labels/focus
// calls) maps a widget answer to a durable ground-truth label with the correct target/source/response/
// value. This is the endpoint round-trip complement to the pure LabelResponseMap test.
public sealed class GroundTruthLabelsActiveCaptureTests
{
    [Theory]
    [InlineData("focus_state", "focused_now", "yes", "focus_check", "focused", 1)]
    [InlineData("focus_state", "focused_now", "no", "focus_check", "not_focused", -1)]
    [InlineData("distraction", "on_task_now", "break", "distraction_check", "off_task", 1)]
    [InlineData("distraction", "on_task_now", "working", "distraction_check", "on_task", -1)]
    [InlineData("overload", "overload_now", "yes", "overload_check", "overloaded", 1)]
    [InlineData("overload", "overload_now", "no", "overload_check", "fine", -1)]
    public void RecordFocusLabel_StoresLabelWithMappedTargetSourceValue(
        string target,
        string promptKind,
        string answer,
        string expectedSource,
        string expectedResponse,
        int expectedValue)
    {
        RunWithController((controller, dbPath) =>
        {
            bool recorded = controller.RecordFocusLabel(new AgentFocusLabelRequest(
                LabelTarget: target,
                PromptKind: promptKind,
                Answer: answer,
                ResponseLatencyMs: 1200));

            Assert.True(recorded);
            var label = ReadLatestLabel(dbPath);
            Assert.Equal(target, label.Target);
            Assert.Equal(expectedSource, label.Source);
            Assert.Equal(expectedResponse, label.Response);
            Assert.Equal(expectedValue, label.Value);
            // Active labels come from the widget, not an intervention ack.
            Assert.Equal(string.Empty, label.InterventionId);
        });
    }

    [Fact]
    public void RecordFocusLabel_UnrecognizedAnswer_RecordsNothing()
    {
        RunWithController((controller, dbPath) =>
        {
            bool recorded = controller.RecordFocusLabel(new AgentFocusLabelRequest(
                LabelTarget: "focus_state",
                PromptKind: "focused_now",
                Answer: "banana",
                ResponseLatencyMs: 0));

            Assert.False(recorded);
            Assert.Equal(0, CountLabels(dbPath));
        });
    }

    [Fact]
    public void GetLabelDiagnostics_ReportsCountsAndNetValueByTarget()
    {
        RunWithController((controller, _) =>
        {
            // focus: one "right" (+1) and one "wrong" (-1) -> count 2, net 0
            Assert.True(controller.RecordFocusLabel(new AgentFocusLabelRequest("focus_state", "focused_now", "yes", 0)));
            Assert.True(controller.RecordFocusLabel(new AgentFocusLabelRequest("focus_state", "focused_now", "no", 0)));
            // overload: one "right" (+1) -> count 1, net 1
            Assert.True(controller.RecordFocusLabel(new AgentFocusLabelRequest("overload", "overload_now", "yes", 0)));

            AgentLabelDiagnostics d = controller.GetLabelDiagnostics();
            Assert.Equal(3, d.TotalLabels);
            Assert.Equal(3, d.LabelsLast24h);

            AgentLabelTargetCount focus = d.Last7dByTarget.Single(t => t.Target == "focus_state");
            Assert.Equal(2, focus.Count);
            Assert.Equal(0, focus.NetValue);

            AgentLabelTargetCount overload = d.Last7dByTarget.Single(t => t.Target == "overload");
            Assert.Equal(1, overload.Count);
            Assert.Equal(1, overload.NetValue);
        });
    }

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

    private static int CountLabels(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ground_truth_labels;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void RunWithController(Action<AgentCoreController, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-gtl-active-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var inner = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            var stateStore = new BucketFinalizerStateStore(
                inner,
                () => new DateTimeOffset(2026, 07, 05, 12, 0, 0, TimeSpan.Zero));
            var controller = new AgentCoreController(stateStore);
            body(controller, dbPath);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
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
