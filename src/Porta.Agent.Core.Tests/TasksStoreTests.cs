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

    // Complaint 3 (2026-08-11) — the close-out card's "Tomorrow" has to persist, or card four is
    // theatre. Schema v25 adds tasks.deferred_until_day; these two pin the contract that matters.

    [Fact]
    public void Defer_RecordsTheDay_AndLeavesTheTaskOpen()
    {
        RunWithStore((store, _) =>
        {
            AgentTask task = store.CreateTask("client revisions", 3600, Now)!;

            AgentTask? deferred = store.ActOnTask(task.Id, "defer", Now, deferUntilDay: "2026-07-08");

            Assert.NotNull(deferred);
            Assert.Equal("2026-07-08", deferred!.DeferredUntilDay);
            // The safety property: deferring is a statement about the PLAN, never a state change,
            // so moving work to tomorrow can never lose it.
            Assert.Equal("pending", deferred.State);
            Assert.Null(deferred.EndedAtUtc);

            Assert.Equal("2026-07-08", store.ReadTasks(Now.AddHours(-1), Now).Single().DeferredUntilDay);
        });
    }

    [Fact]
    public void Defer_AppliesToTheRunningTask_AndDoesNotStopItsClock()
    {
        RunWithStore((store, _) =>
        {
            AgentTask task = store.CreateTask("client revisions", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);

            AgentTask? deferred = store.ActOnTask(task.Id, "defer", Now, deferUntilDay: "2026-07-08");

            // Still active: if the user keeps working on it the measured time is still real, and
            // still lands on this task. Deferring says when it is due back, not that it stopped.
            Assert.Equal("active", deferred!.State);
            Assert.Equal("2026-07-08", deferred.DeferredUntilDay);
        });
    }

    [Fact]
    public void ExistingTasks_HaveNoDeferral()
    {
        // The migration is additive and defaulted, so everything already stored reads as "not
        // deferred" rather than as missing.
        RunWithStore((store, _) =>
        {
            AgentTask task = store.CreateTask("write digest section", 3600, Now)!;
            Assert.Equal(string.Empty, task.DeferredUntilDay);
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

            // Chat 3 (2026-08-24) — active_seconds was 20 here: every kind except away was
            // credited as work, so the two distraction buckets counted BOTH as off-task and as
            // work on the task at the same time. That double-count is what made a film read as
            // 4.9 hours. Only focus and in_meeting are work now; the distraction seconds are
            // still recorded, still off-task, and no longer called work.
            AgentTask running = store.ReadTasks(Now.AddHours(-1), Now).Single();
            Assert.Equal(10d, running.ActiveSeconds, precision: 6);
            Assert.Equal(10d, running.FocusedSeconds, precision: 6);
            Assert.Equal(10d, running.OfftaskSeconds, precision: 6);

            store.ActOnTask(task.Id, "done", Now.AddMinutes(30));
            Attribute(dbPath, 5d, "focus"); // no active task anymore → no-op
            AgentTask closed = store.ReadTasks(Now.AddHours(-1), Now).Single();
            Assert.Equal(10d, closed.ActiveSeconds, precision: 6);
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
            // Step 9a — retention is 400 days, not 30. Measured first: a task row costs 135 bytes,
            // three a day, so a year is about 148 KB against the ~800 MB the 5-second capture
            // pipeline holds. Task history is effectively free and the projects page needs it.
            AgentTask ancient = store.CreateTask("ancient", 1800, Now.AddDays(-500))!;
            store.ActOnTask(ancient.Id, "abandon", Now.AddDays(-500));
            store.CreateTask("current", 1800, Now);

            _ = store.ReadTasks(Now.AddHours(-1), Now); // read piggybacks the cleanup
            Assert.Equal(1, CountRows(dbPath));
        });
    }

    // The point of the change, asserted from the other side: a task closed last quarter is still
    // on disk. Before 9a it was deleted at 30 days, which capped any project history at a month.
    [Fact]
    public void ClosedRowsInsideTheYear_Survive()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask lastQuarter = store.CreateTask("shipped in May", 1800, Now.AddDays(-90))!;
            store.ActOnTask(lastQuarter.Id, "done", Now.AddDays(-90));

            _ = store.ReadTasks(Now.AddHours(-1), Now);

            Assert.Equal(1, CountRows(dbPath));
        });
    }

    // Nothing depends on the sweep for CORRECTNESS: the read filters by state and date itself, so
    // keeping old rows longer must not make an old closed task reappear on today's card.
    [Fact]
    public void KeepingOldRowsLonger_DoesNotPutThemOnTodaysCard()
    {
        RunWithStore((store, _) =>
        {
            AgentTask lastQuarter = store.CreateTask("shipped in May", 1800, Now.AddDays(-90))!;
            store.ActOnTask(lastQuarter.Id, "done", Now.AddDays(-90));
            store.CreateTask("today", 1800, Now);

            IReadOnlyList<AgentTask> today = store.ReadTasks(Now.AddHours(-1), Now);

            Assert.Single(today);
            Assert.Equal("today", today[0].Title);
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
