using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core;

public sealed class SqliteBrainStateStore : IBrainStateStore
{
    private const int DefaultRetentionDays = 90;
    private const int DefaultEventCooldownSeconds = 20;

    private readonly object _gate = new();
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly TimeSpan _retention = TimeSpan.FromDays(DefaultRetentionDays);
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
    private string? _lastAutoEventAction;
    private DateTimeOffset _lastAutoEventAtUtc = DateTimeOffset.MinValue;

    public SqliteBrainStateStore()
    {
        _dbPath = BuildDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = $"Data Source={_dbPath}";
        EnsureSchema();
    }

    public void WriteSnapshot(AgentBrainSnapshot snapshot)
    {
        lock (_gate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            InsertInputBucket(connection, tx, snapshot);
            InsertStateBucket(connection, tx, snapshot);
            InsertLegacySnapshot(connection, tx, snapshot);
            TryInsertAutomaticInterventionEvent(connection, tx, snapshot);
            PruneIfNeeded(connection, tx, snapshot.TimestampUtc);

            tx.Commit();
        }
    }

    public void WriteControlEvent(AgentControlEvent controlEvent)
    {
        lock (_gate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            InsertInterventionEvent(connection, tx, controlEvent);
            PruneIfNeeded(connection, tx, controlEvent.TimestampUtc);

            tx.Commit();
        }
    }

