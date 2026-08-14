using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Core;

/// <summary>
/// Step 10 phase C/D (2026-08-14) — the address book, and the local record of what was sent.
///
/// Two rules run through everything here:
///
/// <b>The address book never leaves.</b> Only the one address on the report being sent goes anywhere
/// (§12.4). Nothing in this file uploads the book, and there is no endpoint that could.
///
/// <b>A sent report is a snapshot.</b> Rows are dead copies of what the client received, so a re-tag
/// or a re-label changes the next report and never a delivered one (§5). That is why
/// <see cref="RecordSentReport"/> takes the values rather than task ids.
/// </summary>
public sealed partial class SqliteBrainStateStore
{
    // ── the address book ──

    public IReadOnlyList<AgentReportRecipient> ReadRecipients()
    {
        var result = new List<AgentReportRecipient>();

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        // Most recently used first: the person you billed yesterday is the likeliest next.
        cmd.CommandText =
            "SELECT id, name, email, created_at_utc, last_used_utc FROM report_recipients " +
            "ORDER BY last_used_utc DESC, name COLLATE NOCASE ASC;";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AgentReportRecipient(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                Email: reader.GetString(2),
                CreatedAtUtc: ParseUtc(reader.GetString(3)),
                LastUsedUtc: reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }

        return result;
    }

    /// <summary>
    /// Create-or-update, never create-or-fail — the same shape as projects. An address typed again
    /// with a different name updates the name rather than colliding with the unique index, because
    /// the index exists to stop one client being held twice, not to refuse a correction.
    /// </summary>
    public AgentReportRecipient? UpsertRecipient(string name, string email, DateTimeOffset nowUtc)
    {
        string trimmedEmail = (email ?? string.Empty).Trim();
        string trimmedName = (name ?? string.Empty).Trim();

        if (trimmedEmail.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO report_recipients (name, email, created_at_utc)
                    VALUES ($name, $email, $now)
                    ON CONFLICT(email) DO UPDATE SET
                        name = CASE WHEN $name <> '' THEN $name ELSE report_recipients.name END;
                    """;
                cmd.Parameters.AddWithValue("$name", trimmedName);
                cmd.Parameters.AddWithValue("$email", trimmedEmail);
                cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return ReadRecipients().FirstOrDefault(
            r => string.Equals(r.Email, trimmedEmail, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Forgetting a client. A real delete, not a flag: the row holds a third party's address and
    /// "delete" has to mean it (§12.0).
    /// </summary>
    public bool DeleteRecipient(long id)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM report_recipients WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void MarkRecipientUsed(string email, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "UPDATE report_recipients SET last_used_utc = $now WHERE email = $email COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$email", (email ?? string.Empty).Trim());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Whether this address has ever had a report sent to it successfully. Drives the
    /// preview-is-mandatory-for-a-new-client rule: a first send to someone must be looked at first.
    /// </summary>
    public bool HasSentToRecipientBefore(string email)
    {
        string trimmed = (email ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sent_reports " +
            "WHERE recipient_email = $email COLLATE NOCASE AND send_state = 'sent';";
        cmd.Parameters.AddWithValue("$email", trimmed);

        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    // ── the local record of what was sent ──

    /// <summary>
    /// Write the record BEFORE the send is attempted (§12.4, bound 3). A crash between this and the
    /// send leaves a 'pending' row, which is the only reason "did it go?" is answerable afterwards.
    ///
    /// Returns false if this <paramref name="reportId"/> already exists — the id is the idempotency
    /// key (bound 2), and a duplicate is a retry of something already recorded, never a new send.
    /// </summary>
    public bool RecordSentReport(AgentSentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = tx;
                exists.CommandText = "SELECT COUNT(*) FROM sent_reports WHERE report_id = $id;";
                exists.Parameters.AddWithValue("$id", report.ReportId);
                if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
                {
                    TryRollback(tx);
                    return false;
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO sent_reports
                        (report_id, project_id, project_name, client_name, range_start_local,
                         range_end_local, recipient_name, recipient_email, from_name, reply_to,
                         total_hours, days_worked, lines_finished, lines_total, requested_at_utc,
                         sent_at_utc, send_state, send_error, server_state)
                    VALUES
                        ($id, $projectId, $projectName, $clientName, $from, $to, $recipientName,
                         $recipientEmail, $fromName, $replyTo, $totalHours, $daysWorked,
                         $linesFinished, $linesTotal, $requestedAt, '', 'pending', '', 'present');
                    """;
                cmd.Parameters.AddWithValue("$id", report.ReportId);
                cmd.Parameters.AddWithValue("$projectId", report.ProjectId);
                cmd.Parameters.AddWithValue("$projectName", report.ProjectName);
                cmd.Parameters.AddWithValue("$clientName", report.ClientName);
                cmd.Parameters.AddWithValue("$from", report.RangeStartLocal);
                cmd.Parameters.AddWithValue("$to", report.RangeEndLocal);
                cmd.Parameters.AddWithValue("$recipientName", report.RecipientName);
                cmd.Parameters.AddWithValue("$recipientEmail", report.RecipientEmail);
                cmd.Parameters.AddWithValue("$fromName", report.FromName);
                cmd.Parameters.AddWithValue("$replyTo", report.ReplyTo);
                cmd.Parameters.AddWithValue("$totalHours", report.TotalHours);
                cmd.Parameters.AddWithValue("$daysWorked", report.DaysWorked);
                cmd.Parameters.AddWithValue("$linesFinished", report.LinesFinished);
                cmd.Parameters.AddWithValue("$linesTotal", report.LinesTotal);
                cmd.Parameters.AddWithValue("$requestedAt", report.RequestedAtUtc.ToUniversalTime().ToString("O"));
                cmd.ExecuteNonQuery();
            }

            int order = 0;
            foreach (AgentSentReportLine line in report.Lines)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO sent_report_lines (report_id, line_id, client_label, finished, hours, order_index) " +
                    "VALUES ($id, $lineId, $label, $finished, $hours, $order);";
                cmd.Parameters.AddWithValue("$id", report.ReportId);
                cmd.Parameters.AddWithValue("$lineId", line.LineId);
                cmd.Parameters.AddWithValue("$label", line.ClientLabel);
                cmd.Parameters.AddWithValue("$finished", line.Finished ? 1 : 0);
                cmd.Parameters.AddWithValue("$hours", line.Hours);
                cmd.Parameters.AddWithValue("$order", order++);
                cmd.ExecuteNonQuery();
            }

