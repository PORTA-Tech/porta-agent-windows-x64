using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Db;

internal static class DbOpen
{
    private const int BusyTimeoutMs = 5000;

    internal static string BuildConnectionString(string dbPath)
    {
        return $"Data Source={dbPath}";
    }

    internal static SqliteConnection OpenConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyPinnedPragmas(connection);
        return connection;
    }

    private static void ApplyPinnedPragmas(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous=FULL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");
        ExecuteNonQuery(connection, $"PRAGMA busy_timeout={BusyTimeoutMs};");
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