    public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics)
    {
        lock (_gate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO diagnostics_snapshots (
                    timestamp_utc,
                    backend_connected,
                    consent_active,
                    paused,
                    last_error,
                    policy_version,
                    schema_version,
                    agent_version,
                    last_event_timestamp_utc
                ) VALUES (
                    $ts, $backend, $consent, $paused, $last_error, $policy, $schema, $agent, $last_event
                );
                """;
            cmd.Parameters.AddWithValue("$ts", diagnostics.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$backend", diagnostics.BackendConnected ? 1 : 0);
            cmd.Parameters.AddWithValue("$consent", diagnostics.ConsentActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$paused", diagnostics.Paused ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_error", diagnostics.LastError ?? string.Empty);
            cmd.Parameters.AddWithValue("$policy", diagnostics.PolicyVersion);
            cmd.Parameters.AddWithValue("$schema", diagnostics.SchemaVersion);
            cmd.Parameters.AddWithValue("$agent", diagnostics.AgentVersion);
            cmd.Parameters.AddWithValue("$last_event", diagnostics.LastEventTimestampUtc?.ToString("O") ?? string.Empty);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, diagnostics.TimestampUtc);
            tx.Commit();
        }
    }

    public AgentBrainSnapshot? ReadLatestSnapshot()
    {
        lock (_gate)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    timestamp_utc,
                    typing_burst_intensity,
                    scroll_velocity_index,
                    focus_switch_rate,
                    focus_strength,
                    overload_likelihood,
                    fatigue_probability,
                    cognitive_stability,
                    confidence,
                    last_action,
                    contributors_json
                FROM brain_snapshots
                ORDER BY id DESC
                LIMIT 1;
                """;

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AgentBrainSnapshot(
                TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                TypingBurstIntensity: reader.GetDouble(1),
                ScrollVelocityIndex: reader.GetDouble(2),
                FocusSwitchRate: reader.GetDouble(3),
                FocusStrength: reader.GetDouble(4),
                OverloadLikelihood: reader.GetDouble(5),
                FatigueProbability: reader.GetDouble(6),
                CognitiveStability: reader.GetDouble(7),
                Confidence: reader.GetDouble(8),
                LastAction: reader.GetString(9),
                Contributors: DeserializeContributors(reader.GetString(10)));
        }
    }

    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = 5)
    {
        lock (_gate)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int bucket = Math.Max(1, bucketSeconds);
            var rawRows = new List<AgentBrainSnapshot>();

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    timestamp_utc,
                    typing_burst_intensity,
                    scroll_velocity_index,
                    focus_switch_rate,
                    focus_strength,
                    overload_likelihood,
                    fatigue_probability,
                    cognitive_stability,
                    confidence,
                    last_action,
                    contributors_json
                FROM brain_snapshots
                WHERE timestamp_utc >= $from AND timestamp_utc <= $to
                ORDER BY timestamp_utc ASC;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rawRows.Add(new AgentBrainSnapshot(
                    TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                    TypingBurstIntensity: reader.GetDouble(1),
                    ScrollVelocityIndex: reader.GetDouble(2),
                    FocusSwitchRate: reader.GetDouble(3),
                    FocusStrength: reader.GetDouble(4),
                    OverloadLikelihood: reader.GetDouble(5),
                    FatigueProbability: reader.GetDouble(6),
                    CognitiveStability: reader.GetDouble(7),
                    Confidence: reader.GetDouble(8),
                    LastAction: reader.GetString(9),
                    Contributors: DeserializeContributors(reader.GetString(10))));
            }

            if (bucket == 1 || rawRows.Count <= 1)
            {
                return rawRows;
            }

            var byBucket = new Dictionary<long, AgentBrainSnapshot>();
            foreach (AgentBrainSnapshot row in rawRows)
            {
                long key = row.TimestampUtc.ToUnixTimeSeconds() / bucket;
                byBucket[key] = row;
            }

            return byBucket
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToList();
        }
    }

    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50)
    {
        lock (_gate)
        {
            int boundedLimit = Math.Clamp(limit, 1, 500);
            var rows = new List<AgentControlEvent>(boundedLimit);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    timestamp_utc,
                    action,
                    trigger_state,
                    trigger_value,
                    confidence,
                    duration_seconds,
                    escalation_level,
                    reversible,
                    reason,
                    contributors_json
                FROM interventions
                ORDER BY timestamp_utc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AgentControlEvent(
                    TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                    Action: reader.GetString(1),
                    TriggerState: reader.GetString(2),
                    TriggerValue: reader.GetDouble(3),
                    Confidence: reader.GetDouble(4),
                    DurationSeconds: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    EscalationLevel: reader.GetString(6),
                    Reversible: reader.GetInt32(7) == 1,
                    Reason: reader.GetString(8),
                    Contributors: DeserializeContributors(reader.GetString(9))));
            }

            return rows;
        }
    }

    public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null)
    {
        lock (_gate)
        {
            DateOnly day = dateUtc ?? DateOnly.FromDateTime(DateTime.UtcNow);
            DateTimeOffset startUtc = new(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset endUtc = startUtc.AddDays(1);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            int totalInterventions = ReadSingleInt(connection,
                """
                SELECT COUNT(1) FROM interventions
                WHERE timestamp_utc >= $start AND timestamp_utc < $end;
                """,
                startUtc, endUtc);

            double avgFocus = ReadSingleDouble(connection,
                """
                SELECT COALESCE(AVG(focus_strength), 0)
                FROM state_buckets
                WHERE timestamp_utc >= $start AND timestamp_utc < $end;
                """,
                startUtc, endUtc);

            double peakOverload = ReadSingleDouble(connection,
                """
                SELECT COALESCE(MAX(overload_likelihood), 0)
                FROM state_buckets
                WHERE timestamp_utc >= $start AND timestamp_utc < $end;
                """,
                startUtc, endUtc);

            double idleFragmentation = ReadSingleDouble(connection,
                """
                SELECT COALESCE(AVG(focus_switch_rate), 0)
                FROM input_buckets
                WHERE timestamp_utc >= $start AND timestamp_utc < $end;
                """,
                startUtc, endUtc);

            int? mostVolatileHour = ReadMostVolatileHour(connection, startUtc, endUtc);

            return new AgentTodaySummary(
                DateUtc: day,
                TotalInterventions: totalInterventions,
                AverageFocusStrength: avgFocus,
                PeakOverloadLikelihood: peakOverload,
                IdleFragmentationIndex: idleFragmentation,
                MostVolatileHourUtc: mostVolatileHour);
        }
    }

    public string ExportBundle(string logFilePath, string consentPath)
    {
        lock (_gate)
        {
            string exportRoot = BuildExportRoot();
            Directory.CreateDirectory(exportRoot);
            string exportDir = Path.Combine(exportRoot, $"porta-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(exportDir);

            string dbTarget = Path.Combine(exportDir, "core-state.db");
            if (File.Exists(_dbPath))
            {
                File.Copy(_dbPath, dbTarget, overwrite: true);
            }

            if (File.Exists(logFilePath))
            {
                File.Copy(logFilePath, Path.Combine(exportDir, "service.log"), overwrite: true);
            }

            if (File.Exists(consentPath))
            {
                File.Copy(consentPath, Path.Combine(exportDir, "consent-state-v2.json"), overwrite: true);
            }

            string metadata = JsonSerializer.Serialize(new
            {
                generated_at_utc = DateTimeOffset.UtcNow,
                schema = "v2",
                db_path = _dbPath,
                retention_days = DefaultRetentionDays
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(exportDir, "metadata.json"), metadata);

            string zipPath = $"{exportDir}.zip";
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(exportDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            return zipPath;
        }
    }

    private void InsertInputBucket(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO input_buckets (
                timestamp_utc,
                typing_burst_intensity,
                scroll_velocity_index,
                focus_switch_rate
            ) VALUES ($ts, $typing, $scroll, $focus_switch);
            """;
        cmd.Parameters.AddWithValue("$ts", snapshot.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$typing", snapshot.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll", snapshot.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch", snapshot.FocusSwitchRate);
        cmd.ExecuteNonQuery();
    }

    private void InsertStateBucket(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO state_buckets (
                timestamp_utc,
                focus_strength,
                overload_likelihood,
                fatigue_probability,
                cognitive_stability,
                confidence,
                contributors_json
            ) VALUES ($ts, $focus, $overload, $fatigue, $stability, $confidence, $contributors);
            """;
        cmd.Parameters.AddWithValue("$ts", snapshot.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$focus", snapshot.FocusStrength);
        cmd.Parameters.AddWithValue("$overload", snapshot.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$fatigue", snapshot.FatigueProbability);
        cmd.Parameters.AddWithValue("$stability", snapshot.CognitiveStability);
        cmd.Parameters.AddWithValue("$confidence", snapshot.Confidence);
        cmd.Parameters.AddWithValue("$contributors", JsonSerializer.Serialize(snapshot.Contributors));
        cmd.ExecuteNonQuery();
    }

    private void InsertLegacySnapshot(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO brain_snapshots (
                timestamp_utc,
                typing_burst_intensity,
                scroll_velocity_index,
                focus_switch_rate,
                focus_strength,
                overload_likelihood,
                fatigue_probability,
                cognitive_stability,
                confidence,
                last_action,
                contributors_json
            ) VALUES (
                $ts, $typing, $scroll, $focus_switch, $focus_strength, $overload,
                $fatigue, $stability, $confidence, $last_action, $contributors
            );
            """;
        cmd.Parameters.AddWithValue("$ts", snapshot.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$typing", snapshot.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll", snapshot.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch", snapshot.FocusSwitchRate);
        cmd.Parameters.AddWithValue("$focus_strength", snapshot.FocusStrength);
        cmd.Parameters.AddWithValue("$overload", snapshot.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$fatigue", snapshot.FatigueProbability);
        cmd.Parameters.AddWithValue("$stability", snapshot.CognitiveStability);
        cmd.Parameters.AddWithValue("$confidence", snapshot.Confidence);
        cmd.Parameters.AddWithValue("$last_action", snapshot.LastAction);
        cmd.Parameters.AddWithValue("$contributors", JsonSerializer.Serialize(snapshot.Contributors));
        cmd.ExecuteNonQuery();
    }

    private void TryInsertAutomaticInterventionEvent(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.LastAction) || IsNonInterventionAction(snapshot.LastAction))
        {
            return;
        }

        if (string.Equals(_lastAutoEventAction, snapshot.LastAction, StringComparison.Ordinal)
            && (snapshot.TimestampUtc - _lastAutoEventAtUtc) < TimeSpan.FromSeconds(DefaultEventCooldownSeconds))
        {
            return;
        }

        (string triggerState, double triggerValue) = ResolveTrigger(snapshot);

        var ev = new AgentControlEvent(
            TimestampUtc: snapshot.TimestampUtc,
            Action: snapshot.LastAction,
            TriggerState: triggerState,
            TriggerValue: triggerValue,
            Confidence: snapshot.Confidence,
            DurationSeconds: null,
            EscalationLevel: ResolveEscalationLevel(snapshot.LastAction),
            Reversible: string.Equals(snapshot.LastAction, "focus_mode_enabled", StringComparison.Ordinal),
            Reason: $"{snapshot.LastAction} from {triggerState}={triggerValue:F2}, confidence={snapshot.Confidence:F2}",
            Contributors: snapshot.Contributors);

        InsertInterventionEvent(connection, tx, ev);
        _lastAutoEventAction = snapshot.LastAction;
        _lastAutoEventAtUtc = snapshot.TimestampUtc;
    }

    private static bool IsNonInterventionAction(string action)
    {
        return string.Equals(action, "monitor_only", StringComparison.Ordinal)
            || string.Equals(action, "paused_by_user", StringComparison.Ordinal)
            || string.Equals(action, "consent_required", StringComparison.Ordinal)
            || string.Equals(action, "starting", StringComparison.Ordinal);
    }

    private static (string triggerState, double triggerValue) ResolveTrigger(AgentBrainSnapshot snapshot)
    {
        if (string.Equals(snapshot.LastAction, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return ("overload", snapshot.OverloadLikelihood);
        }

        if (string.Equals(snapshot.LastAction, "break_suggestion", StringComparison.Ordinal))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

        if (string.Equals(snapshot.LastAction, "maintain_focus", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        double dominant = Math.Max(snapshot.FocusStrength, Math.Max(snapshot.OverloadLikelihood, snapshot.FatigueProbability));
        string state = dominant == snapshot.FocusStrength
            ? "focus"
            : dominant == snapshot.OverloadLikelihood
                ? "overload"
                : "fatigue";

        return (state, dominant);
    }

    private static string ResolveEscalationLevel(string action)
    {
        if (string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return "Nudge";
        }

        if (string.Equals(action, "break_suggestion", StringComparison.Ordinal))
        {
            return "Suggest";
        }

        return "Inform";
    }

    private static void InsertInterventionEvent(SqliteConnection connection, SqliteTransaction tx, AgentControlEvent controlEvent)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO interventions (
                timestamp_utc,
                action,
                trigger_state,
                trigger_value,
                confidence,
                duration_seconds,
                escalation_level,
                reversible,
                reason,
                contributors_json
            ) VALUES (
                $ts, $action, $trigger_state, $trigger_value, $confidence, $duration,
                $escalation, $reversible, $reason, $contributors
            );
            """;
        cmd.Parameters.AddWithValue("$ts", controlEvent.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$action", controlEvent.Action);
        cmd.Parameters.AddWithValue("$trigger_state", controlEvent.TriggerState);
        cmd.Parameters.AddWithValue("$trigger_value", controlEvent.TriggerValue);
        cmd.Parameters.AddWithValue("$confidence", controlEvent.Confidence);
        if (controlEvent.DurationSeconds.HasValue)
        {
            cmd.Parameters.AddWithValue("$duration", controlEvent.DurationSeconds.Value);
        }
        else
        {
            cmd.Parameters.AddWithValue("$duration", DBNull.Value);
        }
        cmd.Parameters.AddWithValue("$escalation", controlEvent.EscalationLevel);
        cmd.Parameters.AddWithValue("$reversible", controlEvent.Reversible ? 1 : 0);
        cmd.Parameters.AddWithValue("$reason", controlEvent.Reason);
        cmd.Parameters.AddWithValue("$contributors", JsonSerializer.Serialize(controlEvent.Contributors));
        cmd.ExecuteNonQuery();
    }

    private int ReadSingleInt(SqliteConnection connection, string sql, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        object? scalar = cmd.ExecuteScalar();
        return scalar is long l ? (int)l : Convert.ToInt32(scalar ?? 0);
    }

    private static double ReadSingleDouble(SqliteConnection connection, string sql, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        object? scalar = cmd.ExecuteScalar();
        return scalar is double d ? d : Convert.ToDouble(scalar ?? 0d);
    }

    private static int? ReadMostVolatileHour(SqliteConnection connection, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT CAST(strftime('%H', timestamp_utc) AS INTEGER) AS hour_utc
            FROM state_buckets
            WHERE timestamp_utc >= $start AND timestamp_utc < $end
            GROUP BY hour_utc
            ORDER BY AVG(ABS(overload_likelihood - focus_strength)) DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt32(scalar);
    }

    private void PruneIfNeeded(SqliteConnection connection, SqliteTransaction tx, DateTimeOffset nowUtc)
    {
        if ((nowUtc - _lastPruneUtc) < TimeSpan.FromMinutes(30))
        {
            return;
        }

        DateTimeOffset cutoff = nowUtc.Subtract(_retention);
        string cutoffRaw = cutoff.ToString("O");

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            DELETE FROM input_buckets WHERE timestamp_utc < $cutoff;
            DELETE FROM state_buckets WHERE timestamp_utc < $cutoff;
            DELETE FROM interventions WHERE timestamp_utc < $cutoff;
            DELETE FROM diagnostics_snapshots WHERE timestamp_utc < $cutoff;
            DELETE FROM brain_snapshots WHERE timestamp_utc < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoffRaw);
        cmd.ExecuteNonQuery();

        _lastPruneUtc = nowUtc;
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS input_buckets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                typing_burst_intensity REAL NOT NULL,
                scroll_velocity_index REAL NOT NULL,
                focus_switch_rate REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS state_buckets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                focus_strength REAL NOT NULL,
                overload_likelihood REAL NOT NULL,
                fatigue_probability REAL NOT NULL,
                cognitive_stability REAL NOT NULL,
                confidence REAL NOT NULL,
                contributors_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS interventions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                action TEXT NOT NULL,
                trigger_state TEXT NOT NULL,
                trigger_value REAL NOT NULL,
                confidence REAL NOT NULL,
                duration_seconds INTEGER NULL,
                escalation_level TEXT NOT NULL,
                reversible INTEGER NOT NULL,
                reason TEXT NOT NULL,
                contributors_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS diagnostics_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                backend_connected INTEGER NOT NULL,
                consent_active INTEGER NOT NULL,
                paused INTEGER NOT NULL,
                last_error TEXT NOT NULL,
                policy_version TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                agent_version TEXT NOT NULL,
                last_event_timestamp_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS brain_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                typing_burst_intensity REAL NOT NULL,
                scroll_velocity_index REAL NOT NULL,
                focus_switch_rate REAL NOT NULL,
                focus_strength REAL NOT NULL,
                overload_likelihood REAL NOT NULL,
                fatigue_probability REAL NOT NULL,
                cognitive_stability REAL NOT NULL,
                confidence REAL NOT NULL,
                last_action TEXT NOT NULL,
                contributors_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_input_buckets_ts ON input_buckets(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_state_buckets_ts ON state_buckets(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_interventions_ts ON interventions(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_diagnostics_ts ON diagnostics_snapshots(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_brain_snapshots_ts ON brain_snapshots(timestamp_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    private static List<string> DeserializeContributors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildDbPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "core-state.db");
    }

    private static string BuildExportRoot()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "exports");
    }
}
