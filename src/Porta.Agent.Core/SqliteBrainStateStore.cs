using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Core;

public sealed class SqliteBrainStateStore : IBrainStateStore, IBucketFinalizerDbWriter
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
    private ActiveInterventionState? _activeIntervention;

    private sealed record ActiveInterventionState(
        string InterventionId,
        string Action,
        string EscalationLevel,
        string PolicyId,
        string Domain,
        DateTimeOffset StartTimestampUtc,
        DateTimeOffset? PlannedEndTimestampUtc,
        DateTimeOffset? CooldownEndTimestampUtc);

    public SqliteBrainStateStore()
    {
        _dbPath = DbPaths.GetCoreStateDbPath();
        _connectionString = DbOpen.BuildConnectionString(_dbPath);
        EnsureSchema();
    }

    public void WriteSnapshot(AgentBrainSnapshot snapshot)
    {
        if (!BucketFinalizerWriteGuard.IsActive)
        {
            throw new InvalidOperationException(
                "Direct WriteSnapshot is blocked. Route snapshot writes through BucketFinalizerStateStore/BucketFinalizer.");
        }

        WriteSnapshotCore(snapshot, inputBucketRow: null);
    }

    void IBucketFinalizerDbWriter.WriteFinalizedBucketSnapshot(AgentBrainSnapshot snapshot, InputBucketRow inputBucketRow)
    {
        using IDisposable _ = BucketFinalizerWriteGuard.Enter();
        WriteSnapshotCore(snapshot, inputBucketRow);
    }

    private void WriteSnapshotCore(AgentBrainSnapshot snapshot, InputBucketRow? inputBucketRow)
    {
        snapshot = BucketPrivacyContract.SanitizeForBucketPersistence(snapshot);
        bool isBucketFinalizeWrite = inputBucketRow.HasValue;
        InputBucketRow mapped = inputBucketRow ?? BuildFallbackInputBucketRow(snapshot);
        long bucketSeq = mapped.BucketSeq;

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                // Finalize contract write order:
                // 1) input_buckets UPSERT
                // 2) baseline gating/update decision (same transaction as bucket finalize)
                // 3) primitive_buckets UPSERT
                // 4) state_buckets UPSERT
                // 5) runtime_state updates
                // Any failure in this block rolls back the entire bucket finalize (fail-closed determinism).
                UpsertInputBucket(connection, tx, mapped);
                BaselineGatingResult gating = BaselineGatingResult.Warm;
                if (isBucketFinalizeWrite)
                {
                    // Baseline updates are finalize-only to keep deterministic finalized-bucket semantics.
                    gating = BaselineManager.UpdateForFinalizedBucket(connection, tx, mapped);
                }
                AgentBrainSnapshot gatedSnapshot = ApplyBaselineGating(snapshot, gating);
                PrimitiveBucketWriteModel primitiveRow = BuildPrimitiveBucketRow(mapped, gatedSnapshot);
                StateBucketWriteModel stateRow = BuildStateBucketRow(mapped, gatedSnapshot);
                UpsertPrimitiveBucket(connection, tx, primitiveRow);
                UpsertStateBucket(connection, tx, stateRow);
                InsertLegacySnapshot(connection, tx, gatedSnapshot);
                TryInsertAutomaticInterventionEvent(connection, tx, gatedSnapshot);
                UpsertRuntimeStateForSnapshot(connection, tx, gatedSnapshot, mapped, gating);
                PruneIfNeeded(connection, tx, gatedSnapshot.TimestampUtc);
                tx.Commit();
            }
            catch (Exception ex)
            {
                TryRollback(tx);
                IncrementDiagnosticsCounter(connection, "bucket_txn_commit_failed_count");
                Console.WriteLine(
                    $"bucket_txn_failed bucket_seq={bucketSeq} state=pending_finalization error={ex.GetType().Name}:{ex.Message}");
                throw;
            }
        }
    }

    public void WriteControlEvent(AgentControlEvent controlEvent)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
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
            using var connection = DbOpen.OpenConnection(_connectionString);
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
            using var connection = DbOpen.OpenConnection(_connectionString);
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

    public void WriteConsent(AgentConsentRecord consent)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
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
            using var connection = DbOpen.OpenConnection(_connectionString);
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
            using var connection = DbOpen.OpenConnection(_connectionString);
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
            using var connection = DbOpen.OpenConnection(_connectionString);
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
                using var connection = DbOpen.OpenConnection(_connectionString);
                using SqliteTransaction tx = connection.BeginTransaction();

                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    DELETE FROM input_buckets;
                    DELETE FROM state_buckets;
                    DELETE FROM events;
                    DELETE FROM interventions;
                    DELETE FROM system_changes;
                    DELETE FROM diagnostics;
                    DELETE FROM diagnostics_snapshots;
                    DELETE FROM consent;
                    DELETE FROM settings;
                    DELETE FROM brain_snapshots;
                    DELETE FROM sqlite_sequence;
                    """;
                cmd.ExecuteNonQuery();
                tx.Commit();

                using var checkpointCmd = connection.CreateCommand();
                checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpointCmd.ExecuteNonQuery();

                using var vacuumCmd = connection.CreateCommand();
                vacuumCmd.CommandText = "VACUUM;";
                vacuumCmd.ExecuteNonQuery();
            }
            catch
            {
                // Fallback: if SQL-level purge fails, attempt file reset.
                TryDeleteDbFiles();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            EnsureSchema();
            _lastAutoEventAction = null;
            _lastAutoEventAtUtc = DateTimeOffset.MinValue;
            _activeIntervention = null;
            _lastPruneUtc = DateTimeOffset.MinValue;
        }
    }

    public AgentBrainSnapshot? ReadLatestSnapshot()
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
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
                    contributors_json,
                    policy_id,
                    domain,
                    escalation_level,
                    planned_end_timestamp_utc,
                    actual_end_timestamp_utc,
                    cooldown_end_timestamp_utc
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
                Contributors: DeserializeContributors(reader.GetString(18)),
                PolicyId: ReadStringOrFallback(reader, 19, "n/a"),
                Domain: ReadStringOrFallback(reader, 20, "control"),
                EscalationLevel: ReadStringOrFallback(reader, 21, "Inform"),
                PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 22, string.Empty)),
                ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 23, string.Empty)),
                CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 24, string.Empty)));
        }
    }

    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds)
    {
        lock (_gate)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            _ = bucketSeconds; // Bucket size is pinned in v1.
            int bucket = Time.bucketSeconds;
            var rawRows = new List<AgentBrainSnapshot>();

            using var connection = DbOpen.OpenConnection(_connectionString);
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
                    contributors_json,
                    policy_id,
                    domain,
                    escalation_level,
                    planned_end_timestamp_utc,
                    actual_end_timestamp_utc,
                    cooldown_end_timestamp_utc
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
                    Contributors: DeserializeContributors(reader.GetString(18)),
                    PolicyId: ReadStringOrFallback(reader, 19, "n/a"),
                    Domain: ReadStringOrFallback(reader, 20, "control"),
                    EscalationLevel: ReadStringOrFallback(reader, 21, "Inform"),
                    PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 22, string.Empty)),
                    ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 23, string.Empty)),
                    CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 24, string.Empty))));
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

            using var connection = DbOpen.OpenConnection(_connectionString);
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
                    contributors_json,
                    intervention_id,
                    policy_id,
                    domain,
                    planned_end_timestamp_utc,
                    actual_end_timestamp_utc,
                    previous_system_state_json,
                    what_changed,
                    how_to_undo,
                    reversal_applied,
                    cooldown_end_timestamp_utc,
                    lifecycle_state
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
                    Contributors: DeserializeContributors(reader.GetString(9)),
                    InterventionId: ReadStringOrFallback(reader, 10, string.Empty),
                    PolicyId: ReadStringOrFallback(reader, 11, "n/a"),
                    Domain: ReadStringOrFallback(reader, 12, "control"),
                    PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 13, string.Empty)),
                    ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 14, string.Empty)),
                    PreviousSystemStateJson: ReadStringOrFallback(reader, 15, "{}"),
                    WhatChanged: ReadStringOrFallback(reader, 16, string.Empty),
                    HowToUndo: ReadStringOrFallback(reader, 17, string.Empty),
                    ReversalApplied: ReadBoolOrFallback(reader, 18, false),
                    CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 19, string.Empty)),
                    LifecycleState: ReadStringOrFallback(reader, 20, "activated")));
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

            using var connection = DbOpen.OpenConnection(_connectionString);
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
                    contributors_json,
                    intervention_id,
                    policy_id,
                    domain,
                    planned_end_timestamp_utc,
                    actual_end_timestamp_utc,
                    previous_system_state_json,
                    what_changed,
                    how_to_undo,
                    reversal_applied,
                    cooldown_end_timestamp_utc,
                    lifecycle_state
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
                    Contributors: DeserializeContributors(reader.GetString(9)),
                    InterventionId: ReadStringOrFallback(reader, 10, string.Empty),
                    PolicyId: ReadStringOrFallback(reader, 11, "n/a"),
                    Domain: ReadStringOrFallback(reader, 12, "control"),
                    PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 13, string.Empty)),
                    ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 14, string.Empty)),
                    PreviousSystemStateJson: ReadStringOrFallback(reader, 15, "{}"),
                    WhatChanged: ReadStringOrFallback(reader, 16, string.Empty),
                    HowToUndo: ReadStringOrFallback(reader, 17, string.Empty),
                    ReversalApplied: ReadBoolOrFallback(reader, 18, false),
                    CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 19, string.Empty)),
                    LifecycleState: ReadStringOrFallback(reader, 20, "activated")));
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

            using var connection = DbOpen.OpenConnection(_connectionString);

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
        IReadOnlyList<string> logFilePaths,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths,
        IReadOnlyDictionary<string, string> supplementalJsonFiles)
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

            string logsDir = Path.Combine(exportDir, "logs");
            Directory.CreateDirectory(logsDir);
            foreach (string logFilePath in logFilePaths)
            {
                string safeName = Path.GetFileName(logFilePath);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                string destination = Path.Combine(logsDir, safeName);
                if (!File.Exists(logFilePath))
                {
                    File.WriteAllText(destination, string.Empty);
                    continue;
                }

                File.Copy(logFilePath, destination, overwrite: true);
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

            foreach ((string fileName, string json) in supplementalJsonFiles)
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                string safeName = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                File.WriteAllText(Path.Combine(exportDir, safeName), json ?? "{}");
            }

            string metadata = JsonSerializer.Serialize(new
            {
                generated_at_utc = DateTimeOffset.UtcNow,
                schema = "v2",
                db_path = _dbPath,
                retention_days = DefaultRetentionDays,
                config_files = exportedConfigNames,
                included_log_files = logFilePaths
                    .Where(File.Exists)
                    .Select(Path.GetFileName)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .ToArray(),
                supplemental_json_files = supplementalJsonFiles.Keys
                    .Select(Path.GetFileName)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .ToArray()
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

    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection)
    {
        // deferred: false starts the write transaction immediately (BEGIN IMMEDIATE semantics).
        return connection.BeginTransaction(deferred: false);
    }

    private static void TryRollback(SqliteTransaction tx)
    {
        try
        {
            tx.Rollback();
        }
        catch
        {
        }
    }

    private static void IncrementDiagnosticsCounter(SqliteConnection connection, string key)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO diagnostics (key, value, updated_utc)
                VALUES ($key, '1', $updated_utc)
                ON CONFLICT(key) DO UPDATE SET
                    value = CASE
                        WHEN diagnostics.value GLOB '-?[0-9]*' THEN CAST(diagnostics.value AS INTEGER) + 1
                        ELSE 1
                    END,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }

    private static void UpsertRuntimeStateForSnapshot(
        SqliteConnection connection,
        SqliteTransaction tx,
        AgentBrainSnapshot snapshot,
        InputBucketRow mapped,
        BaselineGatingResult gating)
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            bucket_seq = mapped.BucketSeq,
            start_utc = mapped.StartUtc,
            end_utc = Time.toIsoUtcMsString(Time.bucketStartEnd(mapped.BucketSeq).endUtcMs),
            timestamp_utc = mapped.TimestampUtc,
            overload_likelihood = snapshot.OverloadLikelihood,
            confidence = snapshot.Confidence,
            baseline_ready = gating.BaselineReady,
            cold_start = gating.ColdStart,
            confidence_cap = gating.ConfidenceCap,
            policies_enabled = gating.PoliciesEnabled,
            cooldown_end_timestamp_utc = snapshot.CooldownEndTimestampUtc?.ToString("O") ?? string.Empty
        });

        string finalizedSeqValueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            bucket_seq = mapped.BucketSeq,
            updated_utc = mapped.TimestampUtc
        });

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, $updated_utc)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", "bucket_finalizer:last_snapshot");
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.Parameters.AddWithValue("$updated_utc", mapped.TimestampUtc);
        cmd.ExecuteNonQuery();

        using var cmd2 = connection.CreateCommand();
        cmd2.Transaction = tx;
        cmd2.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, $updated_utc)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_utc = excluded.updated_utc;
            """;
        cmd2.Parameters.AddWithValue("$key", "bucket_finalizer:last_finalized_bucket_seq");
        cmd2.Parameters.AddWithValue("$value_json", finalizedSeqValueJson);
        cmd2.Parameters.AddWithValue("$updated_utc", mapped.TimestampUtc);
        cmd2.ExecuteNonQuery();
    }

    private static AgentBrainSnapshot ApplyBaselineGating(AgentBrainSnapshot snapshot, BaselineGatingResult gating)
    {
        double cappedConfidence = Math.Clamp(Math.Min(snapshot.Confidence, gating.ConfidenceCap), 0d, 1d);
        AgentBrainSnapshot gated = snapshot with
        {
            Confidence = cappedConfidence
        };

        if (gating.PoliciesEnabled)
        {
            return gated;
        }

        if (!IsPolicyDrivenAction(gated.LastAction))
        {
            return gated with
            {
                PolicyId = "cold-start-gate-v1",
                Domain = "control",
                EscalationLevel = "Inform",
                PlannedEndTimestampUtc = null,
                CooldownEndTimestampUtc = null
            };
        }

        IReadOnlyList<string> contributors = gated.Contributors
            .Concat(["cold_start_policy_suppressed"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return gated with
        {
            LastAction = "monitor_only",
            ActivationWindowTriggered = false,
            CooldownTriggered = false,
            ActivationWindowOpen = false,
            Contributors = contributors,
            PolicyId = "cold-start-gate-v1",
            Domain = "control",
            EscalationLevel = "Inform",
            PlannedEndTimestampUtc = null,
            CooldownEndTimestampUtc = null
        };
    }

    private static InputBucketRow BuildFallbackInputBucketRow(AgentBrainSnapshot snapshot)
    {
        long bucketSeq = Time.bucketSeqFromEventTs(snapshot.TimestampUtc.ToUnixTimeMilliseconds());
        (long startUtcMs, _) = Time.bucketStartEnd(bucketSeq);
        string startUtc = Time.toIsoUtcMsString(startUtcMs);
        string contributorsJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            keyboard = new { keys = 0, backspace = 0 },
            mouse = new { moves = 0, scroll = 0 },
            focus = new { switches = 0 },
            idle = new { idle_s = 0d }
        });

        return new InputBucketRow(
            BucketSeq: bucketSeq,
            TimestampUtc: startUtc,
            StartUtc: startUtc,
            TypingBurstIntensity: Math.Clamp(snapshot.TypingBurstIntensity, 0d, 1d),
            ScrollVelocityIndex: Math.Clamp(snapshot.ScrollVelocityIndex, 0d, 1d),
            FocusSwitchRate: Math.Clamp(snapshot.FocusSwitchRate, 0d, 1d),
            KeyboardIkiMean: 0d,
            KeyboardIkiCv: 0d,
            KeyboardBurstRate: 0d,
            KeyboardBackspaceRate: 0d,
            MouseSpeedMean: 0d,
            MouseJerkP95: 0d,
            MouseScrollSpeed: 0d,
            MouseMicroCorrectionRate: 0d,
            FocusAppEntropy: 0d,
            FocusActiveAppCount: 0d,
            IdleSeconds: 0d,
            IdleMicroIdleCount: 0d,
            QualityScore: 1d,
            Partial: 0,
            ContributorsJson: contributorsJson);
    }

    private static PrimitiveBucketWriteModel BuildPrimitiveBucketRow(InputBucketRow mapped, AgentBrainSnapshot snapshot)
    {
        (long startUtcMs, long endUtcMs) = Time.bucketStartEnd(mapped.BucketSeq);
        string primitivesJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            typing_burst_intensity_raw = mapped.TypingBurstIntensity,
            scroll_velocity_index_raw = mapped.ScrollVelocityIndex,
            focus_switch_rate_raw = mapped.FocusSwitchRate,
            typing_burst_intensity_smoothed = snapshot.SmoothedTypingBurstIntensity,
            scroll_velocity_index_smoothed = snapshot.SmoothedScrollVelocityIndex,
            focus_switch_rate_smoothed = snapshot.SmoothedFocusSwitchRate
        });

        return new PrimitiveBucketWriteModel(
            BucketSeq: mapped.BucketSeq,
            StartUtc: Time.toIsoUtcMsString(startUtcMs),
            EndUtc: Time.toIsoUtcMsString(endUtcMs),
            PrimitivesJson: primitivesJson,
            ContributorsJson: mapped.ContributorsJson,
            Quality: mapped.QualityScore,
            Partial: mapped.Partial);
    }

    private static StateBucketWriteModel BuildStateBucketRow(InputBucketRow mapped, AgentBrainSnapshot snapshot)
    {
        string contributorsJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contributors = snapshot.Contributors
        });

        return new StateBucketWriteModel(
            TimestampUtc: mapped.StartUtc,
            TypingRaw: mapped.TypingBurstIntensity,
            ScrollRaw: mapped.ScrollVelocityIndex,
            FocusRaw: mapped.FocusSwitchRate,
            TypingSmoothed: snapshot.SmoothedTypingBurstIntensity,
            ScrollSmoothed: snapshot.SmoothedScrollVelocityIndex,
            FocusSmoothed: snapshot.SmoothedFocusSwitchRate,
            FocusStrength: snapshot.FocusStrength,
            OverloadLikelihood: snapshot.OverloadLikelihood,
            FatigueProbability: snapshot.FatigueProbability,
            CognitiveStability: snapshot.CognitiveStability,
            DominantStateName: snapshot.DominantStateName,
            DominantStateValue: snapshot.DominantStateValue,
            Confidence: snapshot.Confidence,
            ActivationWindowTriggered: snapshot.ActivationWindowTriggered ? 1 : 0,
            CooldownTriggered: snapshot.CooldownTriggered ? 1 : 0,
            ActivationWindowOpen: snapshot.ActivationWindowOpen ? 1 : 0,
            ContributorsJson: contributorsJson);
    }

    private void UpsertInputBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow mappedRow)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO input_buckets (
                timestamp_utc,
                start_utc,
                bucket_seq,
                typing_burst_intensity,
                scroll_velocity_index,
                focus_switch_rate,
                keyboard_iki_mean,
                keyboard_iki_cv,
                keyboard_burst_rate,
                keyboard_backspace_rate,
                mouse_speed_mean,
                mouse_jerk_p95,
                mouse_scroll_speed,
                mouse_micro_correction_rate,
                focus_app_entropy,
                focus_active_app_count,
                idle_seconds,
                idle_micro_idle_count,
                quality_score,
                partial,
                contributors_json
            ) VALUES (
                $ts, $start_utc, $bucket_seq, $typing, $scroll, $focus_switch,
                $keyboard_iki_mean, $keyboard_iki_cv, $keyboard_burst_rate, $keyboard_backspace_rate,
                $mouse_speed_mean, $mouse_jerk_p95, $mouse_scroll_speed, $mouse_micro_correction_rate,
                $focus_app_entropy, $focus_active_app_count,
                $idle_seconds, $idle_micro_idle_count,
                $quality_score, $partial, $contributors_json
            )
            ON CONFLICT(start_utc) DO UPDATE SET
                timestamp_utc = excluded.timestamp_utc,
                bucket_seq = excluded.bucket_seq,
                typing_burst_intensity = excluded.typing_burst_intensity,
                scroll_velocity_index = excluded.scroll_velocity_index,
                focus_switch_rate = excluded.focus_switch_rate,
                keyboard_iki_mean = excluded.keyboard_iki_mean,
                keyboard_iki_cv = excluded.keyboard_iki_cv,
                keyboard_burst_rate = excluded.keyboard_burst_rate,
                keyboard_backspace_rate = excluded.keyboard_backspace_rate,
                mouse_speed_mean = excluded.mouse_speed_mean,
                mouse_jerk_p95 = excluded.mouse_jerk_p95,
                mouse_scroll_speed = excluded.mouse_scroll_speed,
                mouse_micro_correction_rate = excluded.mouse_micro_correction_rate,
                focus_app_entropy = excluded.focus_app_entropy,
                focus_active_app_count = excluded.focus_active_app_count,
                idle_seconds = excluded.idle_seconds,
                idle_micro_idle_count = excluded.idle_micro_idle_count,
                quality_score = excluded.quality_score,
                partial = excluded.partial,
                contributors_json = excluded.contributors_json;
            """;
        cmd.Parameters.AddWithValue("$ts", mappedRow.TimestampUtc);
        cmd.Parameters.AddWithValue("$start_utc", mappedRow.StartUtc);
        cmd.Parameters.AddWithValue("$bucket_seq", mappedRow.BucketSeq);
        cmd.Parameters.AddWithValue("$typing", mappedRow.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll", mappedRow.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch", mappedRow.FocusSwitchRate);
        cmd.Parameters.AddWithValue("$keyboard_iki_mean", mappedRow.KeyboardIkiMean);
        cmd.Parameters.AddWithValue("$keyboard_iki_cv", mappedRow.KeyboardIkiCv);
        cmd.Parameters.AddWithValue("$keyboard_burst_rate", mappedRow.KeyboardBurstRate);
        cmd.Parameters.AddWithValue("$keyboard_backspace_rate", mappedRow.KeyboardBackspaceRate);
        cmd.Parameters.AddWithValue("$mouse_speed_mean", mappedRow.MouseSpeedMean);
        cmd.Parameters.AddWithValue("$mouse_jerk_p95", mappedRow.MouseJerkP95);
        cmd.Parameters.AddWithValue("$mouse_scroll_speed", mappedRow.MouseScrollSpeed);
        cmd.Parameters.AddWithValue("$mouse_micro_correction_rate", mappedRow.MouseMicroCorrectionRate);
        cmd.Parameters.AddWithValue("$focus_app_entropy", mappedRow.FocusAppEntropy);
        cmd.Parameters.AddWithValue("$focus_active_app_count", mappedRow.FocusActiveAppCount);
        cmd.Parameters.AddWithValue("$idle_seconds", mappedRow.IdleSeconds);
        cmd.Parameters.AddWithValue("$idle_micro_idle_count", mappedRow.IdleMicroIdleCount);
        cmd.Parameters.AddWithValue("$quality_score", mappedRow.QualityScore);
        cmd.Parameters.AddWithValue("$partial", mappedRow.Partial);
        cmd.Parameters.AddWithValue("$contributors_json", mappedRow.ContributorsJson);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertPrimitiveBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        PrimitiveBucketWriteModel row)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO primitive_buckets (
                bucket_seq,
                start_utc,
                end_utc,
                primitives_json,
                contributors_json,
                quality,
                partial
            ) VALUES (
                $bucket_seq, $start_utc, $end_utc, $primitives_json, $contributors_json, $quality, $partial
            )
            ON CONFLICT(bucket_seq) DO UPDATE SET
                start_utc = excluded.start_utc,
                end_utc = excluded.end_utc,
                primitives_json = excluded.primitives_json,
                contributors_json = excluded.contributors_json,
                quality = excluded.quality,
                partial = excluded.partial;
            """;
        cmd.Parameters.AddWithValue("$bucket_seq", row.BucketSeq);
        cmd.Parameters.AddWithValue("$start_utc", row.StartUtc);
        cmd.Parameters.AddWithValue("$end_utc", row.EndUtc);
        cmd.Parameters.AddWithValue("$primitives_json", row.PrimitivesJson);
        cmd.Parameters.AddWithValue("$contributors_json", row.ContributorsJson);
        cmd.Parameters.AddWithValue("$quality", row.Quality);
        cmd.Parameters.AddWithValue("$partial", row.Partial);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertStateBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        StateBucketWriteModel row)
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
            ) VALUES ($ts, $typing_raw, $scroll_raw, $focus_switch_raw, $typing_smooth, $scroll_smooth, $focus_switch_smooth, $focus, $overload, $fatigue, $stability, $dominant_name, $dominant_value, $confidence, $activation_triggered, $cooldown_triggered, $activation_open, $contributors)
            ON CONFLICT(timestamp_utc) DO UPDATE SET
                typing_burst_intensity_raw = excluded.typing_burst_intensity_raw,
                scroll_velocity_index_raw = excluded.scroll_velocity_index_raw,
                focus_switch_rate_raw = excluded.focus_switch_rate_raw,
                typing_burst_intensity_smoothed = excluded.typing_burst_intensity_smoothed,
                scroll_velocity_index_smoothed = excluded.scroll_velocity_index_smoothed,
                focus_switch_rate_smoothed = excluded.focus_switch_rate_smoothed,
                focus_strength = excluded.focus_strength,
                overload_likelihood = excluded.overload_likelihood,
                fatigue_probability = excluded.fatigue_probability,
                cognitive_stability = excluded.cognitive_stability,
                dominant_state_name = excluded.dominant_state_name,
                dominant_state_value = excluded.dominant_state_value,
                confidence = excluded.confidence,
                activation_window_triggered = excluded.activation_window_triggered,
                cooldown_triggered = excluded.cooldown_triggered,
                activation_window_open = excluded.activation_window_open,
                contributors_json = excluded.contributors_json;
            """;
        cmd.Parameters.AddWithValue("$ts", row.TimestampUtc);
        cmd.Parameters.AddWithValue("$typing_raw", row.TypingRaw);
        cmd.Parameters.AddWithValue("$scroll_raw", row.ScrollRaw);
        cmd.Parameters.AddWithValue("$focus_switch_raw", row.FocusRaw);
        cmd.Parameters.AddWithValue("$typing_smooth", row.TypingSmoothed);
        cmd.Parameters.AddWithValue("$scroll_smooth", row.ScrollSmoothed);
        cmd.Parameters.AddWithValue("$focus_switch_smooth", row.FocusSmoothed);
        cmd.Parameters.AddWithValue("$focus", row.FocusStrength);
        cmd.Parameters.AddWithValue("$overload", row.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$fatigue", row.FatigueProbability);
        cmd.Parameters.AddWithValue("$stability", row.CognitiveStability);
        cmd.Parameters.AddWithValue("$dominant_name", row.DominantStateName);
        cmd.Parameters.AddWithValue("$dominant_value", row.DominantStateValue);
        cmd.Parameters.AddWithValue("$confidence", row.Confidence);
        cmd.Parameters.AddWithValue("$activation_triggered", row.ActivationWindowTriggered);
        cmd.Parameters.AddWithValue("$cooldown_triggered", row.CooldownTriggered);
        cmd.Parameters.AddWithValue("$activation_open", row.ActivationWindowOpen);
        cmd.Parameters.AddWithValue("$contributors", row.ContributorsJson);
        cmd.ExecuteNonQuery();
    }

    private readonly record struct PrimitiveBucketWriteModel(
        long BucketSeq,
        string StartUtc,
        string EndUtc,
        string PrimitivesJson,
        string ContributorsJson,
        double Quality,
        int Partial);

    private readonly record struct StateBucketWriteModel(
        string TimestampUtc,
        double TypingRaw,
        double ScrollRaw,
        double FocusRaw,
        double TypingSmoothed,
        double ScrollSmoothed,
        double FocusSmoothed,
        double FocusStrength,
        double OverloadLikelihood,
        double FatigueProbability,
        double CognitiveStability,
        string DominantStateName,
        double DominantStateValue,
        double Confidence,
        int ActivationWindowTriggered,
        int CooldownTriggered,
        int ActivationWindowOpen,
        string ContributorsJson);

    private void InsertLegacySnapshot(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            DELETE FROM brain_snapshots WHERE timestamp_utc = $ts;
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
                contributors_json,
                policy_id,
                domain,
                escalation_level,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                cooldown_end_timestamp_utc
            ) VALUES (
                $ts, $typing, $scroll, $focus_switch, $typing_smooth, $scroll_smooth, $focus_switch_smooth,
                $focus_strength, $overload, $fatigue, $stability, $dominant_name, $dominant_value,
                $confidence, $last_action, $activation_triggered, $cooldown_triggered, $activation_open, $contributors,
                $policy_id, $domain, $escalation, $planned_end, $actual_end, $cooldown_end
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
        cmd.Parameters.AddWithValue("$policy_id", snapshot.PolicyId);
        cmd.Parameters.AddWithValue("$domain", snapshot.Domain);
        cmd.Parameters.AddWithValue("$escalation", snapshot.EscalationLevel);
        cmd.Parameters.AddWithValue("$planned_end", snapshot.PlannedEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$actual_end", snapshot.ActualEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$cooldown_end", snapshot.CooldownEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    private void TryInsertAutomaticInterventionEvent(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        string currentAction = snapshot.LastAction?.Trim() ?? string.Empty;
        bool currentIsIntervention = !string.IsNullOrWhiteSpace(currentAction) && !IsNonInterventionAction(currentAction);

        if (_activeIntervention != null)
        {
            bool actionChanged = !string.Equals(_activeIntervention.Action, currentAction, StringComparison.Ordinal);
            bool plannedExpired = _activeIntervention.PlannedEndTimestampUtc.HasValue
                && snapshot.TimestampUtc >= _activeIntervention.PlannedEndTimestampUtc.Value;
            if (!currentIsIntervention || actionChanged || plannedExpired)
            {
                string reason = !currentIsIntervention
                    ? $"deactivated_due_to:{currentAction}"
                    : actionChanged
                        ? $"deactivated_due_to_action_change:{currentAction}"
                        : "deactivated_due_to_planned_end";
                InsertAutomaticDeactivationEvent(connection, tx, snapshot, reason);
            }
        }

        if (!currentIsIntervention)
        {
            return;
        }

        if (string.Equals(_lastAutoEventAction, currentAction, StringComparison.Ordinal)
            && (snapshot.TimestampUtc - _lastAutoEventAtUtc) < TimeSpan.FromSeconds(DefaultEventCooldownSeconds))
        {
            return;
        }

        (string triggerState, double triggerValue) = ResolveTrigger(snapshot);
        string escalationLevel = string.IsNullOrWhiteSpace(snapshot.EscalationLevel)
            ? ResolveEscalationLevel(currentAction)
            : snapshot.EscalationLevel;
        bool reversible = IsReversibleAction(currentAction);
        int? durationSeconds = ResolveDurationSeconds(currentAction);
        DateTimeOffset? plannedEndTimestampUtc = snapshot.PlannedEndTimestampUtc
            ?? (durationSeconds.HasValue ? snapshot.TimestampUtc.AddSeconds(Math.Max(durationSeconds.Value, 0)) : null);
        string interventionId = Guid.NewGuid().ToString("N");
        string whatChanged = ResolveWhatChanged(currentAction, lifecycleState: "activated");
        string howToUndo = ResolveHowToUndo(currentAction, reversible, lifecycleState: "activated");

        var ev = new AgentControlEvent(
            TimestampUtc: snapshot.TimestampUtc,
            Action: currentAction,
            TriggerState: triggerState,
            TriggerValue: triggerValue,
            Confidence: snapshot.Confidence,
            DurationSeconds: durationSeconds,
            EscalationLevel: escalationLevel,
            Reversible: reversible,
            Reason: ResolveAutomaticReason(currentAction, triggerState, triggerValue, snapshot.Confidence, reversible),
            Contributors: snapshot.Contributors,
            InterventionId: interventionId,
            PolicyId: string.IsNullOrWhiteSpace(snapshot.PolicyId) ? "n/a" : snapshot.PolicyId,
            Domain: string.IsNullOrWhiteSpace(snapshot.Domain) ? "control" : snapshot.Domain,
            PlannedEndTimestampUtc: plannedEndTimestampUtc,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: JsonSerializer.Serialize(new
            {
                previous_action = _activeIntervention?.Action ?? "none",
                previous_escalation = _activeIntervention?.EscalationLevel ?? "Inform"
            }),
            WhatChanged: whatChanged,
            HowToUndo: howToUndo,
            ReversalApplied: false,
            CooldownEndTimestampUtc: snapshot.CooldownEndTimestampUtc,
            LifecycleState: "activated");

        InsertInterventionEvent(connection, tx, ev);
        InsertAutomaticSystemChange(connection, tx, ev);
        _activeIntervention = new ActiveInterventionState(
            InterventionId: interventionId,
            Action: currentAction,
            EscalationLevel: escalationLevel,
            PolicyId: ev.PolicyId,
            Domain: ev.Domain,
            StartTimestampUtc: snapshot.TimestampUtc,
            PlannedEndTimestampUtc: plannedEndTimestampUtc,
            CooldownEndTimestampUtc: snapshot.CooldownEndTimestampUtc);
        _lastAutoEventAction = currentAction;
        _lastAutoEventAtUtc = snapshot.TimestampUtc;
    }

    private void InsertAutomaticDeactivationEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        AgentBrainSnapshot snapshot,
        string reason)
    {
        if (_activeIntervention == null)
        {
            return;
        }

        ActiveInterventionState active = _activeIntervention;
        _activeIntervention = null;

        (string triggerState, double triggerValue) = ResolveTrigger(snapshot);
        int? durationSeconds = null;
        if (snapshot.TimestampUtc >= active.StartTimestampUtc)
        {
            durationSeconds = (int)Math.Max(0, (snapshot.TimestampUtc - active.StartTimestampUtc).TotalSeconds);
        }
        string whatChanged = ResolveWhatChanged(active.Action, lifecycleState: "deactivated");
        string howToUndo = ResolveHowToUndo(active.Action, reversible: true, lifecycleState: "deactivated");
        string deactivationReason = $"{reason}; trigger={triggerState}={triggerValue:F2}; confidence={snapshot.Confidence:F2}";

        var ev = new AgentControlEvent(
            TimestampUtc: snapshot.TimestampUtc,
            Action: active.Action,
            TriggerState: triggerState,
            TriggerValue: triggerValue,
            Confidence: snapshot.Confidence,
            DurationSeconds: durationSeconds,
            EscalationLevel: "Inform",
            Reversible: true,
            Reason: deactivationReason,
            Contributors: ["recovery", "auto_reversal"],
            InterventionId: active.InterventionId,
            PolicyId: active.PolicyId,
            Domain: active.Domain,
            PlannedEndTimestampUtc: active.PlannedEndTimestampUtc,
            ActualEndTimestampUtc: snapshot.TimestampUtc,
            PreviousSystemStateJson: JsonSerializer.Serialize(new
            {
                active_action = active.Action,
                active_escalation = active.EscalationLevel
            }),
            WhatChanged: whatChanged,
            HowToUndo: howToUndo,
            ReversalApplied: true,
            CooldownEndTimestampUtc: snapshot.CooldownEndTimestampUtc ?? active.CooldownEndTimestampUtc,
            LifecycleState: "deactivated");

        InsertInterventionEvent(connection, tx, ev);
        InsertAutomaticSystemChange(connection, tx, ev);
    }

    private static bool IsNonInterventionAction(string action)
    {
        return string.Equals(action, "monitor_only", StringComparison.Ordinal)
            || string.Equals(action, "paused_by_user", StringComparison.Ordinal)
            || string.Equals(action, "consent_required", StringComparison.Ordinal)
            || string.Equals(action, "maintain_focus", StringComparison.Ordinal)
            || string.Equals(action, "starting", StringComparison.Ordinal);
    }

    private static bool IsPolicyDrivenAction(string action)
    {
        return !IsNonInterventionAction(action)
            && !string.IsNullOrWhiteSpace(action);
    }

    private static (string triggerState, double triggerValue) ResolveTrigger(AgentBrainSnapshot snapshot)
    {
        if (string.Equals(snapshot.LastAction, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return ("overload", snapshot.OverloadLikelihood);
        }

        if (string.Equals(snapshot.LastAction, "focus_assist_enabled", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        if (string.Equals(snapshot.LastAction, "fatigue_relief_enabled", StringComparison.Ordinal))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

        if (string.Equals(snapshot.LastAction, "break_suggestion", StringComparison.Ordinal))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

        if (string.Equals(snapshot.LastAction, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        if (string.Equals(snapshot.LastAction, "temporary_app_restriction_enabled", StringComparison.Ordinal))
        {
            if (string.Equals(snapshot.Domain, "focus", StringComparison.OrdinalIgnoreCase))
            {
                return ("focus", snapshot.FocusStrength);
            }

            if (string.Equals(snapshot.Domain, "fatigue", StringComparison.OrdinalIgnoreCase))
            {
                return ("fatigue", snapshot.FatigueProbability);
            }

            return ("overload", snapshot.OverloadLikelihood);
        }

        if (string.Equals(snapshot.LastAction, "inform_overload_risk", StringComparison.Ordinal))
        {
            return ("overload", snapshot.OverloadLikelihood);
        }

        if (string.Equals(snapshot.LastAction, "inform_focus_protection", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        if (string.Equals(snapshot.LastAction, "stretch_break_suggestion", StringComparison.Ordinal))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

        if (string.Equals(snapshot.LastAction, "inform_fatigue_prevention", StringComparison.Ordinal))
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
        if (string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal))
        {
            return "Restrict";
        }

        if (string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return "Nudge";
        }

        if (string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal))
        {
            return "Nudge";
        }

        if (string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal))
        {
            return "Nudge";
        }

        if (string.Equals(action, "break_suggestion", StringComparison.Ordinal))
        {
            return "Suggest";
        }

        if (string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return "Suggest";
        }

        if (string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal))
        {
            return "Suggest";
        }

        return "Inform";
    }

    private static bool IsReversibleAction(string action)
    {
        return string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal)
            || string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal)
            || string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal)
            || string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal);
    }

    private static int? ResolveDurationSeconds(string action)
    {
        if (string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal))
        {
            return 180;
        }

        if (string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return 600;
        }

        if (string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal))
        {
            return 600;
        }

        if (string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal))
        {
            return 600;
        }

        if (string.Equals(action, "break_suggestion", StringComparison.Ordinal))
        {
            return 300;
        }

        if (string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return 300;
        }

        if (string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal))
        {
            return 300;
        }

        if (string.Equals(action, "inform_overload_risk", StringComparison.Ordinal))
        {
            return 60;
        }

        if (string.Equals(action, "inform_focus_protection", StringComparison.Ordinal))
        {
            return 60;
        }

        if (string.Equals(action, "inform_fatigue_prevention", StringComparison.Ordinal))
        {
            return 60;
        }

        return null;
    }

    private static string ResolveAutomaticReason(
        string action,
        string triggerState,
        double triggerValue,
        double confidence,
        bool reversible)
    {
        string reversibility = reversible ? "reversible" : "not_reversible";
        return $"{action} triggered by {triggerState}={triggerValue:F2}, confidence={confidence:F2}, {reversibility}.";
    }

    private static string ResolveWhatChanged(string action, string lifecycleState)
    {
        if (string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase))
        {
            return action switch
            {
                "temporary_app_restriction_enabled" => "Temporary app restriction ended and prior state restoration was applied.",
                "focus_mode_enabled" => "Focus mode intervention ended and prior notification/animation state was restored.",
                "focus_assist_enabled" => "Focus Assist intervention ended and previous Focus Assist state was restored.",
                "fatigue_relief_enabled" => "Fatigue relief intervention ended and prior brightness/animation state was restored.",
                _ => "Intervention ended; prior system state restoration attempted."
            };
        }

        return action switch
        {
            "temporary_app_restriction_enabled" => "Temporary app restriction activated; notification/animation/dimming controls applied per allowed categories.",
            "focus_mode_enabled" => "Focus mode activated; Focus Assist and reduction controls applied per allowed categories.",
            "focus_assist_enabled" => "Focus Assist enabled to protect focus and reduce interruption pressure.",
            "fatigue_relief_enabled" => "Fatigue relief enabled; display dimmed and visual stimulation reduced.",
            "break_suggestion" => "Break suggestion displayed to user (no forced system restriction).",
            "focus_lock_in_suggestion" => "Focus lock-in suggestion displayed to user.",
            "stretch_break_suggestion" => "Stretch break suggestion displayed to user.",
            "inform_overload_risk" => "Overload risk logged for transparency; no direct system setting changed.",
            "inform_focus_protection" => "Focus protection signal logged for transparency; no direct system setting changed.",
            "inform_fatigue_prevention" => "Fatigue prevention signal logged for transparency; no direct system setting changed.",
            _ => $"Action {action} applied."
        };
    }

    private static string ResolveHowToUndo(string action, bool reversible, string lifecycleState)
    {
        if (string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase))
        {
            return "No action required. PORTA already attempted automatic reversal.";
        }

        return action switch
        {
            "break_suggestion" or "focus_lock_in_suggestion" or "stretch_break_suggestion"
                => "Dismiss the suggestion overlay or wait for auto-expiry.",
            "inform_overload_risk" or "inform_focus_protection" or "inform_fatigue_prevention"
                => "No undo required; this action is informational only.",
            _ when reversible
                => "Use Undo Action in Interventions, or disable the category in Settings > Interventions.",
            _ => "No direct undo path is available for this action."
        };
    }

    private static void InsertAutomaticSystemChange(SqliteConnection connection, SqliteTransaction tx, AgentControlEvent controlEvent)
    {
        if (!IsReversibleAction(controlEvent.Action)
            && !string.Equals(controlEvent.Action, "break_suggestion", StringComparison.Ordinal)
            && !string.Equals(controlEvent.Action, "focus_lock_in_suggestion", StringComparison.Ordinal)
            && !string.Equals(controlEvent.Action, "stretch_break_suggestion", StringComparison.Ordinal))
        {
            return;
        }

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
                $ts, $type, $previous, $new, $undo, $reason
            );
            """;
        cmd.Parameters.AddWithValue("$ts", controlEvent.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$type", "regulation");
        cmd.Parameters.AddWithValue("$previous", JsonSerializer.Serialize(new
        {
            action = "none"
        }));
        cmd.Parameters.AddWithValue("$new", JsonSerializer.Serialize(new
        {
            intervention_id = controlEvent.InterventionId,
            action = controlEvent.Action,
            policy_id = controlEvent.PolicyId,
            domain = controlEvent.Domain,
            escalation = controlEvent.EscalationLevel,
            reversible = controlEvent.Reversible,
            lifecycle_state = controlEvent.LifecycleState,
            what_changed = controlEvent.WhatChanged
        }));
        cmd.Parameters.AddWithValue("$undo", string.IsNullOrWhiteSpace(controlEvent.HowToUndo)
            ? (controlEvent.Reversible ? "Disable category in settings/interventions." : "No-op")
            : controlEvent.HowToUndo);
        cmd.Parameters.AddWithValue("$reason", controlEvent.Reason);
        cmd.ExecuteNonQuery();
    }

    private static void InsertInterventionEvent(SqliteConnection connection, SqliteTransaction tx, AgentControlEvent controlEvent)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO events (
                timestamp_utc,
                ts_utc,
                action,
                duration_seconds,
                why,
                why_json,
                escalation,
                reversible,
                trigger_state,
                trigger_value,
                confidence,
                contributors_json,
                state_flags_json,
                idempotency_key,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state
            ) VALUES (
                $ts, $ts, $action, $duration, $reason, $why_json, $escalation, $reversible, $trigger_state, $trigger_value, $confidence, $contributors, $state_flags_json, $idempotency_key,
                $intervention_id, $policy_id, $domain, $planned_end, $actual_end, $previous_state, $what_changed, $how_to_undo, $reversal_applied, $cooldown_end, $lifecycle_state
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
                contributors_json,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state
            ) VALUES (
                $ts, $action, $trigger_state, $trigger_value, $confidence, $duration,
                $escalation, $reversible, $reason, $contributors,
                $intervention_id, $policy_id, $domain, $planned_end, $actual_end, $previous_state, $what_changed, $how_to_undo, $reversal_applied, $cooldown_end, $lifecycle_state
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
        cmd.Parameters.AddWithValue("$contributors", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contributors = controlEvent.Contributors
        }));
        cmd.Parameters.AddWithValue("$why_json", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            reason = controlEvent.Reason
        }));
        cmd.Parameters.AddWithValue("$state_flags_json", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            lifecycle_state = controlEvent.LifecycleState,
            reversible = controlEvent.Reversible,
            reversal_applied = controlEvent.ReversalApplied
        }));
        cmd.Parameters.AddWithValue("$idempotency_key", BuildEventIdempotencyKey(controlEvent));
        cmd.Parameters.AddWithValue("$intervention_id", controlEvent.InterventionId);
        cmd.Parameters.AddWithValue("$policy_id", controlEvent.PolicyId);
        cmd.Parameters.AddWithValue("$domain", controlEvent.Domain);
        cmd.Parameters.AddWithValue("$planned_end", controlEvent.PlannedEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$actual_end", controlEvent.ActualEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$previous_state", controlEvent.PreviousSystemStateJson);
        cmd.Parameters.AddWithValue("$what_changed", controlEvent.WhatChanged ?? string.Empty);
        cmd.Parameters.AddWithValue("$how_to_undo", controlEvent.HowToUndo ?? string.Empty);
        cmd.Parameters.AddWithValue("$reversal_applied", controlEvent.ReversalApplied ? 1 : 0);
        cmd.Parameters.AddWithValue("$cooldown_end", controlEvent.CooldownEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$lifecycle_state", controlEvent.LifecycleState);
        cmd.ExecuteNonQuery();
    }

    private static string BuildEventIdempotencyKey(AgentControlEvent controlEvent)
    {
        string interventionId = string.IsNullOrWhiteSpace(controlEvent.InterventionId)
            ? "none"
            : controlEvent.InterventionId.Trim();
        string lifecycle = string.IsNullOrWhiteSpace(controlEvent.LifecycleState)
            ? "activated"
            : controlEvent.LifecycleState.Trim();
        string action = string.IsNullOrWhiteSpace(controlEvent.Action)
            ? "unknown"
            : controlEvent.Action.Trim();
        string ts = controlEvent.TimestampUtc.ToString("O");
        return $"{action}|{interventionId}|{lifecycle}|{ts}";
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
        CoreStateDbStartup.InitializeAndMigrate(logPragmasAtBoot: true);
    }

    private static List<string> DeserializeContributors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        string? value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            items.Add(value);
                        }
                    }
                }

                return items;
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("contributors", out JsonElement contributorsElement)
                && contributorsElement.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                foreach (JsonElement element in contributorsElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        string? value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            items.Add(value);
                        }
                    }
                }

                return items;
            }

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

    private static string BuildExportRoot()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "exports");
    }

    private void TryDeleteDbFiles()
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

        string walPath = $"{_dbPath}-wal";
        string shmPath = $"{_dbPath}-shm";

        try
        {
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }
        }
        catch
        {
        }

        try
        {
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }
        }
        catch
        {
        }
    }
}

