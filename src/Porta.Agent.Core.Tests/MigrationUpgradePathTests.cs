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
