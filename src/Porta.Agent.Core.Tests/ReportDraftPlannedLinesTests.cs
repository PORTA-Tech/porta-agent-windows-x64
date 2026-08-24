using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Chat 4 (2026-08-24) — work that has not started yet belongs in the report.
///
/// Every line used to be built from <c>app_usage_rollup</c>, so a task with no recorded hours in
/// the range could never become one. The tasks were read a few lines later and thrown away, and the
/// client saw a report that silently omitted everything still to come — which reads as "there is
/// nothing else planned".
/// </summary>
public sealed class ReportDraftPlannedLinesTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 08, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 08, 13, 12, 0, 0, TimeSpan.Zero);

    private const string AppA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // The fix plan's scenario: a project with two worked tasks and two upcoming ones.
    [Fact]
    public void TwoWorkedAndTwoUpcoming_AllFourReachTheReport()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;

            AgentTask worked1 = store.CreateTask("Rewrite the onboarding email", 3600, Day1, project.Id)!;
            store.ActOnTask(worked1.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);
            store.ActOnTask(worked1.Id, "done", Day1);

            AgentTask worked2 = store.CreateTask("Landing page copy", 3600, Day2, project.Id)!;
            store.ActOnTask(worked2.Id, "start", Day2);
            Accumulate(dbPath, 1800d, Day2);

            // Two that have never been touched.
            store.CreateTask("Pricing page", 3600, Day2, project.Id);
            store.CreateTask("Case study", 3600, Day2, project.Id);

            AgentReportDraft draft = Draft(store, project.Id, "2026-08-12", "2026-08-13");

            Assert.Equal(4, draft.Lines.Count);

            // The hours are untouched by the addition — a planned line contributes nothing.
            Assert.Equal(1.5d, draft.TotalHours, precision: 6);

            var planned = draft.Lines.Where(l => l.Status == "planned").ToList();
            Assert.Equal(2, planned.Count);
            Assert.All(planned, l => Assert.Equal(0d, l.Hours, precision: 6));
            Assert.Contains(planned, l => l.Title == "Pricing page");
            Assert.Contains(planned, l => l.Title == "Case study");
        });
    }

    // The renderer groups by status and already has a "Planned" heading the payload could never
    // populate. This is the feed that populates it.
    [Fact]
    public void APlannedLineCarriesItsStatus_SoTheRendererCanGroupIt()
    {
        RunWithStore((store, _) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;
            store.CreateTask("Pricing page", 3600, Day1, project.Id);

            AgentReportDraft draft = Draft(store, project.Id, "2026-08-12", "2026-08-13");

            AgentReportLine line = Assert.Single(draft.Lines);
            Assert.Equal("planned", line.Status);
            Assert.Equal("Pricing page", line.Title);
            Assert.Equal(0d, line.Hours, precision: 6);
            Assert.False(line.Finished);
        });
    }

    // A task moved to in_progress but with no hours yet in this range still belongs — it is work
    // the client is entitled to know has begun.
    [Fact]
    public void AnInProgressTaskWithNoHoursInRangeIsStillAline()
    {
        RunWithStore((store, _) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;
            AgentTask task = store.CreateTask("Landing page copy", 3600, Day1, project.Id)!;
            store.SetTaskReportFields(task.Id, "in_progress", null, null, null);

            AgentReportDraft draft = Draft(store, project.Id, "2026-08-12", "2026-08-13");

            AgentReportLine line = Assert.Single(draft.Lines);
            Assert.Equal("in_progress", line.Status);
            Assert.Equal(0d, line.Hours, precision: 6);
        });
    }

    // Only planned and in_progress are added. A DONE task with no hours in this range was finished
    // in some other window and is not this report's news; a BLOCKED one carries a reason the user
    // has not been asked for. Both would be claims rather than facts.
    [Fact]
    public void DoneAndBlockedTasksWithNoHoursAreNotInvented()
    {
        RunWithStore((store, _) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;

            AgentTask finishedElsewhere = store.CreateTask("Old work", 3600, Day1, project.Id)!;
            store.SetTaskReportFields(finishedElsewhere.Id, "done", null, null, null);

            AgentTask blocked = store.CreateTask("Waiting on legal", 3600, Day1, project.Id)!;
            store.SetTaskReportFields(blocked.Id, "blocked", null, "waiting on legal", null);

            AgentReportDraft draft = Draft(store, project.Id, "2026-08-12", "2026-08-13");

            Assert.Empty(draft.Lines);
        });
    }

    // A worked task must not be listed twice — once from the rollup and again from the task table.
    [Fact]
    public void AWorkedTaskAppearsExactlyOnce()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;
            AgentTask task = store.CreateTask("Landing page copy", 3600, Day1, project.Id)!;
            store.ActOnTask(task.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);

            AgentReportDraft draft = Draft(store, project.Id, "2026-08-12", "2026-08-13");

            AgentReportLine line = Assert.Single(draft.Lines);
            Assert.Equal(1d, line.Hours, precision: 6);
        });
    }

    // Planned work from ANOTHER project must not leak in.
    [Fact]
    public void PlannedTasksFromAnotherProjectAreNotIncluded()
    {
        RunWithStore((store, _) =>
        {
            AgentProject mine = store.CreateProject("Northbridge", Day1)!;
            AgentProject theirs = store.CreateProject("Apollo", Day1)!;
            store.CreateTask("Someone else's task", 3600, Day1, theirs.Id);
            store.CreateTask("Pricing page", 3600, Day1, mine.Id);

            AgentReportDraft draft = Draft(store, mine.Id, "2026-08-12", "2026-08-13");

            AgentReportLine line = Assert.Single(draft.Lines);
            Assert.Equal("Pricing page", line.Title);
        });
    }

    // A planned line has nothing to measure, so it must not claim an unattributed gap either.
    [Fact]
    public void APlannedLineClaimsNoUnmeasuredGap()
    {
        RunWithStore((store, _) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;
            store.CreateTask("Pricing page", 3600, Day1, project.Id);

            AgentReportLine line = Assert.Single(
                Draft(store, project.Id, "2026-08-12", "2026-08-13").Lines);

            Assert.Equal(0d, line.UnattributedHours, precision: 6);
            Assert.False(line.UnattributedMeasurable);
        });
    }

    // ── harness ───────────────────────────────────────────────────────────────────────

    private static AgentReportDraft Draft(
        SqliteBrainStateStore store, long projectId, string from, string to)
        => store.ReadReportDraft(projectId, from, to, TimeSpan.Zero);

    /// <summary>
    /// One measured bucket, through ALL THREE writers in the same transaction, exactly as the
    /// bucket-finalize path runs them. The middle one matters here: AccumulateTaskDayHours is what
    /// promotes a task to in_progress once real time lands on it, so a helper that skips it leaves
    /// worked tasks sitting at 'planned' and the test would be describing a pipeline that does not
    /// exist.
    /// </summary>
    private static void Accumulate(string dbPath, double seconds, DateTimeOffset at)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        SqliteBrainStateStore.AttributeBucketToActiveTask(
            conn, tx, seconds, AttentionSegmentKinds.Focus, at.ToString("O"));
        SqliteBrainStateStore.AccumulateTaskDayHours(
            conn, tx, seconds, AttentionSegmentKinds.Focus, at.ToString("O"));
        SqliteBrainStateStore.AccumulateAppUsageRollup(
            conn, tx, seconds, AttentionSegmentKinds.Focus, AppA, at.ToString("O"));
        tx.Commit();
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-planned-lines-tests", Guid.NewGuid().ToString("N"));
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
