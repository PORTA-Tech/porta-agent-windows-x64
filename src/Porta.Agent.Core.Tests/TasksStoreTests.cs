using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Workstream 4 — the timed-tasks store: lifecycle round-trips, the ONE-active invariant
// (start = switch, never an error), bucket attribution by attention kind, extension math,
// the 30-day cleanup, and the local-data reset wipe.
public sealed class TasksStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 07, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ThenRead_ReturnsPendingTask()
    {
        RunWithStore((store, _) =>
        {
            AgentTask? created = store.CreateTask("write digest section", 3600, Now);
            Assert.NotNull(created);
            Assert.Equal("pending", created!.State);
            Assert.Equal(3600, created.PlannedSeconds);

            IReadOnlyList<AgentTask> tasks = store.ReadTasks(Now.AddHours(-1), Now);
            Assert.Single(tasks);
            Assert.Equal(created.Id, tasks[0].Id);
        });
    }

    [Fact]
    public void Create_RejectsEmptyTitleAndAbsurdDurations()
    {
        RunWithStore((store, _) =>
        {
            Assert.Null(store.CreateTask("   ", 3600, Now));
            Assert.Null(store.CreateTask("too short", 30, Now));
            Assert.Null(store.CreateTask("too long", 9 * 3600, Now));
        });
    }

    [Fact]
    public void StartSecondTask_PausesTheFirst_OneActiveInvariant()
    {
        RunWithStore((store, _) =>
        {
            AgentTask a = store.CreateTask("task a", 3600, Now)!;
            AgentTask b = store.CreateTask("task b", 1800, Now)!;

            Assert.Equal("active", store.ActOnTask(a.Id, "start", Now)!.State);
            AgentTask? bStarted = store.ActOnTask(b.Id, "start", Now.AddMinutes(10));
            Assert.Equal("active", bStarted!.State);

            IReadOnlyList<AgentTask> tasks = store.ReadTasks(Now.AddHours(-1), Now);
            Assert.Single(tasks, t => t.State == "active");
            Assert.Equal("pending", tasks.Single(t => t.Id == a.Id).State);
        });
    }

    [Fact]
    public void Attribution_SplitsByAttentionKind_AndStopsWhenClosed()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("deep work", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);

            Attribute(dbPath, 5d, "focus");
            Attribute(dbPath, 5d, "in_meeting");
            Attribute(dbPath, 5d, "social_media_distraction");
            Attribute(dbPath, 5d, "off_task_distraction");
            Attribute(dbPath, 5d, "away"); // contributes nothing

            AgentTask running = store.ReadTasks(Now.AddHours(-1), Now).Single();
            Assert.Equal(20d, running.ActiveSeconds, precision: 6);
            Assert.Equal(10d, running.FocusedSeconds, precision: 6);
            Assert.Equal(10d, running.OfftaskSeconds, precision: 6);

            store.ActOnTask(task.Id, "done", Now.AddMinutes(30));
            Attribute(dbPath, 5d, "focus"); // no active task anymore → no-op
            AgentTask closed = store.ReadTasks(Now.AddHours(-1), Now).Single();
            Assert.Equal(20d, closed.ActiveSeconds, precision: 6);
            Assert.Equal("done", closed.State);
            Assert.NotNull(closed.EndedAtUtc);
        });
    }

    [Fact]
    public void Extend_AddsFifteenMinutes_OnlyWhileActive()
    {
        RunWithStore((store, _) =>
        {
            AgentTask task = store.CreateTask("task", 1800, Now)!;
            Assert.Null(store.ActOnTask(task.Id, "extend", Now)); // pending → not extendable

            store.ActOnTask(task.Id, "start", Now);
            AgentTask extended = store.ActOnTask(task.Id, "extend", Now)!;
            Assert.Equal(SqliteBrainStateStore.TaskExtendSeconds, extended.ExtendedSeconds);
            AgentTask extendedTwice = store.ActOnTask(task.Id, "extend", Now)!;
            Assert.Equal(2 * SqliteBrainStateStore.TaskExtendSeconds, extendedTwice.ExtendedSeconds);
        });
    }

    [Fact]
    public void UnknownActionOrId_ReturnsNull()
    {
        RunWithStore((store, _) =>
        {
            AgentTask task = store.CreateTask("task", 1800, Now)!;
            Assert.Null(store.ActOnTask(task.Id, "explode", Now));
            Assert.Null(store.ActOnTask(9999, "start", Now));
        });
    }

    [Fact]
    public void ClosedTasksFromEarlierDays_DropOffTheTodayList()
    {
        RunWithStore((store, _) =>
        {
            AgentTask old = store.CreateTask("yesterday's", 1800, Now.AddDays(-1))!;
            store.ActOnTask(old.Id, "start", Now.AddDays(-1));
            store.ActOnTask(old.Id, "done", Now.AddDays(-1).AddHours(1));
            AgentTask fresh = store.CreateTask("today's", 1800, Now)!;

            // Day start = today: yesterday's DONE task is gone, open tasks always show.
            IReadOnlyList<AgentTask> tasks = store.ReadTasks(Now.AddHours(-2), Now);
            Assert.Single(tasks);
            Assert.Equal(fresh.Id, tasks[0].Id);
        });
    }

    [Fact]
    public void OldClosedRows_AreCleanedUpAfterRetention()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask ancient = store.CreateTask("ancient", 1800, Now.AddDays(-40))!;
            store.ActOnTask(ancient.Id, "abandon", Now.AddDays(-40));
            store.CreateTask("current", 1800, Now);

            _ = store.ReadTasks(Now.AddHours(-1), Now); // read piggybacks the cleanup
            Assert.Equal(1, CountRows(dbPath));
        });
    }

    [Fact]
    public void FullLocalDataReset_WipesTasks()
    {
        RunWithStore((store, dbPath) =>
        {
            store.CreateTask("a", 1800, Now);
            store.CreateTask("b", 3600, Now);
            store.DeleteLocalData();
            Assert.Equal(0, CountRows(dbPath));
        });
    }

    private static void Attribute(string dbPath, double seconds, string kind)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        SqliteBrainStateStore.AttributeBucketToActiveTask(conn, tx, seconds, kind);
        tx.Commit();
    }

    private static int CountRows(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tasks;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-task-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            body(store, dbPath);
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
            }
        }
    }
}
