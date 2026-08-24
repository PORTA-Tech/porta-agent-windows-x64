using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// 2026-08-24 — the name the client sees for a task that was never explicitly labelled.
///
/// <c>ReadReportDraft</c> read raw <c>client_label</c> with no fallback, unlike every other read in
/// the store. The SERVER drops any report line whose label is empty, so the effect was not a blank
/// line — it was NO line: the task vanished from the client's report and nothing said so.
///
/// The per-project default is <c>label_style = 0</c>, documented as "or is its raw title fine?", so
/// an unlabelled task on a default project is not unnamed. Its title is the name.
/// </summary>
public sealed class ReportLineLabelFallbackTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 08, 12, 12, 0, 0, TimeSpan.Zero);

    private const string AppA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // ── the rule itself ───────────────────────────────────────────────────────────────

    [Fact]
    public void AWrittenLabelWins()
        => Assert.Equal(
            "ICP Qualification",
            SqliteBrainStateStore.ClientFacingLabel("ICP Qualification", "work on the Ari prospects"));

    [Fact]
    public void NoLabelFallsBackToTheTitle()
        => Assert.Equal(
            "work on apollo leads",
            SqliteBrainStateStore.ClientFacingLabel("", "work on apollo leads"));

    [Fact]
    public void AWhitespaceLabelIsNotALabel()
        => Assert.Equal(
            "work on apollo leads",
            SqliteBrainStateStore.ClientFacingLabel("   ", "work on apollo leads"));

    [Fact]
    public void ANullLabelFallsBackToTheTitle()
        => Assert.Equal("Pricing page", SqliteBrainStateStore.ClientFacingLabel(null, "Pricing page"));

    // ── through the draft ─────────────────────────────────────────────────────────────

    // A worked task the user never labelled must still reach the report.
    [Fact]
    public void AWorkedTaskWithNoLabelStillCarriesAName()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;
            AgentTask task = store.CreateTask("work on apollo leads", 3600, Day1, project.Id)!;
            store.ActOnTask(task.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);

            AgentReportLine line = Assert.Single(
                store.ReadReportDraft(project.Id, "2026-08-12", "2026-08-13", TimeSpan.Zero).Lines);

            Assert.Equal("work on apollo leads", line.ClientLabel);
            Assert.NotEqual(string.Empty, line.ClientLabel);
        });
    }

    // And so must an unworked one — the zero-hour lines added for planned work inherit the same rule.
    [Fact]
    public void APlannedTaskWithNoLabelStillCarriesAName()
    {
        RunWithStore((store, _) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;
            store.CreateTask("Pricing page", 3600, Day1, project.Id);

            AgentReportLine line = Assert.Single(
                store.ReadReportDraft(project.Id, "2026-08-12", "2026-08-13", TimeSpan.Zero).Lines);

            Assert.Equal("Pricing page", line.ClientLabel);
        });
    }

    [Fact]
    public void AWrittenLabelIsNeverOverwrittenByTheTitle()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;
            AgentTask task = store.CreateTask("work on the Ari prospects", 3600, Day1, project.Id)!;
            store.SetTaskClientLabel(task.Id, "ICP Qualification");
            store.ActOnTask(task.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);

            AgentReportLine line = Assert.Single(
                store.ReadReportDraft(project.Id, "2026-08-12", "2026-08-13", TimeSpan.Zero).Lines);

            Assert.Equal("ICP Qualification", line.ClientLabel);
        });
    }

    // The invariant the server's filter makes load-bearing: no line may leave here unnamed, because
    // an unnamed line is not rendered blank — it is dropped, and the task disappears silently.
    [Fact]
    public void NoLineEverLeavesTheDraftWithoutAName()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject project = store.CreateProject("Northbridge", Day1)!;

            AgentTask worked = store.CreateTask("continue to improve the system", 3600, Day1, project.Id)!;
            store.ActOnTask(worked.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);
            store.ActOnTask(worked.Id, "done", Day1);

            store.CreateTask("Pricing page", 3600, Day1, project.Id);

            AgentTask labelled = store.CreateTask("work on the Ari prospects", 3600, Day1, project.Id)!;
            store.SetTaskClientLabel(labelled.Id, "ICP Qualification");

            AgentReportDraft draft = store.ReadReportDraft(
                project.Id, "2026-08-12", "2026-08-13", TimeSpan.Zero);

            Assert.Equal(3, draft.Lines.Count);
            Assert.All(draft.Lines, l => Assert.False(
                string.IsNullOrWhiteSpace(l.ClientLabel),
                $"task {l.TaskId} would be dropped by the server for having no label"));
        });
    }

    // ── harness ───────────────────────────────────────────────────────────────────────

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
        string dir = Path.Combine(Path.GetTempPath(), "porta-label-fallback-tests", Guid.NewGuid().ToString("N"));
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
