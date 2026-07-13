using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Phase 2c — the reminders store: create/next/ack round-trips, the lazy pending→fired
// transition (polling ReadNextReminder IS the clock), the pending cap, snooze semantics,
// the 30-day cleanup of closed rows, and the full local-data reset wipe.
public sealed class RemindersStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 07, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ThenNext_ReturnsPendingReminder()
    {
        RunWithStore((store, _) =>
        {
            AgentReminder? created = store.CreateReminder("standup", Now.AddMinutes(30), Now);
            Assert.NotNull(created);
            Assert.Equal("pending", created!.State);
            Assert.Equal("standup", created.Title);

            AgentReminder? next = store.ReadNextReminder(Now);
            Assert.NotNull(next);
            Assert.Equal(created.Id, next!.Id);
            Assert.Equal("pending", next.State);
        });
    }

    [Fact]
    public void PastDue_TransitionsToFiredOnRead()
    {
        RunWithStore((store, _) =>
        {
            AgentReminder created = store.CreateReminder("ship build", Now.AddMinutes(5), Now)!;
            AgentReminder? next = store.ReadNextReminder(Now.AddMinutes(6));
            Assert.NotNull(next);
            Assert.Equal(created.Id, next!.Id);
            Assert.Equal("fired", next.State);
            Assert.NotNull(next.FiredAtUtc);
        });
    }

    // Workstream 2 — a reminder must NEVER ring before its due time: reads right up to
    // (and including one tick before) due_at_utc leave it pending; at due it fires. Late
    // by up to one poll interval is fine, early is a bug.
    [Fact]
    public void NeverFiresBeforeDueTime()
    {
        RunWithStore((store, _) =>
        {
            DateTimeOffset due = Now.AddHours(2);
            store.CreateReminder("in 2 hours", due, Now);

            Assert.Equal("pending", store.ReadNextReminder(Now)!.State);
            Assert.Equal("pending", store.ReadNextReminder(due.AddSeconds(-1))!.State);
            Assert.Equal("fired", store.ReadNextReminder(due)!.State);
        });
    }

    [Fact]
    public void FiredBeatsEarlierPending()
    {
        RunWithStore((store, _) =>
        {
            store.CreateReminder("due soon", Now.AddMinutes(10), Now);
            AgentReminder overdue = store.CreateReminder("overdue", Now.AddMinutes(1), Now)!;

            AgentReminder? next = store.ReadNextReminder(Now.AddMinutes(2));
            Assert.Equal(overdue.Id, next!.Id);
            Assert.Equal("fired", next.State);
        });
    }

    [Fact]
    public void Done_ClosesReminder()
    {
        RunWithStore((store, _) =>
        {
            AgentReminder created = store.CreateReminder("x", Now.AddMinutes(5), Now)!;
            Assert.True(store.AcknowledgeReminder(created.Id, "done", Now));
            Assert.Null(store.ReadNextReminder(Now));
            // Second ack on a closed reminder fails.
            Assert.False(store.AcknowledgeReminder(created.Id, "done", Now));
        });
    }

    [Fact]
    public void Snooze_MovesDueForwardAndReturnsToPending()
    {
        RunWithStore((store, _) =>
        {
            AgentReminder created = store.CreateReminder("x", Now.AddMinutes(1), Now)!;
            // Fire it.
            AgentReminder fired = store.ReadNextReminder(Now.AddMinutes(2))!;
            Assert.Equal("fired", fired.State);

            // Snooze +10m from ack time.
            DateTimeOffset ackAt = Now.AddMinutes(3);
            Assert.True(store.AcknowledgeReminder(created.Id, "snooze", ackAt));

            AgentReminder? next = store.ReadNextReminder(ackAt);
            Assert.Equal("pending", next!.State);
            Assert.Equal(1, next.SnoozeCount);
            Assert.Equal(ackAt.AddMinutes(PortaExecutionConstants.Reminders.SnoozeMinutes), next.DueAtUtc);
        });
    }

    [Fact]
    public void UnknownActionOrId_ReturnsFalse()
    {
        RunWithStore((store, _) =>
        {
            AgentReminder created = store.CreateReminder("x", Now.AddMinutes(5), Now)!;
            Assert.False(store.AcknowledgeReminder(created.Id, "banana", Now));
            Assert.False(store.AcknowledgeReminder(9999, "done", Now));
        });
    }

    [Fact]
    public void PendingCap_RejectsCreationAtLimit()
    {
        RunWithStore((store, _) =>
        {
            for (int i = 0; i < PortaExecutionConstants.Reminders.MaxPending; i++)
            {
                Assert.NotNull(store.CreateReminder($"r{i}", Now.AddHours(1 + i), Now));
            }

            Assert.Null(store.CreateReminder("one too many", Now.AddHours(99), Now));

            // Closing one frees a slot.
            AgentReminder? next = store.ReadNextReminder(Now);
            Assert.True(store.AcknowledgeReminder(next!.Id, "dismiss", Now));
            Assert.NotNull(store.CreateReminder("fits again", Now.AddHours(50), Now));
        });
    }

    [Fact]
    public void ClosedRows_AreCleanedUpAfterRetention_OpenOnesSurvive()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentReminder closed = store.CreateReminder("old done", Now.AddMinutes(1), Now)!;
            store.AcknowledgeReminder(closed.Id, "done", Now);
            // A reminder set far in the future stays pending across the cleanup horizon.
            store.CreateReminder("vacation prep", Now.AddDays(40), Now);

            // 31 days later: poll triggers the piggyback cleanup.
            AgentReminder? next = store.ReadNextReminder(Now.AddDays(31));
            Assert.NotNull(next); // the future reminder survived
            Assert.Equal("vacation prep", next!.Title);
            Assert.Equal(1, CountRows(dbPath)); // the closed row is gone
        });
    }

    [Fact]
    public void FullLocalDataReset_WipesReminders()
    {
        RunWithStore((store, dbPath) =>
        {
            store.CreateReminder("a", Now.AddMinutes(10), Now);
            store.CreateReminder("b", Now.AddMinutes(20), Now);
            store.DeleteLocalData();
            Assert.Equal(0, CountRows(dbPath));
            Assert.Null(store.ReadNextReminder(Now));
        });
    }

    private static int CountRows(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reminders;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-reminder-tests", Guid.NewGuid().ToString("N"));
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
                // best-effort temp cleanup
            }
        }
    }
}
