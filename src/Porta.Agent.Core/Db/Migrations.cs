using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Porta.Agent.Core.Db;

internal static class Migrations
{
    private const int LatestSchemaVersion = 4;

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
            Apply: Schema.ApplyDiagnosticsKeyValueContract)
    ];

    internal static void Migrate(SqliteConnection connection)
    {
        try
        {
            DbHealth.EnsureTable(connection);
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
