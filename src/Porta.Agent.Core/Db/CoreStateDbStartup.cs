using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Db;

public static class CoreStateDbStartup
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> InitializedDbPaths = new(StringComparer.OrdinalIgnoreCase);

    public static string InitializeAndMigrate(bool logPragmasAtBoot = true, string? dbPathOverride = null)
    {
        lock (Gate)
        {
            string dbPath = string.IsNullOrWhiteSpace(dbPathOverride)
                ? DbPaths.GetCoreStateDbPath()
                : dbPathOverride;
            if (InitializedDbPaths.Contains(dbPath))
            {
                return dbPath;
            }

            string connectionString = DbOpen.BuildConnectionString(dbPath);

            try
            {
                using var connection = DbOpen.OpenConnection(connectionString, logPragmasAtBoot);
                Migrations.Migrate(connection);
                PrimitiveComputer.EnsurePrimitivesPrevStateInitialized(connection);
                InitializedDbPaths.Add(dbPath);
                return dbPath;
            }
            catch
            {
                TrySetMigrationFailed(dbPath);
                throw;
            }
        }
    }

    public static void TrySetMigrationFailed(string? dbPath = null)
    {
        try
        {
            string resolvedPath = string.IsNullOrWhiteSpace(dbPath) ? DbPaths.GetCoreStateDbPath() : dbPath;
            string? directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = new SqliteConnection($"Data Source={resolvedPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS diagnostics (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                INSERT INTO diagnostics (key, value, updated_utc)
                VALUES ('db_migration_failed', 'true', $updated_utc)
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }
}
