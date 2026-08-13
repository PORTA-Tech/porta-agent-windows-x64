using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Step 10 phase B — the report draft. These guard the things that would lose an argument with a
/// client: figures that do not add up to their own lines, a chart that hides the days nothing
/// happened, and any number that was not measured.
///
/// Everything is derived from <c>app_usage_rollup</c> and nothing is stored, so a draft is only
/// ever a reading of what was measured.
/// </summary>
public sealed class ReportDraftStoreTests
{
    // 12:00Z on a UTC-offset-zero day, so a local date in these tests is the UTC date.
    private static readonly DateTimeOffset Day1 = new(2026, 08, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 08, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day4 = new(2026, 08, 15, 12, 0, 0, TimeSpan.Zero);

    private const string AppA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void HoursComeFromMeasuredTime_AndTheTotalIsTheSumOfItsOwnLines()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;
            AgentTask one = store.CreateTask("finish my writting part", 3600, Day1, nolan.Id)!;
            store.ActOnTask(one.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);          // 1.0h
            store.ActOnTask(one.Id, "done", Day1);

            AgentTask two = store.CreateTask("working on appolo leads", 3600, Day2, nolan.Id)!;
            store.ActOnTask(two.Id, "start", Day2);
            Accumulate(dbPath, 1800d, Day2);          // 0.5h

            AgentReportDraft draft = Draft(store, nolan.Id, "2026-08-12", "2026-08-13");

            Assert.Equal(2, draft.Lines.Count);
            Assert.Equal(1.5d, draft.TotalHours, precision: 6);
            Assert.Equal(
                draft.TotalHours,
                Math.Round(draft.Lines.Sum(l => l.Hours), 1, MidpointRounding.AwayFromZero),
                precision: 6);
            Assert.Equal(2, draft.DaysWorked);
            Assert.Equal(1, draft.LinesFinished);
        });
    }

    /// <summary>
    /// §4 — a chart that hides the days you did not work is the kind of thing a client notices, and
    /// it costs trust permanently. So an empty day is present with 0.0, never skipped.
    /// </summary>
    [Fact]
    public void EveryDayInTheRangeIsPresent_IncludingTheOnesWithNoWork()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;
            AgentTask task = store.CreateTask("Pricing page", 3600, Day1, nolan.Id)!;
            store.ActOnTask(task.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);
            Accumulate(dbPath, 3600d, Day4);

            AgentReportDraft draft = Draft(store, nolan.Id, "2026-08-12", "2026-08-15");

            Assert.Equal(4, draft.Days.Count);
            Assert.Equal(
                new[] { "2026-08-12", "2026-08-13", "2026-08-14", "2026-08-15" },
                draft.Days.Select(d => d.DateLocal).ToArray());
            Assert.Equal(0d, draft.Days[1].Hours, precision: 6);
            Assert.Equal(0d, draft.Days[2].Hours, precision: 6);
            Assert.Equal(2, draft.DaysWorked);
        });
    }

    [Fact]
    public void OnlyTheAskedForProjectAppears()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;
            AgentProject apollo = store.CreateProject("Apollo", Day1)!;

            AgentTask mine = store.CreateTask("Nolan work", 3600, Day1, nolan.Id)!;
            store.ActOnTask(mine.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);
            store.ActOnTask(mine.Id, "done", Day1);

            AgentTask theirs = store.CreateTask("Apollo work", 3600, Day1, apollo.Id)!;
            store.ActOnTask(theirs.Id, "start", Day1);
            Accumulate(dbPath, 7200d, Day1);

            AgentReportDraft draft = Draft(store, nolan.Id, "2026-08-12", "2026-08-12");

            Assert.Single(draft.Lines);
            Assert.Equal(1.0d, draft.TotalHours, precision: 6);
        });
    }

    /// <summary>
    /// Re-tagging is allowed at any time and the project is resolved at READ time, so a whole
    /// history moves between reports at once. A report already SENT is a snapshot and is never
    /// regenerated — that rule lives with sending, in phase C.
    /// </summary>
    [Fact]
    public void RetaggingATask_MovesItsHoursToTheOtherProjectsNextDraft()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;
            AgentProject apollo = store.CreateProject("Apollo", Day1)!;
            AgentTask task = store.CreateTask("Pricing page", 3600, Day1, nolan.Id)!;
            store.ActOnTask(task.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);

            Assert.Equal(1.0d, Draft(store, nolan.Id, "2026-08-12", "2026-08-12").TotalHours, precision: 6);

            store.SetTaskProject(task.Id, apollo.Id);

            Assert.Equal(0d, Draft(store, nolan.Id, "2026-08-12", "2026-08-12").TotalHours, precision: 6);
            Assert.Equal(1.0d, Draft(store, apollo.Id, "2026-08-12", "2026-08-12").TotalHours, precision: 6);
        });
    }

    /// <summary>
    /// THE DATA FLOOR. Backfilled rows carry task_id = 0 — which app was recoverable, which task
    /// never was — so they belong to no project and can never reach a report. A number that walked
    /// in from a reconstruction is exactly what a client's invoice must not contain.
    /// </summary>
    [Fact]
    public void BackfilledHoursNeverReachAReport()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;
            InsertBackfilledRow(dbPath, Day1, seconds: 7200d);

            AgentReportDraft draft = Draft(store, nolan.Id, "2026-08-12", "2026-08-12");

            Assert.Empty(draft.Lines);
            Assert.Equal(0d, draft.TotalHours, precision: 6);
            Assert.Equal(0, draft.DaysWorked);
        });
    }

    /// <summary>
    /// The report counts what the rollup saw; a bucket with no identifiable application still
    /// credits the task but carries no rollup row. That gap is surfaced per line so a report that
    /// reads lower than the widget explains itself on the user's own screen — never to a client.
    /// </summary>
    [Fact]
    public void TimeWithNoRecognisedApp_IsReportedAsUnattributed_AndNotCounted()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;
            AgentTask task = store.CreateTask("Pricing page", 3600, Day1, nolan.Id)!;
            store.ActOnTask(task.Id, "start", Day1);

            // Measured on the task, with an app: lands in both places.
            Accumulate(dbPath, 3600d, Day1);
            // Measured on the task with NO app: credited to the task, absent from the rollup.
            AttributeOnly(dbPath, 1800d);

            AgentReportLine line = Assert.Single(Draft(store, nolan.Id, "2026-08-12", "2026-08-12").Lines);

            Assert.Equal(1.0d, line.Hours, precision: 6);
            Assert.Equal(0.5d, line.UnattributedHours, precision: 6);
        });
    }

    [Fact]
    public void TheClientFacingNameIsCarried_AndTheLocalNameStaysLocal()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject project = store.CreateProject("nolan - chase invoice", Day1)!;
            store.SetProjectClientName(project.Id, "Nolan Studio");
            AgentTask task = store.CreateTask("Pricing page", 3600, Day1, project.Id)!;
            store.ActOnTask(task.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);

            AgentReportDraft draft = Draft(store, project.Id, "2026-08-12", "2026-08-12");

            Assert.Equal("Nolan Studio", draft.ClientName);
            Assert.Equal("nolan - chase invoice", draft.ProjectName);
        });
    }

    [Fact]
    public void TheClientLabelIsCarried_AndSoIsTheRawTitleForTheUsersOwnScreen()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;
            AgentTask task = store.CreateTask("finish my writting part", 3600, Day1, nolan.Id)!;
            store.ActOnTask(task.Id, "start", Day1);
            Accumulate(dbPath, 3600d, Day1);
            store.SetTaskClientLabel(task.Id, "Chapter three");

            AgentReportLine line = Assert.Single(Draft(store, nolan.Id, "2026-08-12", "2026-08-12").Lines);

            Assert.Equal("Chapter three", line.ClientLabel);
            Assert.Equal("finish my writting part", line.Title);
        });
    }

    [Theory]
    [InlineData("not-a-date", "2026-08-13")]
    [InlineData("2026-08-13", "2026-08-12")]   // end before start
    public void AnImpossibleRangeGivesAnEmptyDraft_RatherThanAGuess(string from, string to)
    {
        RunWithStore((store, _) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Day1)!;

            AgentReportDraft draft = Draft(store, nolan.Id, from, to);

            Assert.Empty(draft.Lines);
            Assert.Empty(draft.Days);
            Assert.Equal(0d, draft.TotalHours, precision: 6);
        });
    }

    private static AgentReportDraft Draft(SqliteBrainStateStore store, long projectId, string from, string to)
        => store.ReadReportDraft(projectId, from, to, TimeSpan.Zero);

    /// <summary>
    /// One measured bucket, exactly as production writes it: the task credited and the rollup
    /// accumulated in the SAME transaction off the same inputs. Writing only one of the two would
    /// make these tests agree with a pipeline that does not exist.
    /// </summary>
    private static void Accumulate(string dbPath, double seconds, DateTimeOffset at)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        SqliteBrainStateStore.AttributeBucketToActiveTask(
            conn, tx, seconds, AttentionSegmentKinds.Focus);
        SqliteBrainStateStore.AccumulateAppUsageRollup(
            conn, tx, seconds, AttentionSegmentKinds.Focus, AppA, at.ToString("O"));
        tx.Commit();
    }

    /// <summary>The half that happens when no application could be identified.</summary>
    private static void AttributeOnly(string dbPath, double seconds)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        SqliteBrainStateStore.AttributeBucketToActiveTask(
            conn, tx, seconds, AttentionSegmentKinds.Focus);
        tx.Commit();
    }

    private static void InsertBackfilledRow(string dbPath, DateTimeOffset at, double seconds)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO app_usage_rollup
                (hour_start_utc, task_id, app_hash, focused_seconds, offtask_seconds, active_seconds, backfilled)
            VALUES ($hour, 0, $app, 0, 0, $seconds, 1);
            """;
        cmd.Parameters.AddWithValue(
            "$hour", at.ToUniversalTime().ToString("yyyy-MM-ddTHH:00:00.0000000+00:00"));
        cmd.Parameters.AddWithValue("$app", AppA);
        cmd.Parameters.AddWithValue("$seconds", seconds);
        cmd.ExecuteNonQuery();
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-report-draft-tests", Guid.NewGuid().ToString("N"));
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
