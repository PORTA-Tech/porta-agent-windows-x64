using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Porta.Agent.Core.Db;

internal static class Migrations
{
    // DERIVED from the migration list — never hand-maintain this again. On 2026-07-09 it was a
    // hardcoded 21 while reminders_v22 was already in OrderedSteps below; once a DB reached v22 the
    // guard in Migrate() rejected it, the service's local-API listener died silently, and every
    // agent showed AWAY. Deriving it makes that drift impossible: the supported version is always
    // exactly the newest migration.
    internal static int LatestSchemaVersion => OrderedSteps.Max(step => step.Version);

    // Exposed (internal) only so the migration-numbering consistency test can assert the list is sane.
    internal static int[] OrderedStepVersions => Array.ConvertAll(OrderedSteps, step => step.Version);

    private static readonly MigrationStep[] OrderedSteps =
    [
        new(
            Version: 1,
            Name: "base_schema",
            Apply: Schema.ApplyBaseSchema),
        new(
            Version: 2,
            Name: "schema_patches",
            Apply: Schema.ApplySchemaPatches),
        new(
            Version: 3,
            Name: "contract_tables_events_columns",
            Apply: Schema.ApplyContractSchemaV3),
        new(
            Version: 4,
            Name: "diagnostics_key_value_contract",
            Apply: Schema.ApplyDiagnosticsKeyValueContract),
        new(
            Version: 5,
            Name: "index_contract_v5",
            Apply: Schema.ApplyIndexContractV5),
        new(
            Version: 6,
            Name: "input_buckets_contract_v6",
            Apply: Schema.ApplyInputBucketsContractV6),
        new(
            Version: 7,
            Name: "bucket_finalize_upsert_contract_v7",
            Apply: Schema.ApplyBucketFinalizeUpsertContractV7),
        new(
            Version: 8,
            Name: "input_bucket_metrics_contract_v8",
            Apply: Schema.ApplyInputBucketMetricsContractV8),
        new(
            Version: 9,
            Name: "baseline_stats_contract_v9",
            Apply: Schema.ApplyBaselineStatsContractV9),
        new(
            Version: 10,
            Name: "baseline_stats_welford_contract_v10",
            Apply: Schema.ApplyBaselineStatsWelfordContractV10),
        new(
            Version: 11,
            Name: "input_buckets_bucket_seq_contract_v11",
            Apply: Schema.ApplyInputBucketsBucketSeqContractV11),
        new(
            Version: 12,
            Name: "metric_bounds_contract_v12",
            Apply: Schema.ApplyMetricBoundsContractV12),
        new(
            Version: 13,
            Name: "primitive_buckets_json_contract_v13",
            Apply: Schema.ApplyPrimitiveBucketsJsonContractV13),
        new(
            Version: 14,
            Name: "state_buckets_derived_session_contract_v14",
            Apply: Schema.ApplyStateBucketsDerivedSessionContractV14),
        new(
            Version: 15,
            Name: "input_buckets_session_fields_contract_v15",
            Apply: Schema.ApplyInputBucketsSessionFieldsContractV15),
        new(
            Version: 16,
            Name: "state_buckets_state_flags_contract_v16",
            Apply: Schema.ApplyStateBucketsStateFlagsContractV16),
        new(
            Version: 17,
            Name: "events_idempotency_unique_contract_v17",
            Apply: Schema.ApplyEventsIdempotencyUniqueContractV17),
        new(
            Version: 18,
            Name: "interventions_delivery_contract_v18",
            Apply: Schema.ApplyInterventionsDeliveryContractV18),
        new(
            Version: 19,
            Name: "brain_snapshots_cleanup_vacuum_v19",
            Apply: Schema.ApplyBrainSnapshotsCleanupV19),
        new(
            Version: 20,
            Name: "ground_truth_labels_v20",
            Apply: Schema.ApplyGroundTruthLabelsV20),
        new(
            Version: 21,
            Name: "prompt_events_v21",
            Apply: Schema.ApplyPromptEventsV21),
        new(
            Version: 22,
            Name: "reminders_v22",
            Apply: Schema.ApplyRemindersV22),
        new(
            Version: 23,
            Name: "tasks_v23",
            Apply: Schema.ApplyTasksV23),
        new(
            Version: 24,
            Name: "daily_goals_v24",
            Apply: Schema.ApplyDailyGoalsV24),
        // Complaint 3 (2026-08-11) — "Tomorrow" on the close-out card has to persist, or card four
        // is theatre. Adds tasks.deferred_until_day. Additive, defaulted, local only.
        new(
            Version: 25,
            Name: "tasks_defer_v25",
            Apply: Schema.ApplyTasksDeferV25),
        // Step 9a (2026-08-12) — projects a task can belong to. Adds the projects table and
        // tasks.project_id (0 = untagged). Additive, defaulted, LOCAL ONLY: nothing new leaves
        // the machine until 9b.
        new(
            Version: 26,
            Name: "projects_v26",
            Apply: Schema.ApplyProjectsV26),
        // 2026-08-12 — the hourly app-usage rollup, so a project's picture can outlive the
        // 14-day capture window. Creates the table AND backfills it from whatever
        // input_signal_windows still holds, which is the only chance those days will get.
        new(
            Version: 27,
            Name: "app_usage_rollup_v27",
            Apply: Schema.ApplyAppUsageRollupV27),
        // Step 10 phase A (2026-08-13) — client-facing labels for tasks and projects, and the
        // user-side rate. Additive, defaulted, LOCAL ONLY: no payload gains a field in this phase.
        new(
            Version: 28,
            Name: "report_labels_v28",
            Apply: Schema.ApplyReportLabelsV28),
        // Step 10 phase C/D (2026-08-14) — the address book and the local record of what was sent.
        // The book is local only; the record is a dead snapshot so a re-tag can never rewrite what a
        // client has already received.
        new(
            Version: 29,
            Name: "report_sending_v29",
            Apply: Schema.ApplyReportSendingV29),
        // Phase 0.1 + 1.1 + 1.2 (2026-08-17) — hours per task per DAY, plus the task and project
        // fields the client report is made of. Additive, defaulted, LOCAL ONLY. The day table has
        // no backfill on purpose: see ApplyReportContentV30.
        new(
            Version: 30,
            Name: "report_content_v30",
            Apply: Schema.ApplyReportContentV30),
        // Phase 3 rebuilt (2026-08-18) — the column 1.2.161 tried to add inside step 30 and so
        // never delivered to an existing database, plus the derived/asked reset. See
        // ApplyDerivedProjectFieldsV31 for why editing a shipped step was the mistake.
        new(
            Version: 31,
            Name: "derived_project_fields_v31",
            Apply: Schema.ApplyDerivedProjectFieldsV31)
    ];

