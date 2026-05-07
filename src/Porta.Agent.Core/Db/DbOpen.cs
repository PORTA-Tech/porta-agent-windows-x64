using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Db;

internal static class DbOpen
{
    // WAL + NORMAL synchronous: safe for WAL mode per SQLite docs, eliminates the second
    // fsync on every commit. FULL required two syncs (WAL data + WAL header update) which
    // caused 1-5s stalls on Windows when the I/O subsystem was busy, exhausting the thread
    // pool and causing intermittent API timeouts. NORMAL is sufficient — the WAL guarantees
    // crash-safety without the header sync.
    private const string PinnedJournalMode = "wal";
    private const int PinnedSynchronousMode = 1; // NORMAL (was FULL=2, safe to reduce with WAL)
    private const int PinnedForeignKeys = 1; // ON
    private const int DefaultBusyTimeoutMs = 5000;
    private static int _bootPragmasLogged;

    internal static string BuildConnectionString(string dbPath)
    {
        return $"Data Source={dbPath}";
    }

    internal static SqliteConnection OpenConnection(
        string connectionString,
        bool logPragmasAtBoot = false,
        int? busyTimeoutMs = null)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        PragmaSnapshot snapshot = ApplyPinnedPragmas(connection, busyTimeoutMs ?? DefaultBusyTimeoutMs);
        if (logPragmasAtBoot && Interlocked.CompareExchange(ref _bootPragmasLogged, 1, 0) == 0)
        {
            Console.WriteLine(
                $"db_pragmas journal_mode={snapshot.JournalMode} synchronous={snapshot.Synchronous} foreign_keys={snapshot.ForeignKeys} busy_timeout={snapshot.BusyTimeoutMs}");
        }

        return connection;
    }

    internal static SqliteConnection OpenReadConnection(
        string connectionString,
        int? busyTimeoutMs = null)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        ApplyReadSessionPragmas(connection, busyTimeoutMs ?? DefaultBusyTimeoutMs);
        return connection;
    }

    private static PragmaSnapshot ApplyPinnedPragmas(SqliteConnection connection, int busyTimeoutMs)
    {
        string requestedJournalMode = ExecuteScalarString(connection, "PRAGMA journal_mode=WAL;");
        if (!string.Equals(requestedJournalMode, PinnedJournalMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SQLite journal_mode contract violation. Expected '{PinnedJournalMode}', got '{requestedJournalMode}'.");
        }

        ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");
        ExecuteNonQuery(connection, $"PRAGMA busy_timeout={Math.Max(0, busyTimeoutMs)};");
        // 50 MB page cache keeps the state_buckets B-tree in memory so that the
        // ReadTimeline INNER JOIN (489 primary-key lookups) hits the cache rather
        // than causing random disk reads (~1-5ms each = 2-10s per timeline call).
        // Default is -2000 (2 MB) which is far too small for a 3.5 GB database.
        ExecuteNonQuery(connection, "PRAGMA cache_size=-51200;");

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
                $"SQLite synchronous contract violation. Expected '{PinnedSynchronousMode}' (NORMAL), got '{synchronous}'.");
        }

        if (foreignKeys != PinnedForeignKeys)
        {
            throw new InvalidOperationException(
                $"SQLite foreign_keys contract violation. Expected '{PinnedForeignKeys}', got '{foreignKeys}'.");
        }

        if (busyTimeout != Math.Max(0, busyTimeoutMs))
        {
            throw new InvalidOperationException(
                $"SQLite busy_timeout contract violation. Expected '{Math.Max(0, busyTimeoutMs)}', got '{busyTimeout}'.");
        }

        return new PragmaSnapshot(journalMode, synchronous, foreignKeys, busyTimeout);
    }

    private static void ApplyReadSessionPragmas(SqliteConnection connection, int busyTimeoutMs)
    {
        ExecuteNonQuery(connection, $"PRAGMA busy_timeout={Math.Max(0, busyTimeoutMs)};");

        int busyTimeout = ExecuteScalarInt(connection, "PRAGMA busy_timeout;");
        if (busyTimeout != Math.Max(0, busyTimeoutMs))
        {
            throw new InvalidOperationException(
                $"SQLite read busy_timeout contract violation. Expected '{Math.Max(0, busyTimeoutMs)}', got '{busyTimeout}'.");
        }
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
