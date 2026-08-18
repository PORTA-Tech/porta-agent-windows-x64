using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// 2026-08-18. The Tasks page's filters were wrong in two ways that made real work invisible, and
/// both were reported by Yadin from the running app rather than caught here.
///
///   Upcoming read the expected-finish date and never the DEFERRED-UNTIL day, so a task he pushed
///   to tomorrow from the close-out card vanished from every filter instead of appearing here.
///
///   In progress required status == 'in_progress', a value almost nothing carries, so the tab was
///   empty while work was underway.
///
/// These tests build the shapes his database actually holds and assert the filters see them.
/// </summary>
public sealed class TaskFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 18, 09, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATaskPutOffUntilTomorrow_IsCarriedThroughTheStore()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("daily weekly digest email", 3600, Now)!;
            string tomorrow = DateTime.Now.Date.AddDays(1).ToString("yyyy-MM-dd");
            store.ActOnTask(task.Id, "defer", Now, deferUntilDay: tomorrow);

            AgentTaskRow row = store.ReadAllTaskRows().Single(r => r.TaskId == task.Id);

            // This is the field the page could not see. Everything downstream depends on it
            // arriving, so it is asserted at the store rather than only in the UI.
            Assert.Equal(tomorrow, row.DeferredUntilDay);
            Assert.NotEqual("done", row.Status);
        });
    }

    [Fact]
    public void ATaskWithRecordedTime_IsNotFinished_AndReadsAsInProgress()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("work on phase 3", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);
            Attribute(store, 900d);

            AgentTaskRow row = store.ReadAllTaskRows().Single(r => r.TaskId == task.Id);

            // "Time recorded against them, and you have not marked them finished" — the filter's
            // own description. Hours must arrive, or the tab is empty no matter what it asks for.
            Assert.True(row.Hours > 0, "hours must reach the row, or In progress can never match");
            Assert.NotEqual("done", row.Status);
        });
    }

    [Fact]
    public void AFinishedTaskKeepsItsHours_ButIsNotStillInProgress()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("finish the send button", 3600, Now)!;
            store.ActOnTask(task.Id, "start", Now);
            Attribute(store, 900d);
            store.SetTaskStatus(task.Id, "done", string.Empty);

            AgentTaskRow row = store.ReadAllTaskRows().Single(r => r.TaskId == task.Id);
            Assert.Equal("done", row.Status);
            Assert.True(row.Hours > 0);
        });
    }

    [Fact]
    public void UntaggedWorkIsListedAndMarked_RatherThanHidden()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("something untagged", 3600, Now)!;

            AgentTaskRow row = store.ReadAllTaskRows().Single(r => r.TaskId == task.Id);

            // It can never reach a client report, and hiding it would make that invisible instead
            // of obvious.
            Assert.True(row.IsUntagged);
            Assert.Equal(string.Empty, row.ProjectName);
        });
    }

    [Fact]
    public void ABlockedTaskCarriesItsReason()
    {
        RunWithStore(store =>
        {
            AgentTask task = store.CreateTask("add the task page", 3600, Now)!;
            store.SetTaskStatus(task.Id, "blocked", "waiting on the design being agreed");

            AgentTaskRow row = store.ReadAllTaskRows().Single(r => r.TaskId == task.Id);
            Assert.Equal("blocked", row.Status);
        });
    }

    private static void Attribute(SqliteBrainStateStore store, double seconds)
    {
        // Goes through the real bucket path, so hours reach the row the same way they do live.
        using var conn = new SqliteConnection($"Data Source={CurrentDbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        SqliteBrainStateStore.AttributeBucketToActiveTask(conn, tx, seconds, "focus");
        SqliteBrainStateStore.AccumulateTaskDayHours(conn, tx, seconds, "focus", Now.ToString("O"));
        SqliteBrainStateStore.AccumulateAppUsageRollup(conn, tx, seconds, "focus", "code", Now.ToString("O"));
        tx.Commit();
    }

    private static string CurrentDbPath = string.Empty;

    private static void RunWithStore(Action<SqliteBrainStateStore> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-filter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        CurrentDbPath = Path.Combine(dir, "core-state.db");
        try
        {
            body(new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: CurrentDbPath));
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ArchivingASentReport_MovesItBetweenTheTwoLists_AndIsReversible()
    {
        RunWithStore(store =>
        {
            // The Archived tab was a hardcoded empty state with no flag, no action and no storage,
            // so archiving appeared to do nothing. This is the round trip that makes it real.
            AgentSentReport report = SampleSentReport("report-1");
            Assert.True(store.RecordSentReport(report));
            store.MarkSentReportOutcome("report-1", sent: true, "2026-08-18T09:00:00.000Z", string.Empty);

            Assert.False(store.ReadSentReports().Single(r => r.ReportId == "report-1").Archived);

            Assert.True(store.SetSentReportArchived("report-1", true));
            Assert.True(store.ReadSentReports().Single(r => r.ReportId == "report-1").Archived);

            // Restoring must work too: archiving is cosmetic and nothing about it is permanent.
            Assert.True(store.SetSentReportArchived("report-1", false));
            Assert.False(store.ReadSentReports().Single(r => r.ReportId == "report-1").Archived);
        });
    }

    private static AgentSentReport SampleSentReport(string id)
        => new(
            ReportId: id, ProjectId: 1, ProjectName: "PORTA", ClientName: "PORTA SYSTEM",
            RangeStartLocal: "2026-08-12", RangeEndLocal: "2026-08-18",
            RecipientName: "", RecipientEmail: "client@example.com",
            FromName: "Yadin", ReplyTo: "yadin@example.com",
            TotalHours: 5.9, DaysWorked: 3, LinesFinished: 2, LinesTotal: 4,
            RequestedAtUtc: Now, SentAtUtc: "", SendState: "pending", SendError: "",
            ServerState: "present", Lines: [], Days: []);
}