    internal static void Migrate(SqliteConnection connection)
    {
        try
        {
            DbHealth.EnsureTable(connection);
            Schema.EnsureInputSignalWindowsContract(connection);
            EnsureMigrationsTable(connection);

            int currentVersion = GetCurrentVersion(connection);
            if (currentVersion > LatestSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Database schema version {currentVersion} is newer than supported version {LatestSchemaVersion}.");
            }

            foreach (MigrationStep step in OrderedSteps)
            {
                if (step.Version <= currentVersion)
                {
                    continue;
                }

                ApplyStep(connection, step);
                currentVersion = step.Version;
            }

            Schema.AssertEventsContract(connection);
            Schema.AssertDiagnosticsContract(connection);
            Schema.AssertInputBucketsContract(connection);
            Schema.AssertBaselineStatsContract(connection);
            Schema.AssertMetricBoundsContract(connection);
            Schema.AssertPrimitiveBucketsContract(connection);
            Schema.AssertStateBucketsDerivedSessionContract(connection);
            Schema.AssertBucketFinalizeUpsertContract(connection);
            DbHealth.SetSchemaVersion(connection, currentVersion.ToString(CultureInfo.InvariantCulture));
            DbHealth.SetMigrationFailed(connection, failed: false);
        }
        catch
        {
            DbHealth.SetMigrationFailed(connection, failed: true);
            throw;
        }
    }

    private static void EnsureMigrationsTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_utc TEXT NOT NULL,
                checksum TEXT,
                app_build TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static int GetCurrentVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM migrations ORDER BY version ASC;";
        using SqliteDataReader reader = cmd.ExecuteReader();

        int expected = 1;
        int lastSeen = 0;
        while (reader.Read())
        {
            int version = reader.GetInt32(0);
            if (version != expected)
            {
                throw new InvalidOperationException(
                    $"Migration ledger is not contiguous. Expected version {expected}, found {version}.");
            }

            lastSeen = version;
            expected++;
        }

        return lastSeen;
    }

    private static void ApplyStep(SqliteConnection connection, MigrationStep step)
    {
        ExecuteNonQuery(connection, "BEGIN IMMEDIATE;");
        try
        {
            step.Apply(connection);
            RecordApplied(connection, step);
            DbHealth.SetSchemaVersion(connection, step.Version.ToString(CultureInfo.InvariantCulture));
            DbHealth.SetMigrationFailed(connection, failed: false);
            ExecuteNonQuery(connection, "COMMIT;");
        }
        catch
        {
            TryRollback(connection);
            throw;
        }
    }

    private static void RecordApplied(SqliteConnection connection, MigrationStep step)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO migrations (version, name, applied_utc, checksum, app_build)
            VALUES ($version, $name, $applied_utc, $checksum, $app_build);
            """;
        cmd.Parameters.AddWithValue("$version", step.Version);
        cmd.Parameters.AddWithValue("$name", step.Name);
        cmd.Parameters.AddWithValue("$applied_utc", Time.toIsoUtcMsString(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        cmd.Parameters.AddWithValue("$checksum", ComputeChecksum(step));
        cmd.Parameters.AddWithValue("$app_build", ResolveAppBuild());
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            ExecuteNonQuery(connection, "ROLLBACK;");
        }
        catch
        {
        }
    }

    private static string ComputeChecksum(MigrationStep step)
    {
        string seed = $"{step.Version}|{step.Name}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash);
    }

    private static string ResolveAppBuild()
    {
        string? version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }

    private readonly record struct MigrationStep(
        int Version,
        string Name,
        Action<SqliteConnection> Apply);
}
