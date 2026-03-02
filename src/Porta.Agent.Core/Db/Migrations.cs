using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Db;

internal static class Migrations
{
    internal const string SchemaVersion = "2.0.0";

    internal static void Migrate(SqliteConnection connection)
    {
        DbHealth.EnsureTable(connection);
        EnsureMigrationLedger(connection);

        try
        {
            ApplyStep(connection, "001_base_schema", Schema.ApplyBaseSchema);
            ApplyStep(connection, "002_schema_patches", Schema.ApplySchemaPatches);
            DbHealth.SetSchemaVersion(connection, SchemaVersion);
            DbHealth.SetMigrationFailed(connection, failed: false);
        }
        catch
        {
            DbHealth.SetMigrationFailed(connection, failed: true);
            throw;
        }
    }

    private static void EnsureMigrationLedger(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS migration_ledger (
                migration_id TEXT PRIMARY KEY,
                applied_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ApplyStep(SqliteConnection connection, string migrationId, Action<SqliteConnection> migrationAction)
    {
        if (IsApplied(connection, migrationId))
        {
            return;
        }

        ExecuteNonQuery(connection, "BEGIN IMMEDIATE;");
        try
        {
            migrationAction(connection);
            RecordApplied(connection, migrationId);
            ExecuteNonQuery(connection, "COMMIT;");
        }
        catch
        {
            TryRollback(connection);
            throw;
        }
    }

    private static bool IsApplied(SqliteConnection connection, string migrationId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM migration_ledger WHERE migration_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", migrationId);
        object? row = cmd.ExecuteScalar();
        return row != null;
    }

    private static void RecordApplied(SqliteConnection connection, string migrationId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO migration_ledger (migration_id, applied_utc)
            VALUES ($id, $applied_utc);
            """;
        cmd.Parameters.AddWithValue("$id", migrationId);
        cmd.Parameters.AddWithValue("$applied_utc", DateTimeOffset.UtcNow.ToString("O"));
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
}
