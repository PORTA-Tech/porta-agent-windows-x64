using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// 2026-08-18 — bug 2. Nothing ever moved a task to 'in_progress', so work with real hours
// against it appeared under a "Planned" heading in a client's report. The promotion now happens
// where the time is attributed, which is the only place it is a measured fact rather than a guess.
//
// These tests pin the promotion AND, more importantly, the four things it must refuse to do.
public sealed class TaskStatusPromotionTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 18, 09, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AttributedTime_PromotesThePlannedTask_ToInProgress()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("work on phase 2", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);

            // A newly created task is 'planned' and nothing has moved it. This is the bug.
            Assert.Equal("planned", Read(store, task.Id).Status);

            AccumulateDay(dbPath, 300d, "focus", Now);

            AgentTask promoted = Read(store, task.Id);
            Assert.Equal("in_progress", promoted.Status);
            Assert.Equal("active", promoted.State);   // the execution axis is untouched
        });
    }

    [Fact]
    public void Promotion_IsIdempotent_AcrossManyBuckets()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("long stretch", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);

            AccumulateDay(dbPath, 300d, "focus", Now);
            DateTimeOffset firstStart = Read(store, task.Id).StartedAtUtc!.Value;

            for (int i = 1; i <= 5; i++)
            {
                AccumulateDay(dbPath, 300d, "focus", Now.AddMinutes(5 * i));
            }

            AgentTask after = Read(store, task.Id);
            Assert.Equal("in_progress", after.Status);
            // The start is written once and never rewritten by a later bucket.
            Assert.Equal(firstStart, after.StartedAtUtc!.Value);
        });
    }

    [Fact]
    public void Promotion_NeverOverwrites_AStatusTheUserSet()
    {
        RunWithStore((store, dbPath) =>
        {
            // 'blocked' and 'done' are statements the person made. A background timer must not
            // silently contradict either, even while the timer is genuinely running.
            AgentTask blocked = store.CreateTask("waiting on the client", 3600, Now)!;
            store.ActOnTask(blocked.Id, "start", Now);
            store.SetTaskStatus(blocked.Id, "blocked", "waiting on brand assets");

            AccumulateDay(dbPath, 300d, "focus", Now);

            AgentTask still = Read(store, blocked.Id);
            Assert.Equal("blocked", still.Status);
            Assert.Equal("waiting on brand assets", still.BlockedReason);

            store.SetTaskStatus(blocked.Id, "done", string.Empty);
            AccumulateDay(dbPath, 300d, "focus", Now.AddMinutes(10));
            Assert.Equal("done", Read(store, blocked.Id).Status);
        });
    }

    [Fact]
    public void Promotion_TouchesOnlyTheActiveTask()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask running = store.CreateTask("the one being worked on", 3600, Now)!;
            AgentTask idle = store.CreateTask("not started", 3600, Now)!;
            store.ActOnTask(running.Id, "start", Now);

            AccumulateDay(dbPath, 300d, "focus", Now);

            Assert.Equal("in_progress", Read(store, running.Id).Status);
            // A task nobody has touched stays planned. "In progress" has to mean something.
            Assert.Equal("planned", Read(store, idle.Id).Status);
        });
    }

    [Fact]
    public void AwayTime_AndZeroLengthBuckets_PromoteNothing()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("stepped out", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);

            AccumulateDay(dbPath, 300d, "away", Now);   // away is not work
            AccumulateDay(dbPath, 0d, "focus", Now);    // nothing measured

            // No time was attributed, so there is no measured fact to promote on.
            Assert.Equal("planned", Read(store, task.Id).Status);
        });
    }

    [Fact]
    public void StartedAt_ComesFromTheBucket_NotFromNow()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("backdated bucket", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);
            ClearStartedAt(dbPath, task.Id);

            DateTimeOffset bucketAt = Now.AddHours(-2);
            AccumulateDay(dbPath, 300d, "focus", bucketAt);

            AgentTask promoted = Read(store, task.Id);
            Assert.Equal("in_progress", promoted.Status);
            // Dated to when the work happened, not to when the bucket was written.
            Assert.Equal(bucketAt, promoted.StartedAtUtc!.Value.ToUniversalTime());
        });
    }


    // 2026-08-18 — found while testing the promotion above, and worse than the bug it was found
    // under. Four setters opened the database with the bare FILE PATH where a CONNECTION STRING
    // was required, so every one of them threw ArgumentException the moment it was called. That is
    // the whole of phase 1.1: the four-way status, the result line, the blocked reason and the
    // expected finish could not be saved at all in 1.2.159. These pin the round-trip so the four
    // never silently regress to a path again.

    [Fact]
    public void TheFieldsAReportIsMadeOf_RoundTrip()
    {
        RunWithStore((store, _) =>
        {
            AgentTask task = store.CreateTask("ship the report page", 3600, Now)!;

            Assert.NotNull(store.SetTaskStatus(task.Id, "blocked", "waiting on brand assets"));
            AgentTask afterStatus = Read(store, task.Id);
            Assert.Equal("blocked", afterStatus.Status);
            Assert.Equal("waiting on brand assets", afterStatus.BlockedReason);

            Assert.NotNull(store.SetTaskExpectedFinish(task.Id, "2026-08-25"));
            Assert.Equal("2026-08-25", Read(store, task.Id).ExpectedFinishDay);

            Assert.NotNull(store.SetTaskStatus(task.Id, "done", string.Empty));
            AgentTask done = Read(store, task.Id);
            Assert.Equal("done", done.Status);
            // Clearing blocked clears its reason: "blocked" with no reason is worse than silence.
            Assert.Equal(string.Empty, done.BlockedReason);
            Assert.NotNull(done.CompletedAtUtc);
        });
    }

    private static AgentTask Read(SqliteBrainStateStore store, long id)
        => store.ReadTasks(Now.AddDays(-2), Now.AddDays(1)).Single(t => t.Id == id);

    private static void AccumulateDay(string dbPath, double seconds, string kind, DateTimeOffset at)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        SqliteBrainStateStore.AccumulateTaskDayHours(conn, tx, seconds, kind, at.ToString("O"));
        tx.Commit();
    }

    private static void ClearStartedAt(string dbPath, long id)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET started_at_utc = '' WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-status-tests", Guid.NewGuid().ToString("N"));
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
