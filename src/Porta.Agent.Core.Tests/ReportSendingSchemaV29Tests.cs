using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Step 10 phase C/D — schema v29. The address book, and the local record of what was sent.
///
/// These assert against the TABLES rather than through a pipeline, deliberately: the defect this
/// shape exists to prevent is a re-tag rewriting a report a client has already received, and that
/// would be invisible to a test that only ever reads back what it just wrote.
/// </summary>
public sealed class ReportSendingSchemaV29Tests
{
    [Fact]
    public void TheAddressBookAndTheSentRecordExist()
    {
        RunWithDatabase((_, dbPath) =>
        {
            Assert.True(TableExists(dbPath, "report_recipients"));
            Assert.True(TableExists(dbPath, "sent_reports"));
            Assert.True(TableExists(dbPath, "sent_report_lines"));
            Assert.True(TableExists(dbPath, "sent_report_days"));
        });
    }

    /// <summary>
    /// Same reasoning as idx_projects_name. Two rows differing only in case are one client held
    /// twice, and here the duplicate is a third party's address — the one kind of data in this
    /// product that does not belong to the person who installed it (§12.0).
    /// </summary>
    [Fact]
    public void AnAddressCannotBeStoredTwiceInDifferentCase()
    {
        RunWithDatabase((_, dbPath) =>
        {
            Insert(dbPath, "accounts@example-client.test");

            SqliteException error = Assert.Throws<SqliteException>(
                () => Insert(dbPath, "Accounts@Example-Client.TEST"));

            Assert.Contains("UNIQUE", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The record-before-attempt rule (§12.4, bound 3) needs a state that means
    /// "recorded, not yet confirmed sent". A crash between the two must be visible, and a default of
    /// 'sent' would make it invisible.
    /// </summary>
    [Fact]
    public void ASentReportDefaultsToPendingWithNoSentTimestamp()
    {
        RunWithDatabase((_, dbPath) =>
        {
            Exec(dbPath,
                """
                INSERT INTO sent_reports
                    (report_id, range_start_local, range_end_local, recipient_email, requested_at_utc)
                VALUES ('r1', '2026-08-12', '2026-08-13', 'accounts@example-client.test',
                        '2026-08-14T09:00:00.0000000+00:00');
                """);

            Assert.Equal("pending", Scalar(dbPath, "SELECT send_state FROM sent_reports WHERE report_id='r1';"));
            Assert.Equal(string.Empty, Scalar(dbPath, "SELECT sent_at_utc FROM sent_reports WHERE report_id='r1';"));
            Assert.Equal("present", Scalar(dbPath, "SELECT server_state FROM sent_reports WHERE report_id='r1';"));
        });
    }

    /// <summary>
    /// `reportId` is the idempotency key (§12.4, bound 2) and the primary key is what makes "the same
    /// id never sends twice" a property of the database rather than of whoever remembers to check.
    /// </summary>
    [Fact]
    public void TheSameReportIdCannotBeRecordedTwice()
    {
        RunWithDatabase((_, dbPath) =>
        {
            const string Sql =
                """
                INSERT INTO sent_reports
                    (report_id, range_start_local, range_end_local, recipient_email, requested_at_utc)
                VALUES ('same-id', '2026-08-12', '2026-08-13', 'accounts@example-client.test',
                        '2026-08-14T09:00:00.0000000+00:00');
                """;

            Exec(dbPath, Sql);
            SqliteException error = Assert.Throws<SqliteException>(() => Exec(dbPath, Sql));

            Assert.Contains("UNIQUE", error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The sent record must be a DEAD COPY. If a line pointed at tasks.client_label, re-labelling a
    /// task would silently rewrite a document already in a client's inbox — the exact thing §5's
    /// snapshot rule forbids. So: a task's label changes, the sent line does not.
    /// </summary>
    [Fact]
    public void RelabellingATaskDoesNotChangeAReportAlreadySent()
    {
        RunWithDatabase((store, dbPath) =>
        {
            var now = new DateTimeOffset(2026, 08, 14, 9, 0, 0, TimeSpan.Zero);
            AgentTask task = store.CreateTask("working on appolo leads", 3600, now)!;
            store.SetTaskClientLabel(task.Id, "Interview transcripts");

            Exec(dbPath,
                $"""
                INSERT INTO sent_reports
                    (report_id, range_start_local, range_end_local, recipient_email, requested_at_utc)
                VALUES ('r1', '2026-08-12', '2026-08-13', 'accounts@example-client.test',
                        '2026-08-14T09:00:00.0000000+00:00');
                INSERT INTO sent_report_lines (report_id, line_id, client_label, finished, hours, order_index)
                VALUES ('r1', 'a1', 'Interview transcripts', 1, 4.9, 0);
                """);

            // The user renames it afterwards, which §3 explicitly allows.
            store.SetTaskClientLabel(task.Id, "Something else entirely");

            Assert.Equal(
                "Interview transcripts",
                Scalar(dbPath, "SELECT client_label FROM sent_report_lines WHERE report_id='r1';"));
        });
    }

    // ── helpers ──

    private static void Insert(string dbPath, string email)
        => Exec(dbPath,
            $"INSERT INTO report_recipients (name, email, created_at_utc) " +
            $"VALUES ('Client', '{email}', '2026-08-14T09:00:00.0000000+00:00');");

    private static void Exec(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? value = cmd.ExecuteScalar();
        return value is null || value == DBNull.Value
            ? string.Empty
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TableExists(string dbPath, string table)
        => Scalar(dbPath, $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}';") == table;

    private static void RunWithDatabase(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-report-send-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            body(store, dbPath);
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
                // A leaked temp database is a mess, not a test failure. §11c is the real fix.
            }
        }
    }
}
