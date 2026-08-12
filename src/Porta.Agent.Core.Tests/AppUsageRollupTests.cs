using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// 2026-08-12 — the hourly app-usage rollup. It exists so a project's picture outlives the 14-day
/// capture window, at ~15 KB/day instead of ~32 MB/day.
///
/// The guard that matters most is <see cref="Retagging_RewritesEveryDayOfHistoryAtOnce"/>: the
/// rollup is keyed by TASK and resolves the project at read time, which is the whole reason a
/// re-tag reaches history that the source buckets can no longer rebuild.
/// </summary>
public sealed class AppUsageRollupTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 13, 10, 30, 0, TimeSpan.Zero);
    private const string AppA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AppB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void ABucketLandsOnTheRollup_SplitByAttention()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.OffTaskDistraction, app: AppA, at: Now);

            AgentAppUsageHour row = Single(store);
            Assert.Equal(AppA, row.AppHash);
            Assert.Equal(5d, row.FocusedSeconds, precision: 6);
            Assert.Equal(5d, row.OfftaskSeconds, precision: 6);
            Assert.Equal(10d, row.ActiveSeconds, precision: 6);
            Assert.False(row.Backfilled);
        });
    }

    // Away is not an application. Same rule AttributeBucketToActiveTask already follows.
    [Fact]
    public void AwayTimeIsNotRecorded()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Away, app: AppA, at: Now);
            Assert.Empty(store.ReadAppUsage(Now.AddHours(-1), Now.AddHours(1), TimeSpan.Zero));
        });
    }

    [Fact]
    public void TimeWithNoActiveTask_IsRecordedAsUntagged_NotDropped()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);

            AgentAppUsageHour row = Single(store);
            Assert.Equal(0, row.TaskId);
            Assert.Equal(0, row.ProjectId);
            Assert.Equal(string.Empty, row.ProjectName);
            Assert.Equal(5d, row.ActiveSeconds, precision: 6);
        });
    }

    [Fact]
    public void TimeOnAnActiveTask_ResolvesThroughToItsProject()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Now)!;
            AgentTask task = store.CreateTask("Pricing page", 3600, Now, nolan.Id)!;
            store.ActOnTask(task.Id, "start", Now);

            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);

            AgentAppUsageHour row = Single(store);
            Assert.Equal(task.Id, row.TaskId);
            Assert.Equal(nolan.Id, row.ProjectId);
            Assert.Equal("Nolan", row.ProjectName);
        });
    }

    /// <summary>
    /// THE reason it is keyed by task. Re-tagging is allowed at any time and the page is current
    /// truth; if project_id were stored on these rows, history older than the 14-day bucket window
    /// could never be corrected.
    /// </summary>
    [Fact]
    public void Retagging_RewritesEveryDayOfHistoryAtOnce()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Now)!;
            AgentProject apollo = store.CreateProject("Apollo", Now)!;
            AgentTask task = store.CreateTask("Pricing page", 3600, Now, nolan.Id)!;
            store.ActOnTask(task.Id, "start", Now);

            // Three separate days of measured time on that task.
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now.AddDays(-40));
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now.AddDays(-20));
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);

            store.SetTaskProject(task.Id, apollo.Id);

            IReadOnlyList<AgentAppUsageHour> rows =
                store.ReadAppUsage(Now.AddDays(-60), Now.AddHours(1), TimeSpan.Zero);

            Assert.Equal(3, rows.Count);
            // Every day of it, including days whose source buckets are long gone.
            Assert.All(rows, r => Assert.Equal("Apollo", r.ProjectName));
        });
    }

    [Fact]
    public void ARowWhoseTaskIsGone_ReadsAsUntagged_RatherThanVanishing()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentProject nolan = store.CreateProject("Nolan", Now)!;
            AgentTask task = store.CreateTask("Pricing page", 3600, Now, nolan.Id)!;
            store.ActOnTask(task.Id, "start", Now);
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);

            DeleteTaskRow(dbPath, task.Id);

            AgentAppUsageHour row = Single(store);
            Assert.Equal(0, row.ProjectId);
            Assert.Equal(5d, row.ActiveSeconds, precision: 6);
        });
    }

    // Hourly, not daily: detail not captured is gone forever, detail captured can be ignored.
    [Fact]
    public void SeparateHoursStaySeparate()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);
            Accumulate(dbPath, seconds: 7, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now.AddHours(2));

            IReadOnlyList<AgentAppUsageHour> rows =
                store.ReadAppUsage(Now.AddHours(-1), Now.AddHours(4), TimeSpan.Zero);

            Assert.Equal(2, rows.Count);
            Assert.Equal(10, rows[0].HourLocal);
            Assert.Equal(12, rows[1].HourLocal);
        });
    }

    [Fact]
    public void SeparateAppsInTheSameHourStaySeparate()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);
            Accumulate(dbPath, seconds: 9, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppB, at: Now);

            IReadOnlyList<AgentAppUsageHour> rows =
                store.ReadAppUsage(Now.AddHours(-1), Now.AddHours(1), TimeSpan.Zero);
            Assert.Equal(2, rows.Count);
            Assert.Equal(14d, rows.Sum(r => r.ActiveSeconds), precision: 6);
        });
    }

    // The local hour is resolved at READ time, so history does not carry one machine's offset.
    [Fact]
    public void TheLocalHourComesFromTheReadersOffset()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now); // 10:30 UTC

            AgentAppUsageHour utc = store
                .ReadAppUsage(Now.AddHours(-1), Now.AddHours(1), TimeSpan.Zero).Single();
            AgentAppUsageHour dubai = store
                .ReadAppUsage(Now.AddHours(-1), Now.AddHours(1), TimeSpan.FromHours(4)).Single();

            Assert.Equal(10, utc.HourLocal);
            Assert.Equal(14, dubai.HourLocal);
            Assert.Equal("2026-08-13", dubai.LocalDate);
        });
    }

    [Fact]
    public void TheRollupOutlivesTheCaptureTables_ButNotTaskRetention()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now.AddDays(-500));
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now.AddDays(-100));

            TriggerPrune(store);

            IReadOnlyList<AgentAppUsageHour> rows =
                store.ReadAppUsage(Now.AddDays(-600), Now.AddHours(1), TimeSpan.Zero);

            // 100 days is far past the 14-day capture window and still here; 500 is past 400.
            AgentAppUsageHour kept = Assert.Single(rows);
            Assert.True(kept.HourStartUtc > Now.AddDays(-400));
        });
    }

    [Fact]
    public void DeleteLocalData_TakesTheRollupWithIt()
    {
        RunWithStore((store, dbPath) =>
        {
            Accumulate(dbPath, seconds: 5, kind: Porta.Agent.Common.Analysis.AttentionSegmentKinds.Focus, app: AppA, at: Now);
            Assert.NotEmpty(store.ReadAppUsage(Now.AddHours(-1), Now.AddHours(1), TimeSpan.Zero));

            store.DeleteLocalData();

            Assert.Empty(store.ReadAppUsage(Now.AddHours(-1), Now.AddHours(1), TimeSpan.Zero));
        });
    }

    private static AgentAppUsageHour Single(SqliteBrainStateStore store)
        => store.ReadAppUsage(Now.AddDays(-1), Now.AddDays(1), TimeSpan.Zero).Single();

    private static void Accumulate(string dbPath, double seconds, string kind, string app, DateTimeOffset at)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        SqliteBrainStateStore.AccumulateAppUsageRollup(conn, tx, seconds, kind, app, at.ToString("O"));
        tx.Commit();
    }

    private static void DeleteTaskRow(string dbPath, long taskId)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    private static void TriggerPrune(SqliteBrainStateStore store)
        => store.WriteSettings(new AgentSettingsRecord(
            TimestampUtc: Now,
            PrivacyLevel: "standard",
            RegulationIntensity: "balanced",
            DisabledCategories: Array.Empty<string>(),
            NotificationReductionEnabled: false,
            FocusModeEnabled: false,
            AnimationReductionEnabled: false,
            TemporaryAppRestrictionEnabled: false,
            BaselineRecalibrationStartedUtc: null,
            BaselineRecalibrationUntilUtc: null));

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-rollup-tests", Guid.NewGuid().ToString("N"));
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
