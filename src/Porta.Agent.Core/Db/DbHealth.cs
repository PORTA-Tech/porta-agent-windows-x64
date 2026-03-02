using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Db;

internal static class DbHealth
{
    private const string KeySchemaVersion = "schema_version";
    private const string KeyMigrationFailed = "db_migration_failed";

    internal static void EnsureTable(SqliteConnection connection)
    {
        Schema.ApplyDiagnosticsKeyValueContract(connection);
    }

    internal static void SetSchemaVersion(SqliteConnection connection, string schemaVersion)
    {
        Upsert(connection, KeySchemaVersion, schemaVersion);
    }

    internal static void SetMigrationFailed(SqliteConnection connection, bool failed)
    {
        Upsert(connection, KeyMigrationFailed, failed ? "true" : "false");
    }

    private static void Upsert(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO diagnostics (key, value, updated_utc)
            VALUES ($key, $value, $updated_utc)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
