using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Workstream D4 — the check-in loop generates Phase-1b ground truth for free: the morning goal and
// the yes/partly/not_yet midday answer are written to ground_truth_labels by the controller, so they
// land regardless of which caller drove the check-in.
public sealed class DailyGoalsGroundTruthTests
{
    [Fact]
    public void SetMorningGoal_RecordsDailyGoalGroundTruthLabel()
    {
        RunWithController((controller, dbPath) =>
        {
            AgentDailyGoal? goal = controller.SetMorningGoal("ship the release", "keep_on_track");
            Assert.NotNull(goal);

            var label = ReadLatestLabel(dbPath);
            Assert.Equal("daily_goal", label.Target);
            Assert.Equal("morning_goal", label.Source);
            Assert.Equal("keep_on_track", label.Response); // the tracking preference
            Assert.Equal(0, label.Value);                  // a stated goal is neutral, not right/wrong
            Assert.Equal(string.Empty, label.InterventionId);
        });
    }

    [Fact]
    public void SetMorningGoal_EmptyGoalText_RecordsNoLabel()
    {
        RunWithController((controller, dbPath) =>
        {
            controller.SetMorningGoal("   ", "remind_me");
            Assert.Equal(0, CountLabels(dbPath));
        });
    }

    [Theory]
    [InlineData("yes", 1)]
    [InlineData("partly", 0)]
    [InlineData("not_yet", -1)]
    public void RecordMiddayAnswer_RecordsGoalProgressGroundTruthLabel(string answer, int expectedValue)
    {
        RunWithController((controller, dbPath) =>
        {
            controller.SetMorningGoal("ship the release", "just_track");
            AgentDailyGoal? updated = controller.RecordMiddayAnswer(answer);
            Assert.NotNull(updated);

            var label = ReadLatestLabel(dbPath);
            Assert.Equal("goal_progress", label.Target);
            Assert.Equal("midday_check", label.Source);
            Assert.Equal(answer, label.Response);
            Assert.Equal(expectedValue, label.Value);
        });
    }

    [Fact]
    public void RecordMiddayAnswer_UnknownAnswer_RecordsNoProgressLabel()
    {
        RunWithController((controller, dbPath) =>
        {
            controller.SetMorningGoal("ship the release", "just_track"); // one label (the morning goal)
            Assert.Null(controller.RecordMiddayAnswer("maybe"));         // store rejects → no label
            Assert.Equal(1, CountLabels(dbPath));
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
        string dir = Path.Combine(Path.GetTempPath(), "porta-goal-gtl-tests", Guid.NewGuid().ToString("N"));
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
