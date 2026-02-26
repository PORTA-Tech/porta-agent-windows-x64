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

    public void WriteSystemChange(AgentSystemChange change)
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
                INSERT INTO system_changes (
                    timestamp_utc,
                    change_type,
                    previous_value_json,
                    new_value_json,
                    undo_info,
                    reason
                ) VALUES (
                    $ts, $change_type, $previous, $new, $undo, $reason
                );
                """;
            cmd.Parameters.AddWithValue("$ts", change.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$change_type", change.ChangeType);
            cmd.Parameters.AddWithValue("$previous", change.PreviousValueJson);
            cmd.Parameters.AddWithValue("$new", change.NewValueJson);
            cmd.Parameters.AddWithValue("$undo", change.UndoInfo);
            cmd.Parameters.AddWithValue("$reason", change.Reason);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, change.TimestampUtc);
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
                INSERT INTO diagnostics (
                    timestamp_utc,
                    last_error,
                    last_backend_ok_utc,
                    last_event_ts_utc,
                    backend_connected,
                    consent_active,
                    paused,
                    policy_version,
                    schema_version,
                    agent_version
                ) VALUES (
                    $ts, $last_error, $last_backend_ok, $last_event, $backend, $consent, $paused, $policy, $schema, $agent
                );

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
            cmd.Parameters.AddWithValue("$last_backend_ok", diagnostics.LastBackendOkUtc?.ToString("O") ?? string.Empty);
            cmd.Parameters.AddWithValue("$policy", diagnostics.PolicyVersion);
            cmd.Parameters.AddWithValue("$schema", diagnostics.SchemaVersion);
            cmd.Parameters.AddWithValue("$agent", diagnostics.AgentVersion);
            cmd.Parameters.AddWithValue("$last_event", diagnostics.LastEventTimestampUtc?.ToString("O") ?? string.Empty);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, diagnostics.TimestampUtc);
            tx.Commit();
        }
    }

    public void WriteConsent(AgentConsentRecord consent)
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
                INSERT INTO consent (
                    timestamp_utc,
                    active,
                    version,
                    disclosure_hash
                ) VALUES (
                    $ts, $active, $version, $hash
                );
                """;
            cmd.Parameters.AddWithValue("$ts", consent.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$active", consent.Active ? 1 : 0);
            cmd.Parameters.AddWithValue("$version", consent.Version);
            cmd.Parameters.AddWithValue("$hash", consent.DisclosureHash);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, consent.TimestampUtc);
            tx.Commit();
        }
    }

    public AgentConsentRecord? ReadLatestConsent()
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
                    active,
                    version,
                    disclosure_hash
                FROM consent
                ORDER BY id DESC
                LIMIT 1;
                """;

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AgentConsentRecord(
                TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                Active: reader.GetInt32(1) == 1,
                Version: reader.GetString(2),
                DisclosureHash: reader.GetString(3));
        }
    }

    public void WriteSettings(AgentSettingsRecord settings)
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
                INSERT INTO settings (
                    timestamp_utc,
                    privacy_level,
                    regulation_intensity,
                    disabled_categories_json,
                    notification_reduction_enabled,
                    focus_mode_enabled,
                    animation_reduction_enabled,
                    temporary_app_restriction_enabled,
                    baseline_recalibration_started_utc,
                    baseline_recalibration_until_utc
                ) VALUES (
                    $ts, $privacy, $intensity, $disabled, $notif, $focus, $anim, $restrict, $recal_start, $recal_until
                );
                """;
            cmd.Parameters.AddWithValue("$ts", settings.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$privacy", settings.PrivacyLevel);
            cmd.Parameters.AddWithValue("$intensity", settings.RegulationIntensity);
            cmd.Parameters.AddWithValue("$disabled", JsonSerializer.Serialize(settings.DisabledCategories));
            cmd.Parameters.AddWithValue("$notif", settings.NotificationReductionEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$focus", settings.FocusModeEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$anim", settings.AnimationReductionEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$restrict", settings.TemporaryAppRestrictionEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$recal_start", settings.BaselineRecalibrationStartedUtc?.ToString("O") ?? string.Empty);
            cmd.Parameters.AddWithValue("$recal_until", settings.BaselineRecalibrationUntilUtc?.ToString("O") ?? string.Empty);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, settings.TimestampUtc);
            tx.Commit();
        }
    }

    public AgentSettingsRecord? ReadLatestSettings()
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
                    privacy_level,
                    regulation_intensity,
                    disabled_categories_json,
                    notification_reduction_enabled,
                    focus_mode_enabled,
                    animation_reduction_enabled,
                    temporary_app_restriction_enabled,
                    baseline_recalibration_started_utc,
                    baseline_recalibration_until_utc
                FROM settings
                ORDER BY id DESC
                LIMIT 1;
                """;

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AgentSettingsRecord(
                TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                PrivacyLevel: reader.GetString(1),
                RegulationIntensity: reader.GetString(2),
                DisabledCategories: DeserializeStringList(reader.GetString(3)),
                NotificationReductionEnabled: reader.GetInt32(4) == 1,
                FocusModeEnabled: reader.GetInt32(5) == 1,
                AnimationReductionEnabled: reader.GetInt32(6) == 1,
                TemporaryAppRestrictionEnabled: reader.GetInt32(7) == 1,
                BaselineRecalibrationStartedUtc: ParseOptionalDateTimeOffset(reader.GetString(8)),
                BaselineRecalibrationUntilUtc: ParseOptionalDateTimeOffset(reader.GetString(9)));
        }
    }

    public void DeleteLocalData()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }
            }
            catch
            {
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            EnsureSchema();
            _lastAutoEventAction = null;
            _lastAutoEventAtUtc = DateTimeOffset.MinValue;
            _lastPruneUtc = DateTimeOffset.MinValue;
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
                    smoothed_typing_burst_intensity,
                    smoothed_scroll_velocity_index,
                    smoothed_focus_switch_rate,
                    focus_strength,
                    overload_likelihood,
                    fatigue_probability,
                    cognitive_stability,
                    dominant_state_name,
                    dominant_state_value,
                    confidence,
                    last_action,
                    activation_window_triggered,
                    cooldown_triggered,
                    activation_window_open,
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
                SmoothedTypingBurstIntensity: ReadDoubleOrFallback(reader, 4, fallback: reader.GetDouble(1)),
                SmoothedScrollVelocityIndex: ReadDoubleOrFallback(reader, 5, fallback: reader.GetDouble(2)),
                SmoothedFocusSwitchRate: ReadDoubleOrFallback(reader, 6, fallback: reader.GetDouble(3)),
                FocusStrength: reader.GetDouble(7),
                OverloadLikelihood: reader.GetDouble(8),
                FatigueProbability: reader.GetDouble(9),
                CognitiveStability: reader.GetDouble(10),
                DominantStateName: ReadStringOrFallback(reader, 11, ResolveDominantStateName(reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9))),
                DominantStateValue: ReadDoubleOrFallback(reader, 12, Math.Max(reader.GetDouble(7), Math.Max(reader.GetDouble(8), reader.GetDouble(9)))),
                Confidence: reader.GetDouble(13),
                LastAction: reader.GetString(14),
                ActivationWindowTriggered: ReadBoolOrFallback(reader, 15, false),
                CooldownTriggered: ReadBoolOrFallback(reader, 16, false),
                ActivationWindowOpen: ReadBoolOrFallback(reader, 17, false),
                Contributors: DeserializeContributors(reader.GetString(18)));
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
                    smoothed_typing_burst_intensity,
                    smoothed_scroll_velocity_index,
                    smoothed_focus_switch_rate,
                    focus_strength,
                    overload_likelihood,
                    fatigue_probability,
                    cognitive_stability,
                    dominant_state_name,
                    dominant_state_value,
                    confidence,
                    last_action,
                    activation_window_triggered,
                    cooldown_triggered,
                    activation_window_open,
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
                    SmoothedTypingBurstIntensity: ReadDoubleOrFallback(reader, 4, fallback: reader.GetDouble(1)),
                    SmoothedScrollVelocityIndex: ReadDoubleOrFallback(reader, 5, fallback: reader.GetDouble(2)),
                    SmoothedFocusSwitchRate: ReadDoubleOrFallback(reader, 6, fallback: reader.GetDouble(3)),
                    FocusStrength: reader.GetDouble(7),
                    OverloadLikelihood: reader.GetDouble(8),
                    FatigueProbability: reader.GetDouble(9),
                    CognitiveStability: reader.GetDouble(10),
                    DominantStateName: ReadStringOrFallback(reader, 11, ResolveDominantStateName(reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9))),
                    DominantStateValue: ReadDoubleOrFallback(reader, 12, Math.Max(reader.GetDouble(7), Math.Max(reader.GetDouble(8), reader.GetDouble(9)))),
                    Confidence: reader.GetDouble(13),
                    LastAction: reader.GetString(14),
                    ActivationWindowTriggered: ReadBoolOrFallback(reader, 15, false),
                    CooldownTriggered: ReadBoolOrFallback(reader, 16, false),
                    ActivationWindowOpen: ReadBoolOrFallback(reader, 17, false),
                    Contributors: DeserializeContributors(reader.GetString(18))));
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

    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null)
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
                    escalation,
                    reversible,
                    why,
                    contributors_json
                FROM events
                WHERE ($since = '' OR timestamp_utc >= $since)
                ORDER BY timestamp_utc DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", boundedLimit);
            cmd.Parameters.AddWithValue("$since", sinceUtc?.ToString("O") ?? string.Empty);

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

    public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000)
    {
        lock (_gate)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int boundedLimit = Math.Clamp(limit, 1, 10000);
            var rows = new List<AgentControlEvent>();

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
                    escalation,
                    reversible,
                    why,
                    contributors_json
                FROM events
                WHERE timestamp_utc >= $from AND timestamp_utc <= $to
                ORDER BY timestamp_utc ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
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

    public AgentStorageInfo ReadStorageInfo()
    {
        lock (_gate)
        {
            long dbBytes = 0;
            try
            {
                if (File.Exists(_dbPath))
                {
                    dbBytes = new FileInfo(_dbPath).Length;
                }
            }
            catch
            {
            }

            return new AgentStorageInfo(
                RetentionDays: DefaultRetentionDays,
                DatabaseBytes: dbBytes);
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
                SELECT COUNT(1) FROM events
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

    public string ExportBundle(
        string logFilePath,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths)
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

            if (File.Exists(settingsPath))
            {
                File.Copy(settingsPath, Path.Combine(exportDir, "effective-settings-v1.json"), overwrite: true);
            }

            string configExportDir = Path.Combine(exportDir, "config");
            Directory.CreateDirectory(configExportDir);
            var exportedConfigNames = new List<string>();
            foreach (string configPath in configPaths)
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                string safeName = Path.GetFileName(configPath);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                string destination = Path.Combine(configExportDir, safeName);
                File.Copy(configPath, destination, overwrite: true);
                exportedConfigNames.Add(safeName);
            }

            string versionsJson = JsonSerializer.Serialize(versions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(exportDir, "versions.json"), versionsJson);

            string metadata = JsonSerializer.Serialize(new
            {
                generated_at_utc = DateTimeOffset.UtcNow,
                schema = "v2",
                db_path = _dbPath,
                retention_days = DefaultRetentionDays,
                config_files = exportedConfigNames
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
                typing_burst_intensity_raw,
                scroll_velocity_index_raw,
                focus_switch_rate_raw,
                typing_burst_intensity_smoothed,
                scroll_velocity_index_smoothed,
                focus_switch_rate_smoothed,
                focus_strength,
                overload_likelihood,
                fatigue_probability,
                cognitive_stability,
                dominant_state_name,
                dominant_state_value,
                confidence,
                activation_window_triggered,
                cooldown_triggered,
                activation_window_open,
                contributors_json
            ) VALUES ($ts, $typing_raw, $scroll_raw, $focus_switch_raw, $typing_smooth, $scroll_smooth, $focus_switch_smooth, $focus, $overload, $fatigue, $stability, $dominant_name, $dominant_value, $confidence, $activation_triggered, $cooldown_triggered, $activation_open, $contributors);
            """;
        cmd.Parameters.AddWithValue("$ts", snapshot.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$typing_raw", snapshot.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll_raw", snapshot.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch_raw", snapshot.FocusSwitchRate);
        cmd.Parameters.AddWithValue("$typing_smooth", snapshot.SmoothedTypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll_smooth", snapshot.SmoothedScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch_smooth", snapshot.SmoothedFocusSwitchRate);
        cmd.Parameters.AddWithValue("$focus", snapshot.FocusStrength);
        cmd.Parameters.AddWithValue("$overload", snapshot.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$fatigue", snapshot.FatigueProbability);
        cmd.Parameters.AddWithValue("$stability", snapshot.CognitiveStability);
        cmd.Parameters.AddWithValue("$dominant_name", snapshot.DominantStateName);
        cmd.Parameters.AddWithValue("$dominant_value", snapshot.DominantStateValue);
        cmd.Parameters.AddWithValue("$confidence", snapshot.Confidence);
        cmd.Parameters.AddWithValue("$activation_triggered", snapshot.ActivationWindowTriggered ? 1 : 0);
        cmd.Parameters.AddWithValue("$cooldown_triggered", snapshot.CooldownTriggered ? 1 : 0);
        cmd.Parameters.AddWithValue("$activation_open", snapshot.ActivationWindowOpen ? 1 : 0);
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
                smoothed_typing_burst_intensity,
                smoothed_scroll_velocity_index,
                smoothed_focus_switch_rate,
                focus_strength,
                overload_likelihood,
                fatigue_probability,
                cognitive_stability,
                dominant_state_name,
                dominant_state_value,
                confidence,
                last_action,
                activation_window_triggered,
                cooldown_triggered,
                activation_window_open,
                contributors_json
            ) VALUES (
                $ts, $typing, $scroll, $focus_switch, $typing_smooth, $scroll_smooth, $focus_switch_smooth,
                $focus_strength, $overload, $fatigue, $stability, $dominant_name, $dominant_value,
                $confidence, $last_action, $activation_triggered, $cooldown_triggered, $activation_open, $contributors
            );
            """;
        cmd.Parameters.AddWithValue("$ts", snapshot.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$typing", snapshot.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll", snapshot.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch", snapshot.FocusSwitchRate);
        cmd.Parameters.AddWithValue("$typing_smooth", snapshot.SmoothedTypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll_smooth", snapshot.SmoothedScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch_smooth", snapshot.SmoothedFocusSwitchRate);
        cmd.Parameters.AddWithValue("$focus_strength", snapshot.FocusStrength);
        cmd.Parameters.AddWithValue("$overload", snapshot.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$fatigue", snapshot.FatigueProbability);
        cmd.Parameters.AddWithValue("$stability", snapshot.CognitiveStability);
        cmd.Parameters.AddWithValue("$dominant_name", snapshot.DominantStateName);
        cmd.Parameters.AddWithValue("$dominant_value", snapshot.DominantStateValue);
        cmd.Parameters.AddWithValue("$confidence", snapshot.Confidence);
        cmd.Parameters.AddWithValue("$last_action", snapshot.LastAction);
        cmd.Parameters.AddWithValue("$activation_triggered", snapshot.ActivationWindowTriggered ? 1 : 0);
        cmd.Parameters.AddWithValue("$cooldown_triggered", snapshot.CooldownTriggered ? 1 : 0);
        cmd.Parameters.AddWithValue("$activation_open", snapshot.ActivationWindowOpen ? 1 : 0);
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
            INSERT INTO events (
                timestamp_utc,
                action,
                duration_seconds,
                why,
                escalation,
                reversible,
                trigger_state,
                trigger_value,
                confidence,
                contributors_json
            ) VALUES (
                $ts, $action, $duration, $reason, $escalation, $reversible, $trigger_state, $trigger_value, $confidence, $contributors
            );

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
            DELETE FROM events WHERE timestamp_utc < $cutoff;
            DELETE FROM system_changes WHERE timestamp_utc < $cutoff;
            DELETE FROM diagnostics WHERE timestamp_utc < $cutoff;
            DELETE FROM consent WHERE timestamp_utc < $cutoff;
            DELETE FROM settings WHERE timestamp_utc < $cutoff;
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
                typing_burst_intensity_raw REAL NOT NULL DEFAULT 0,
                scroll_velocity_index_raw REAL NOT NULL DEFAULT 0,
                focus_switch_rate_raw REAL NOT NULL DEFAULT 0,
                typing_burst_intensity_smoothed REAL NOT NULL DEFAULT 0,
                scroll_velocity_index_smoothed REAL NOT NULL DEFAULT 0,
                focus_switch_rate_smoothed REAL NOT NULL DEFAULT 0,
                focus_strength REAL NOT NULL,
                overload_likelihood REAL NOT NULL,
                fatigue_probability REAL NOT NULL,
                cognitive_stability REAL NOT NULL,
                dominant_state_name TEXT NOT NULL DEFAULT 'focus',
                dominant_state_value REAL NOT NULL DEFAULT 0,
                confidence REAL NOT NULL,
                activation_window_triggered INTEGER NOT NULL DEFAULT 0,
                cooldown_triggered INTEGER NOT NULL DEFAULT 0,
                activation_window_open INTEGER NOT NULL DEFAULT 0,
                contributors_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                action TEXT NOT NULL,
                duration_seconds INTEGER NULL,
                why TEXT NOT NULL,
                escalation TEXT NOT NULL,
                reversible INTEGER NOT NULL,
                trigger_state TEXT NOT NULL,
                trigger_value REAL NOT NULL,
                confidence REAL NOT NULL,
                contributors_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS system_changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                change_type TEXT NOT NULL,
                previous_value_json TEXT NOT NULL,
                new_value_json TEXT NOT NULL,
                undo_info TEXT NOT NULL,
                reason TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS diagnostics (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                last_error TEXT NOT NULL,
                last_backend_ok_utc TEXT NOT NULL,
                last_event_ts_utc TEXT NOT NULL,
                backend_connected INTEGER NOT NULL,
                consent_active INTEGER NOT NULL,
                paused INTEGER NOT NULL,
                policy_version TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                agent_version TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS consent (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                active INTEGER NOT NULL,
                version TEXT NOT NULL,
                disclosure_hash TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                privacy_level TEXT NOT NULL,
                regulation_intensity TEXT NOT NULL,
                disabled_categories_json TEXT NOT NULL,
                notification_reduction_enabled INTEGER NOT NULL,
                focus_mode_enabled INTEGER NOT NULL,
                animation_reduction_enabled INTEGER NOT NULL,
                temporary_app_restriction_enabled INTEGER NOT NULL,
                baseline_recalibration_started_utc TEXT NOT NULL,
                baseline_recalibration_until_utc TEXT NOT NULL
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
                smoothed_typing_burst_intensity REAL NOT NULL DEFAULT 0,
                smoothed_scroll_velocity_index REAL NOT NULL DEFAULT 0,
                smoothed_focus_switch_rate REAL NOT NULL DEFAULT 0,
                focus_strength REAL NOT NULL,
                overload_likelihood REAL NOT NULL,
                fatigue_probability REAL NOT NULL,
                cognitive_stability REAL NOT NULL,
                dominant_state_name TEXT NOT NULL DEFAULT 'focus',
                dominant_state_value REAL NOT NULL DEFAULT 0,
                confidence REAL NOT NULL,
                last_action TEXT NOT NULL,
                activation_window_triggered INTEGER NOT NULL DEFAULT 0,
                cooldown_triggered INTEGER NOT NULL DEFAULT 0,
                activation_window_open INTEGER NOT NULL DEFAULT 0,
                contributors_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_input_buckets_ts ON input_buckets(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_state_buckets_ts ON state_buckets(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_events_ts ON events(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_system_changes_ts ON system_changes(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_diagnostics_new_ts ON diagnostics(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_consent_ts ON consent(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_settings_ts ON settings(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_interventions_ts ON interventions(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_diagnostics_ts ON diagnostics_snapshots(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_brain_snapshots_ts ON brain_snapshots(timestamp_utc);
            """;
        cmd.ExecuteNonQuery();

        EnsureColumnExists(connection, "state_buckets", "typing_burst_intensity_raw", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "scroll_velocity_index_raw", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "focus_switch_rate_raw", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "typing_burst_intensity_smoothed", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "scroll_velocity_index_smoothed", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "focus_switch_rate_smoothed", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "dominant_state_name", "TEXT NOT NULL DEFAULT 'focus'");
        EnsureColumnExists(connection, "state_buckets", "dominant_state_value", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "activation_window_triggered", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "cooldown_triggered", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "activation_window_open", "INTEGER NOT NULL DEFAULT 0");

        EnsureColumnExists(connection, "brain_snapshots", "smoothed_typing_burst_intensity", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "brain_snapshots", "smoothed_scroll_velocity_index", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "brain_snapshots", "smoothed_focus_switch_rate", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "brain_snapshots", "dominant_state_name", "TEXT NOT NULL DEFAULT 'focus'");
        EnsureColumnExists(connection, "brain_snapshots", "dominant_state_value", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "brain_snapshots", "activation_window_triggered", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "brain_snapshots", "cooldown_triggered", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "brain_snapshots", "activation_window_open", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumnExists(SqliteConnection connection, string table, string column, string definition)
    {
        using var existsCmd = connection.CreateCommand();
        existsCmd.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = existsCmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alterCmd.ExecuteNonQuery();
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

    private static List<string> DeserializeStringList(string? raw)
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

    private static double ReadDoubleOrFallback(SqliteDataReader reader, int ordinal, double fallback)
    {
        try
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetDouble(ordinal);
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string ReadStringOrFallback(SqliteDataReader reader, int ordinal, string fallback)
    {
        try
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetString(ordinal);
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static bool ReadBoolOrFallback(SqliteDataReader reader, int ordinal, bool fallback)
    {
        try
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetInt32(ordinal) == 1;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string ResolveDominantStateName(double focusStrength, double overloadLikelihood, double fatigueProbability)
    {
        double dominant = Math.Max(focusStrength, Math.Max(overloadLikelihood, fatigueProbability));
        if (dominant == focusStrength)
        {
            return "focus";
        }

        if (dominant == overloadLikelihood)
        {
            return "overload";
        }

        return "fatigue";
    }

    private static DateTimeOffset? ParseOptionalDateTimeOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
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
