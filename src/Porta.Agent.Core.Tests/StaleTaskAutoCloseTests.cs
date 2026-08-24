using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Chat 3 (2026-08-24) — the synthetic day from the fix plan, replayed through the real store.
//
// 40 minutes of real work, then three hours of a film with light input, then nothing. The task
// must show roughly 40 minutes, not 3h40m, and must come back as auto-closed rather than as an
// outcome Ari invented.
public sealed class StaleTaskAutoCloseTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 08, 24, 09, 0, 0, TimeSpan.Zero);

    private const double BucketSeconds = 60d;

    [Fact]
    public void AFilmAfterRealWork_IsNotCreditedAndTheTaskAutoCloses()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("landing page copy", 7200, DayStart)!;
            store.ActOnTask(task.Id, "start", DayStart);

            // 40 minutes of genuine work.
            for (int minute = 0; minute < 40; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.Focus);
            }

            AgentTask afterWork = Read(store);
            Assert.Equal(2400d, afterWork.ActiveSeconds, precision: 6);
            Assert.Equal("active", afterWork.State);

            // Three hours of a film with a keypress often enough to look alive.
            for (int minute = 40; minute < 220; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.PassiveConsumption);
            }

            AgentTask afterFilm = Read(store);

            // The whole point: still ~40 minutes of work, not 3h40m.
            Assert.Equal(2400d, afterFilm.ActiveSeconds, precision: 6);

            // The film is on the record as off-task — but only for as long as the task was still
            // running. Work stopped at minute 39, so the 45-minute ceiling is crossed at minute 85
            // and the task closes there: 46 minutes of film landed on it, and the remaining two
            // hours belong to no task at all. That is correct rather than lossy — the writers only
            // ever touch the ACTIVE task, and after the auto-close there is not one. The hourly app
            // rollup still records the whole stretch against task 0, which is where untagged time
            // has always gone.
            Assert.Equal(46d * 60d, afterFilm.OfftaskSeconds, precision: 6);

            // And the task stopped itself once 45 minutes had passed with no working time.
            Assert.Equal("pending", afterFilm.State);
        });
    }

    [Fact]
    public void TheAutoCloseIsStampedAtTheLastCreditedBucket_NotAtNow()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("landing page copy", 7200, DayStart)!;
            store.ActOnTask(task.Id, "start", DayStart);

            for (int minute = 0; minute < 40; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.Focus);
            }

            for (int minute = 40; minute < 220; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.PassiveConsumption);
            }

            // Closing at "now" would hand the task the entire idle stretch. It closes where the
            // work actually stopped: the last credited bucket, at 09:39.
            string autoClosed = ReadColumn(dbPath, task.Id, "auto_closed_at_utc");
            Assert.False(string.IsNullOrWhiteSpace(autoClosed), "the task was not recorded as auto-closed");

            DateTimeOffset stamped = DateTimeOffset.Parse(
                autoClosed, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(DayStart.AddMinutes(39), stamped);
        });
    }

    // Auto-closed is not done and not abandoned. Ari does not know which it was, so the evening
    // card asks — which it can only do while the task is still open.
    [Fact]
    public void AnAutoClosedTaskIsNotMarkedDone()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("landing page copy", 7200, DayStart)!;
            store.ActOnTask(task.Id, "start", DayStart);

            Bucket(dbPath, DayStart, AttentionSegmentKinds.Focus);
            for (int minute = 1; minute < 60; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.PassiveConsumption);
            }

            AgentTask closed = Read(store);
            Assert.Equal("pending", closed.State);
            Assert.NotEqual("done", closed.State);
            Assert.NotEqual("abandoned", closed.State);
            Assert.NotEqual("done", ReadColumn(dbPath, task.Id, "status"));
        });
    }

    // A task being worked on continuously must never be auto-closed, however long it runs.
    [Fact]
    public void ATaskBeingWorkedOnIsNeverAutoClosed()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("long stretch", 14400, DayStart)!;
            store.ActOnTask(task.Id, "start", DayStart);

            for (int minute = 0; minute < 180; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.Focus);
            }

            AgentTask running = Read(store);
            Assert.Equal("active", running.State);
            Assert.Equal(180d * 60d, running.ActiveSeconds, precision: 6);
            Assert.Equal(string.Empty, ReadColumn(dbPath, task.Id, "auto_closed_at_utc"));
        });
    }

    // A short break is a break, not the end of the task.
    [Fact]
    public void AShortBreakDoesNotCloseTheTask()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("coffee then back", 7200, DayStart)!;
            store.ActOnTask(task.Id, "start", DayStart);

            for (int minute = 0; minute < 30; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.Focus);
            }

            // 20 minutes away — well inside the 45-minute ceiling.
            for (int minute = 30; minute < 50; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.Away);
            }

            Bucket(dbPath, DayStart.AddMinutes(50), AttentionSegmentKinds.Focus);

            AgentTask stillRunning = Read(store);
            Assert.Equal("active", stillRunning.State);
            Assert.Equal(31d * 60d, stillRunning.ActiveSeconds, precision: 6);
        });
    }

    // A task that has never carried work has no last-credited reading, and auto-closing on an
    // absent reading would be the same guessing this change removes.
    [Fact]
    public void ATaskThatHasNeverBeenCredited_IsLeftAlone()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = store.CreateTask("just started", 3600, DayStart)!;
            store.ActOnTask(task.Id, "start", DayStart);

            for (int minute = 0; minute < 90; minute++)
            {
                Bucket(dbPath, DayStart.AddMinutes(minute), AttentionSegmentKinds.PassiveConsumption);
            }

            Assert.Equal("active", Read(store).State);
        });
    }

    // ── harness ───────────────────────────────────────────────────────────────────────

    // One finalized bucket, through the same two writers the bucket transaction uses and in the
    // same order, so this replay exercises the real crediting path rather than a copy of it.
    private static void Bucket(string dbPath, DateTimeOffset atUtc, string kind)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        string at = atUtc.ToUniversalTime().ToString("O");
        SqliteBrainStateStore.AttributeBucketToActiveTask(conn, tx, BucketSeconds, kind, at);
        SqliteBrainStateStore.AutoCloseStaleActiveTask(conn, tx, at);
        tx.Commit();
    }

    private static AgentTask Read(SqliteBrainStateStore store)
        => store.ReadTasks(DayStart.AddDays(-1), DayStart.AddDays(1)).Single();

    private static string ReadColumn(string dbPath, long taskId, string column)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", taskId);
        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-autoclose-tests", Guid.NewGuid().ToString("N"));
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