            order = 0;
            foreach (AgentReportDay day in report.Days)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO sent_report_days (report_id, date_local, hours, order_index) " +
                    "VALUES ($id, $date, $hours, $order);";
                cmd.Parameters.AddWithValue("$id", report.ReportId);
                cmd.Parameters.AddWithValue("$date", day.DateLocal);
                cmd.Parameters.AddWithValue("$hours", day.Hours);
                cmd.Parameters.AddWithValue("$order", order++);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// The outcome, written after the attempt. <paramref name="sentAtUtc"/> is the SERVER's timestamp
    /// — the one that corresponds to a real delivery and therefore the one that answers a dispute.
    /// </summary>
    public void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE sent_reports
                SET send_state  = $state,
                    sent_at_utc = $sentAt,
                    send_error  = $error
                WHERE report_id = $id;
                """;
            cmd.Parameters.AddWithValue("$state", sent ? "sent" : "failed");
            cmd.Parameters.AddWithValue("$sentAt", sentAtUtc ?? string.Empty);
            cmd.Parameters.AddWithValue("$error", error ?? string.Empty);
            cmd.Parameters.AddWithValue("$id", reportId);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AgentSentReport> ReadSentReports()
    {
        var reports = new List<AgentSentReport>();
        var lines = new Dictionary<string, List<AgentSentReportLine>>();
        var days = new Dictionary<string, List<AgentReportDay>>();

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT report_id, line_id, client_label, finished, hours FROM sent_report_lines " +
                "ORDER BY report_id, order_index;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                if (!lines.TryGetValue(id, out List<AgentSentReportLine>? list))
                {
                    list = [];
                    lines[id] = list;
                }

                list.Add(new AgentSentReportLine(
                    reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetDouble(4)));
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT report_id, date_local, hours FROM sent_report_days ORDER BY report_id, order_index;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                if (!days.TryGetValue(id, out List<AgentReportDay>? list))
                {
                    list = [];
                    days[id] = list;
                }

                list.Add(new AgentReportDay(reader.GetString(1), reader.GetDouble(2)));
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT report_id, project_id, project_name, client_name, range_start_local,
                       range_end_local, recipient_name, recipient_email, from_name, reply_to,
                       total_hours, days_worked, lines_finished, lines_total, requested_at_utc,
                       sent_at_utc, send_state, send_error, server_state
                FROM sent_reports
                ORDER BY requested_at_utc DESC;
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0);
                reports.Add(new AgentSentReport(
                    ReportId: id,
                    ProjectId: reader.GetInt64(1),
                    ProjectName: reader.GetString(2),
                    ClientName: reader.GetString(3),
                    RangeStartLocal: reader.GetString(4),
                    RangeEndLocal: reader.GetString(5),
                    RecipientName: reader.GetString(6),
                    RecipientEmail: reader.GetString(7),
                    FromName: reader.GetString(8),
                    ReplyTo: reader.GetString(9),
                    TotalHours: reader.GetDouble(10),
                    DaysWorked: (int)reader.GetInt64(11),
                    LinesFinished: (int)reader.GetInt64(12),
                    LinesTotal: (int)reader.GetInt64(13),
                    RequestedAtUtc: ParseUtc(reader.GetString(14)),
                    SentAtUtc: reader.GetString(15),
                    SendState: reader.GetString(16),
                    SendError: reader.GetString(17),
                    ServerState: reader.GetString(18),
                    Lines: lines.GetValueOrDefault(id, []),
                    Days: days.GetValueOrDefault(id, [])));
            }
        }

        return reports;
    }

    /// <summary>
    /// Mark a report as awaiting its server-side delete. Called BEFORE the server is asked, so a
    /// failure leaves the report listed as present rather than gone (§12.6.5).
    /// </summary>
    public void MarkSentReportDeletePending(string reportId)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE sent_reports SET server_state = 'delete_pending' WHERE report_id = $id;";
            cmd.Parameters.AddWithValue("$id", reportId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The local half of a delete, and it runs ONLY after the server half succeeded.
    ///
    /// <b>A half-delete must never report success.</b> This is the failure
    /// <c>primitive_buckets</c> already made once — "delete my local data" reported success and left
    /// 542 MB behind — and it must not happen twice in one product. So the caller confirms the server
    /// first, and if that fails nothing here is called and the report stays listed.
    ///
    /// It takes the recipient's address with it, which is the point: that field belongs to someone
    /// who never installed Ari (§12.0).
    /// </summary>
    public bool DeleteSentReportLocally(string reportId)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            foreach (string sql in new[]
                     {
                         "DELETE FROM sent_report_lines WHERE report_id = $id;",
                         "DELETE FROM sent_report_days WHERE report_id = $id;",
                         "DELETE FROM sent_reports WHERE report_id = $id;"
                     })
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", reportId);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return true;
    }

    /// <summary>
    /// Sent reports are covered by "delete my local data" as well (§12.6.5). Kept here rather than in
    /// the reset list so the three tables cannot be forgotten one at a time.
    /// </summary>
    internal static void DeleteAllReportSendingData(SqliteConnection connection, SqliteTransaction tx)
    {
        foreach (string table in new[]
                 {
                     "sent_report_lines", "sent_report_days", "sent_reports", "report_recipients"
                 })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table};";
            cmd.ExecuteNonQuery();
        }
    }

    private static DateTimeOffset ParseUtc(string raw)
        => DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
