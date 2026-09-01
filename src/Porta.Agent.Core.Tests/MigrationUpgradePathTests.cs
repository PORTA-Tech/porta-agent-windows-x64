using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Db;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// 2026-08-18. 1.2.161 added a column inside migration step 30, which had already shipped.
///
/// A step runs once. Every existing database was already at v30, so the step never ran again and
/// the column was never created — while a brand-new database got it and every test passed. On a
/// real machine every project read then threw "no such column", which broke the Projects page,
/// creating a project, renaming, archiving, and tagging a task to a project.
///
/// These tests exercise the UPGRADE path rather than a fresh database, which is the only way that
/// class of mistake is visible. Fresh-database tests cannot catch it and never will.
/// </summary>
public sealed class MigrationUpgradePathTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 18, 09, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryColumnTheCodeReads_ExistsAfterUpgradingAnOlderDatabase()
    {
        RunWithUpgradedDb(dbPath =>
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            HashSet<string> columns = ReadColumns(conn, "projects");

            // The exact list the project queries select. If a release adds one and forgets a new
            // migration step, this fails on the upgrade path the way the real machine did.
            foreach (string required in new[]
                     {
                         "id", "name", "created_at_utc", "archived_at_utc", "order_index",
                         "client_name", "label_style", "rate_minor", "rate_currency",
                         "status", "start_day", "completion_day", "recipients", "reporting_frequency"
                     })
            {
                Assert.True(columns.Contains(required), $"projects.{required} is missing after upgrade");
            }
        });
    }

    [Fact]
    public void ReadingProjects_WorksAfterUpgrading_NotOnlyOnAFreshDatabase()
    {
        RunWithUpgradedDb(dbPath =>
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);

            // Each of these threw on a real 1.2.161 install. None of them threw in any test.
            AgentProject? created = store.CreateProject("Kestrel", Now);
            Assert.NotNull(created);
            Assert.NotEmpty(store.ReadProjects(includeArchived: true));
            Assert.NotNull(store.RenameProject(created!.Id, "Kestrel Consulting"));
            Assert.NotNull(store.SetProjectArchived(created.Id, true, Now));
        });
    }

    [Fact]
    public void TheV30StatusDefault_BecomesDerive_SoItIsNotPinnedActiveForever()
    {
        RunWithUpgradedDb(dbPath =>
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, reporting_frequency FROM projects;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // Empty means "Ari works it out". A stored value means the user overrode it, and
                // the v30 default was never an answer anybody gave.
                Assert.Equal(string.Empty, reader.GetString(0));
                Assert.Equal(string.Empty, reader.GetString(1));
            }
        });
    }

    /// <summary>
    /// THE DOWNGRADE. What a user sees if they end up back on an older build after a newer one has
    /// already written the database.
    ///
    /// The migration never runs backwards — the guard refuses to open a database newer than the
    /// build understands, so nothing is corrupted and no history is lost. Reinstalling the newer
    /// build brings everything back exactly as it was.
    ///
    /// But the refusal is total, and that is the part worth pinning down. The store constructor
    /// throws, and BOTH hosted services (AgentBrainWorker, BucketFinalizerWorker) take
    /// IBrainStateStore, so nothing that depends on the database can start. This test asserts the
    /// two facts that decide how visible the failure is: the guard names BOTH versions in its
    /// message, and db_migration_failed is written where a diagnostic can find it.
    ///
    /// It cannot assert what the widget shows, because that depends on the local API being up.
    /// See the handover note: it is not up, and the failure reads as an ordinary outage.
    /// </summary>
    [Fact]
    public void ADatabaseFromANewerBuild_IsRefusedClearly_AndFlagged_NeverSilentlyDowngraded()
    {
        RunWithFutureVersionDb(dbPath =>
        {
            // 1. The guard fires, and its message names both numbers — without them a support
            //    conversation cannot tell this apart from ordinary database corruption.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                InvalidOperationException error =
                    Assert.Throws<InvalidOperationException>(() => Migrations.Migrate(conn));
                Assert.Contains("36", error.Message, StringComparison.Ordinal);
                Assert.Contains(
                    Migrations.LatestSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    error.Message,
                    StringComparison.Ordinal);
                Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
            }

            // 2. The failure is FLAGGED in the database rather than only thrown.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM diagnostics WHERE key = 'db_migration_failed';";
                Assert.Equal("true", Convert.ToString(cmd.ExecuteScalar()));
            }

            // 3. Nothing was downgraded. The newer ledger is intact, so reinstalling the newer
            //    build finds its own database exactly as it left it.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MAX(version) FROM migrations;";
                Assert.Equal(36, Convert.ToInt32(cmd.ExecuteScalar()));
            }

            // 4. And the store refuses to construct at all — which is why nothing that depends on
            //    it can start, and why the failure does not reach the user as a database message.
            Assert.ThrowsAny<Exception>(() => new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath));
        });
    }

    /// <summary>
    /// A database written by a build one migration NEWER than this one — exactly what an older
    /// build meets after a newer build has run on the same machine.
    /// </summary>
    private static void RunWithFutureVersionDb(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-downgrade-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "core-state.db");

        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                Migrations.Migrate(conn);

                using var forward = conn.CreateCommand();
                forward.CommandText =
                    "INSERT INTO migrations (version, name, applied_utc, checksum, app_build) "
                    + "VALUES (36, 'from_a_newer_build', $now, 'x', '1.2.999');";
                forward.Parameters.AddWithValue("$now", Now.ToString("O"));
                forward.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            // The service's own startup path, which is what actually runs on a real machine.
            Assert.ThrowsAny<Exception>(
                () => CoreStateDbStartup.InitializeAndMigrate(logPragmasAtBoot: false, dbPathOverride: dbPath));

            SqliteConnection.ClearAllPools();
            body(dbPath);
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

    /// <summary>
    /// Task attribution (2026-09-01) — V35 on a real V34 database.
    ///
    /// The whole risk of this migration is that it touches the tables the client's hours are made
    /// of. It must not: it is purely additive, it creates two new tables and nothing else, and every
    /// hour already measured has to read back to the second afterwards. A fresh-database test cannot
    /// see that, which is the entire reason this file exists.
    /// </summary>
    [Fact]
    public void V35_AddsItsTables_AndLeavesEveryExistingTasksHoursExactlyAsTheyWere()
    {
        RunWithV34Db(dbPath =>
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            // The hours the V34 database was carrying, untouched.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT active_seconds, focused_seconds, offtask_seconds FROM tasks WHERE title = 'existing work';";
                using SqliteDataReader reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(4321d, reader.GetDouble(0), precision: 6);
                Assert.Equal(4000d, reader.GetDouble(1), precision: 6);
                Assert.Equal(321d, reader.GetDouble(2), precision: 6);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT active_seconds FROM task_day_hours WHERE local_date = '2026-08-30';";
                Assert.Equal(4321d, Convert.ToDouble(cmd.ExecuteScalar()), 6);
            }

            // ...and the two new tables exist, with the columns the code reads.
            foreach (string required in new[] { "task_id", "app_hash", "credited_seconds", "source", "first_seen_utc" })
            {
                Assert.True(
                    ReadColumns(conn, "task_app_affinity").Contains(required),
                    $"task_app_affinity.{required} is missing after upgrade");
            }

            foreach (string required in new[]
                     { "task_id", "bucket_start_utc", "local_date", "seconds", "app_hash", "kind", "state" })
            {
                Assert.True(
                    ReadColumns(conn, "pending_task_seconds").Contains(required),
                    $"pending_task_seconds.{required} is missing after upgrade");
            }

            // Both start empty. An upgrade must never invent held time or a footprint, because a
            // footprint learned from history would be learned from the drift this change removes.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT (SELECT COUNT(*) FROM task_app_affinity) + (SELECT COUNT(*) FROM pending_task_seconds);";
                Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
            }
        });
    }

    /// <summary>
    /// A database at V34 with real measured hours on it, then migrated. The tables V35 adds are
    /// dropped rather than the ledger merely wound back, so the step genuinely has to run.
    /// </summary>
    private static void RunWithV34Db(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-upgrade-v35-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "core-state.db");

        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                Migrations.Migrate(conn);

                using (var back = conn.CreateCommand())
                {
                    back.CommandText =
                        "DELETE FROM migrations WHERE version > 34; "
                        + "DROP TABLE IF EXISTS task_app_affinity; "
                        + "DROP TABLE IF EXISTS pending_task_seconds;";
                    back.ExecuteNonQuery();
                }

                using (var seed = conn.CreateCommand())
                {
                    seed.CommandText =
                        "INSERT INTO tasks (created_at_utc, title, planned_seconds, state, "
                        + "active_seconds, focused_seconds, offtask_seconds) "
                        + "VALUES ($now, 'existing work', 7200, 'pending', 4321, 4000, 321); "
                        + "INSERT INTO task_day_hours (local_date, task_id, active_seconds) "
                        + "VALUES ('2026-08-30', (SELECT id FROM tasks WHERE title = 'existing work'), 4321);";
                    seed.Parameters.AddWithValue("$now", Now.ToString("O"));
                    seed.ExecuteNonQuery();
                }
            }

            SqliteConnection.ClearAllPools();

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                Migrations.Migrate(conn);
            }

            SqliteConnection.ClearAllPools();
            body(dbPath);
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

    /// <summary>
    /// Builds a database at the PREVIOUS schema version, the way an installed machine actually
    /// looks, then migrates it — rather than creating one from nothing.
    /// </summary>
    private static void RunWithUpgradedDb(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-upgrade-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "core-state.db");

        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                Migrations.Migrate(conn);

                // Wind the recorded version back to 30 and put the column set back to what 30
                // actually created, so the next Migrate() sees a genuine older install.
                using (var drop = conn.CreateCommand())
                {
                    drop.CommandText =
                        "DELETE FROM migrations WHERE version > 30; "
                        + "UPDATE projects SET status = 'active';";
                    drop.ExecuteNonQuery();
                }

                if (ReadColumns(conn, "projects").Contains("reporting_frequency"))
                {
                    using var strip = conn.CreateCommand();
                    strip.CommandText = "ALTER TABLE projects DROP COLUMN reporting_frequency;";
                    strip.ExecuteNonQuery();
                }

                using (var seed = conn.CreateCommand())
                {
                    seed.CommandText =
                        "INSERT INTO projects (name, created_at_utc, archived_at_utc, order_index, status) "
                        + "VALUES ('Existing', $now, '', 1, 'active');";
                    seed.Parameters.AddWithValue("$now", Now.ToString("O"));
                    seed.ExecuteNonQuery();
                }
            }

            SqliteConnection.ClearAllPools();

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                Migrations.Migrate(conn);
            }

            SqliteConnection.ClearAllPools();
            body(dbPath);
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

    private static HashSet<string> ReadColumns(SqliteConnection conn, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
