using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Db;

internal static class DbOpen
{
    // Pinned durability contract for bucket/state commits: WAL + FULL synchronous.
    private const string PinnedJournalMode = "wal";
    private const int PinnedSynchronousMode = 2; // FULL
    private const int PinnedForeignKeys = 1; // ON
    private const int BusyTimeoutMs = 5000;
    private static int _bootPragmasLogged;

    internal static string BuildConnectionString(string dbPath)
    {
        return $"Data Source={dbPath}";
    }

    internal static SqliteConnection OpenConnection(string connectionString, bool logPragmasAtBoot = false)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        PragmaSnapshot snapshot = ApplyPinnedPragmas(connection);
        if (logPragmasAtBoot && Interlocked.CompareExchange(ref _bootPragmasLogged, 1, 0) == 0)
        {
            Console.WriteLine(
                $"db_pragmas journal_mode={snapshot.JournalMode} synchronous={snapshot.Synchronous} foreign_keys={snapshot.ForeignKeys} busy_timeout={snapshot.BusyTimeoutMs}");
        }

        return connection;
    }

    private static PragmaSnapshot ApplyPinnedPragmas(SqliteConnection connection)
    {
        string requestedJournalMode = ExecuteScalarString(connection, "PRAGMA journal_mode=WAL;");
        if (!string.Equals(requestedJournalMode, PinnedJournalMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SQLite journal_mode contract violation. Expected '{PinnedJournalMode}', got '{requestedJournalMode}'.");
        }

        ExecuteNonQuery(connection, "PRAGMA synchronous=FULL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");
        ExecuteNonQuery(connection, $"PRAGMA busy_timeout={BusyTimeoutMs};");

        string journalMode = ExecuteScalarString(connection, "PRAGMA journal_mode;");
        int synchronous = ExecuteScalarInt(connection, "PRAGMA synchronous;");
        int foreignKeys = ExecuteScalarInt(connection, "PRAGMA foreign_keys;");
        int busyTimeout = ExecuteScalarInt(connection, "PRAGMA busy_timeout;");

        if (!string.Equals(journalMode, PinnedJournalMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SQLite journal_mode contract violation after apply. Expected '{PinnedJournalMode}', got '{journalMode}'.");
        }

        if (synchronous != PinnedSynchronousMode)
        {
            throw new InvalidOperationException(
                $"SQLite synchronous contract violation. Expected '{PinnedSynchronousMode}' (FULL), got '{synchronous}'.");
        }

        if (foreignKeys != PinnedForeignKeys)
        {
            throw new InvalidOperationException(
                $"SQLite foreign_keys contract violation. Expected '{PinnedForeignKeys}', got '{foreignKeys}'.");
        }

        if (busyTimeout != BusyTimeoutMs)
        {
            throw new InvalidOperationException(
                $"SQLite busy_timeout contract violation. Expected '{BusyTimeoutMs}', got '{busyTimeout}'.");
        }

        return new PragmaSnapshot(journalMode, synchronous, foreignKeys, busyTimeout);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string ExecuteScalarString(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        object? value = cmd.ExecuteScalar();
        return value?.ToString()?.Trim() ?? string.Empty;
    }

    private static int ExecuteScalarInt(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        object? value = cmd.ExecuteScalar();
        if (value is long l)
        {
            return checked((int)l);
        }

        if (value is int i)
        {
            return i;
        }

        if (value != null && int.TryParse(value.ToString(), out int parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Failed to read integer PRAGMA value for '{sql}'.");
    }

    private readonly record struct PragmaSnapshot(
        string JournalMode,
        int Synchronous,
        int ForeignKeys,
        int BusyTimeoutMs);
}
