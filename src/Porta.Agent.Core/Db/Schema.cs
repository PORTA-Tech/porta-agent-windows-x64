using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Db;

internal static class Schema
{
    internal static void EnsureInputSignalWindowsContract(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS input_signal_windows (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                accepted_at_utc TEXT NOT NULL,
                schema_ver INTEGER NOT NULL,
                agent_instance_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                window_start_utc TEXT NOT NULL,
                window_end_utc TEXT NOT NULL,
                keyboard_keydown_count INTEGER NOT NULL,
                keyboard_backspace_count INTEGER NOT NULL,
                mouse_wheel_delta INTEGER NOT NULL,
                mouse_move_dx REAL NOT NULL,
                mouse_move_dy REAL NOT NULL,
                focus_app_hash TEXT NOT NULL DEFAULT '',
                idle_seconds REAL NOT NULL,
                last_input_event_utc TEXT NOT NULL DEFAULT '',
                keyboard_iki_mean_ms REAL NULL,
                keyboard_iki_std_ms REAL NULL,
                keyboard_iki_cv REAL NULL,
                keyboard_burst_count INTEGER NOT NULL DEFAULT 0,
                keyboard_burst_rate REAL NULL,
                keyboard_correction_spike_count INTEGER NOT NULL DEFAULT 0,
                keyboard_correction_spike_rate REAL NULL,
                mouse_move_sample_count INTEGER NOT NULL DEFAULT 0,
                mouse_path_length_px REAL NULL,
                mouse_speed_mean_px_per_sec REAL NULL,
                mouse_jerk_p95 REAL NULL,
                mouse_micro_correction_count INTEGER NOT NULL DEFAULT 0,
                mouse_micro_correction_rate REAL NULL,
                mouse_wheel_event_count INTEGER NOT NULL DEFAULT 0,
                mouse_scroll_burst_count INTEGER NOT NULL DEFAULT 0,
                mouse_scroll_burst_rate REAL NULL,
                mouse_scroll_speed_mean REAL NULL,
                focus_observation_count INTEGER NOT NULL DEFAULT 0,
                focus_switch_count INTEGER NOT NULL DEFAULT 0,
                focus_active_app_count INTEGER NOT NULL DEFAULT 0,
                focus_app_entropy REAL NULL,
                idle_observation_count INTEGER NOT NULL DEFAULT 0,
                idle_seconds_within_window REAL NULL,
                micro_idle_count INTEGER NOT NULL DEFAULT 0,
                micro_idle_seconds REAL NULL,
                break_seconds_within_window REAL NULL,
                short_idle_boundary_observed INTEGER NOT NULL DEFAULT 0,
                notification_toasts_enabled INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS ix_input_signal_windows_range
                ON input_signal_windows(window_start_utc, window_end_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyBaseSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS input_signal_windows (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                accepted_at_utc TEXT NOT NULL,
                schema_ver INTEGER NOT NULL,
                agent_instance_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                window_start_utc TEXT NOT NULL,
                window_end_utc TEXT NOT NULL,
                keyboard_keydown_count INTEGER NOT NULL,
                keyboard_backspace_count INTEGER NOT NULL,
                mouse_wheel_delta INTEGER NOT NULL,
                mouse_move_dx REAL NOT NULL,
                mouse_move_dy REAL NOT NULL,
                focus_app_hash TEXT NOT NULL DEFAULT '',
                idle_seconds REAL NOT NULL,
                last_input_event_utc TEXT NOT NULL DEFAULT '',
                keyboard_iki_mean_ms REAL NULL,
                keyboard_iki_std_ms REAL NULL,
                keyboard_iki_cv REAL NULL,
                keyboard_burst_count INTEGER NOT NULL DEFAULT 0,
                keyboard_burst_rate REAL NULL,
                keyboard_correction_spike_count INTEGER NOT NULL DEFAULT 0,
                keyboard_correction_spike_rate REAL NULL,
                mouse_move_sample_count INTEGER NOT NULL DEFAULT 0,
                mouse_path_length_px REAL NULL,
                mouse_speed_mean_px_per_sec REAL NULL,
                mouse_jerk_p95 REAL NULL,
                mouse_micro_correction_count INTEGER NOT NULL DEFAULT 0,
                mouse_micro_correction_rate REAL NULL,
                mouse_wheel_event_count INTEGER NOT NULL DEFAULT 0,
                mouse_scroll_burst_count INTEGER NOT NULL DEFAULT 0,
                mouse_scroll_burst_rate REAL NULL,
                mouse_scroll_speed_mean REAL NULL,
                focus_observation_count INTEGER NOT NULL DEFAULT 0,
                focus_switch_count INTEGER NOT NULL DEFAULT 0,
                focus_active_app_count INTEGER NOT NULL DEFAULT 0,
                focus_app_entropy REAL NULL,
                idle_observation_count INTEGER NOT NULL DEFAULT 0,
                idle_seconds_within_window REAL NULL,
                micro_idle_count INTEGER NOT NULL DEFAULT 0,
                micro_idle_seconds REAL NULL,
                break_seconds_within_window REAL NULL,
                short_idle_boundary_observed INTEGER NOT NULL DEFAULT 0,
                notification_toasts_enabled INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS input_buckets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                bucket_seq INTEGER NOT NULL DEFAULT 0,
                typing_burst_intensity REAL NOT NULL,
                scroll_velocity_index REAL NOT NULL,
                focus_switch_rate REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS baseline_stats (
                metric_name TEXT PRIMARY KEY,
                mean REAL NOT NULL,
                std REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS primitive_buckets (
                bucket_seq INTEGER PRIMARY KEY,
                start_utc TEXT NOT NULL,
                end_utc TEXT NOT NULL,
                primitives_json TEXT NOT NULL,
                contributors_json TEXT NOT NULL,
                quality REAL NOT NULL,
                partial INTEGER NOT NULL
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
                contributors_json TEXT NOT NULL,
                intervention_id TEXT NOT NULL DEFAULT '',
                policy_id TEXT NOT NULL DEFAULT 'n/a',
                domain TEXT NOT NULL DEFAULT 'control',
                planned_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                actual_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                previous_system_state_json TEXT NOT NULL DEFAULT '{}',
                what_changed TEXT NOT NULL DEFAULT '',
                how_to_undo TEXT NOT NULL DEFAULT '',
                reversal_applied INTEGER NOT NULL DEFAULT 0,
                cooldown_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                lifecycle_state TEXT NOT NULL DEFAULT 'activated'
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
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runtime_state (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
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
                contributors_json TEXT NOT NULL,
                intervention_id TEXT NOT NULL DEFAULT '',
                policy_id TEXT NOT NULL DEFAULT 'n/a',
                domain TEXT NOT NULL DEFAULT 'control',
                planned_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                actual_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                previous_system_state_json TEXT NOT NULL DEFAULT '{}',
                what_changed TEXT NOT NULL DEFAULT '',
                how_to_undo TEXT NOT NULL DEFAULT '',
                reversal_applied INTEGER NOT NULL DEFAULT 0,
                cooldown_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                lifecycle_state TEXT NOT NULL DEFAULT 'activated',
                session_id TEXT NOT NULL DEFAULT '',
                command_type TEXT NOT NULL DEFAULT '',
                payload_json TEXT NOT NULL DEFAULT '{}',
                execution_state TEXT NOT NULL DEFAULT 'decision_only',
                decided_at_utc TEXT NOT NULL DEFAULT '',
                applied_at_utc TEXT NOT NULL DEFAULT '',
                acked_at_utc TEXT NOT NULL DEFAULT '',
                error_code TEXT NOT NULL DEFAULT '',
                retry_count INTEGER NOT NULL DEFAULT 0,
                executor_agent_instance_id TEXT NOT NULL DEFAULT ''
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
                contributors_json TEXT NOT NULL,
                policy_id TEXT NOT NULL DEFAULT 'n/a',
                domain TEXT NOT NULL DEFAULT 'control',
                escalation_level TEXT NOT NULL DEFAULT 'Inform',
                planned_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                actual_end_timestamp_utc TEXT NOT NULL DEFAULT '',
                cooldown_end_timestamp_utc TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS ix_input_buckets_ts ON input_buckets(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_input_bucket_seq ON input_buckets(bucket_seq DESC);
            CREATE INDEX IF NOT EXISTS ix_baseline_stats_metric ON baseline_stats(metric_name);
            CREATE INDEX IF NOT EXISTS ix_primitive_buckets_start_utc ON primitive_buckets(start_utc);
            CREATE INDEX IF NOT EXISTS ix_state_buckets_ts ON state_buckets(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_events_ts ON events(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_events_recent ON events(timestamp_utc DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_system_changes_ts ON system_changes(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_diagnostics_updated_utc ON diagnostics(updated_utc);
            CREATE INDEX IF NOT EXISTS ix_runtime_state_updated_utc ON runtime_state(updated_utc);
            CREATE INDEX IF NOT EXISTS ix_consent_ts ON consent(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_settings_ts ON settings(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_interventions_ts ON interventions(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_interventions_delivery_pending
                ON interventions(session_id, execution_state, decided_at_utc, id);
            CREATE INDEX IF NOT EXISTS ix_interventions_delivery_latest
                ON interventions(intervention_id, id DESC);
            CREATE INDEX IF NOT EXISTS ix_diagnostics_ts ON diagnostics_snapshots(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_brain_snapshots_ts ON brain_snapshots(timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_input_signal_windows_range ON input_signal_windows(window_start_utc, window_end_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplySchemaPatches(SqliteConnection connection)
    {
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
        EnsureColumnExists(connection, "brain_snapshots", "policy_id", "TEXT NOT NULL DEFAULT 'n/a'");
        EnsureColumnExists(connection, "brain_snapshots", "domain", "TEXT NOT NULL DEFAULT 'control'");
        EnsureColumnExists(connection, "brain_snapshots", "escalation_level", "TEXT NOT NULL DEFAULT 'Inform'");
        EnsureColumnExists(connection, "brain_snapshots", "planned_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "brain_snapshots", "actual_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "brain_snapshots", "cooldown_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");

        EnsureColumnExists(connection, "events", "intervention_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "events", "policy_id", "TEXT NOT NULL DEFAULT 'n/a'");
        EnsureColumnExists(connection, "events", "domain", "TEXT NOT NULL DEFAULT 'control'");
        EnsureColumnExists(connection, "events", "planned_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "events", "actual_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "events", "previous_system_state_json", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumnExists(connection, "events", "what_changed", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "events", "how_to_undo", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "events", "reversal_applied", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "events", "cooldown_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "events", "lifecycle_state", "TEXT NOT NULL DEFAULT 'activated'");

        EnsureColumnExists(connection, "interventions", "intervention_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "policy_id", "TEXT NOT NULL DEFAULT 'n/a'");
        EnsureColumnExists(connection, "interventions", "domain", "TEXT NOT NULL DEFAULT 'control'");
        EnsureColumnExists(connection, "interventions", "planned_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "actual_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "previous_system_state_json", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumnExists(connection, "interventions", "what_changed", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "how_to_undo", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "reversal_applied", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "interventions", "cooldown_end_timestamp_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "lifecycle_state", "TEXT NOT NULL DEFAULT 'activated'");
    }

    internal static void ApplyInterventionsDeliveryContractV18(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "interventions", "session_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "command_type", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "payload_json", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumnExists(connection, "interventions", "execution_state", "TEXT NOT NULL DEFAULT 'decision_only'");
        EnsureColumnExists(connection, "interventions", "decided_at_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "applied_at_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "acked_at_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "error_code", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "interventions", "retry_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "interventions", "executor_agent_instance_id", "TEXT NOT NULL DEFAULT ''");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                UPDATE interventions
                SET decided_at_utc = timestamp_utc
                WHERE decided_at_utc IS NULL OR decided_at_utc = '';

                UPDATE interventions
                SET execution_state = CASE
                    WHEN command_type IS NOT NULL AND command_type <> '' THEN 'decided'
                    ELSE 'decision_only'
                END
                WHERE execution_state IS NULL OR execution_state = '';

                CREATE INDEX IF NOT EXISTS ix_interventions_delivery_pending
                    ON interventions(session_id, execution_state, decided_at_utc, id);

                CREATE INDEX IF NOT EXISTS ix_interventions_delivery_latest
                    ON interventions(intervention_id, id DESC);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    internal static void ApplyBrainSnapshotsCleanupV19(SqliteConnection connection)
    {
        // brain_snapshots was written at the 5-second pipeline cadence (~17 000 rows/day).
        // ReadTimeline now reads from state_buckets (Step 9), so brain_snapshots is no
        // longer needed and can be purged. Deleting it and running VACUUM recovers roughly
        // 1 GB of disk space on a typical installation and removes a major source of
        // write-amplification that was slowing the service down.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM brain_snapshots;";
            cmd.ExecuteNonQuery();
        }

        // VACUUM outside a transaction to compact the file on disk.
        // This runs once, blocks the service for a few seconds, and is worth it.
        using (var vacuumCmd = connection.CreateCommand())
        {
            vacuumCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            vacuumCmd.ExecuteNonQuery();
        }
    }

    internal static void ApplyContractSchemaV3(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS baseline_stats (
                metric_name TEXT PRIMARY KEY,
                mean REAL NOT NULL,
                std REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS primitive_buckets (
                bucket_seq INTEGER PRIMARY KEY,
                start_utc TEXT NOT NULL,
                end_utc TEXT NOT NULL,
                primitives_json TEXT NOT NULL,
                contributors_json TEXT NOT NULL,
                quality REAL NOT NULL,
                partial INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runtime_state (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // idempotency_key is required now so a later migration can enforce uniqueness for exactly-once-ish ingestion.
        EnsureColumnExists(connection, "events", "idempotency_key", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "events", "contributors_json", "TEXT NOT NULL DEFAULT '{\"schema_ver\":1,\"contributors\":[]}'");
        EnsureColumnExists(connection, "events", "why_json", "TEXT NOT NULL DEFAULT '{\"schema_ver\":1}'");
        EnsureColumnExists(connection, "events", "state_flags_json", "TEXT NOT NULL DEFAULT '{\"schema_ver\":1}'");

        AssertEventsContract(connection, requireUniqueIdempotencyIndex: false);
    }

    internal static void ApplyDiagnosticsKeyValueContract(SqliteConnection connection)
    {
        HashSet<string> columns = GetTableColumns(connection, "diagnostics");
        bool hasKeyValueShape = columns.Contains("key")
            && columns.Contains("value")
            && columns.Contains("updated_utc");

        if (!hasKeyValueShape && columns.Count > 0)
        {
            bool hasLegacy = TableExists(connection, "diagnostics_legacy");
            using var renameOrDrop = connection.CreateCommand();
            renameOrDrop.CommandText = hasLegacy
                ? "DROP TABLE diagnostics;"
                : "ALTER TABLE diagnostics RENAME TO diagnostics_legacy;";
            renameOrDrop.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS diagnostics (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_diagnostics_updated_utc ON diagnostics(updated_utc);
                """;
            cmd.ExecuteNonQuery();
        }

        AssertDiagnosticsContract(connection);
    }

    internal static void ApplyIndexContractV5(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "events", "ts_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "input_buckets", "start_utc", "TEXT NOT NULL DEFAULT ''");

        BackfillColumnFromColumn(connection, "events", "ts_utc", "timestamp_utc");
        BackfillColumnFromColumn(connection, "input_buckets", "start_utc", "timestamp_utc");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                DROP INDEX IF EXISTS idx_events_recent;
                CREATE INDEX IF NOT EXISTS idx_events_recent ON events(ts_utc DESC, id DESC);
                CREATE INDEX IF NOT EXISTS idx_input_start_utc ON input_buckets(start_utc);
                """;
            cmd.ExecuteNonQuery();
        }

        EnsureBucketSeqIndexIfNeeded(connection, "primitive_buckets", "idx_primitive_buckets_bucket_seq");
        EnsureBucketSeqIndexIfNeeded(connection, "state_buckets", "idx_state_buckets_bucket_seq");
    }

    internal static void ApplyInputBucketsContractV6(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "input_buckets", "quality_score", "REAL NOT NULL DEFAULT 1");
        EnsureColumnExists(connection, "input_buckets", "partial", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(
            connection,
            "input_buckets",
            "contributors_json",
            "TEXT NOT NULL DEFAULT '{\"schema_ver\":1,\"keyboard\":{\"keys\":0,\"backspace\":0},\"mouse\":{\"moves\":0,\"scroll\":0},\"focus\":{\"switches\":0},\"idle\":{\"idle_s\":0}}'");
    }

    internal static void ApplyBucketFinalizeUpsertContractV7(SqliteConnection connection)
    {
        // Deduplicate before creating UNIQUE indexes used by UPSERT write path.
        using (var dedupe = connection.CreateCommand())
        {
            dedupe.CommandText =
                """
                DELETE FROM input_buckets
                WHERE id NOT IN (
                    SELECT MAX(id)
                    FROM input_buckets
                    GROUP BY start_utc
                );

                DELETE FROM state_buckets
                WHERE id NOT IN (
                    SELECT MAX(id)
                    FROM state_buckets
                    GROUP BY timestamp_utc
                );
                """;
            dedupe.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_input_buckets_start_utc
                ON input_buckets(start_utc);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_state_buckets_timestamp_utc
                ON state_buckets(timestamp_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyInputBucketMetricsContractV8(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "input_buckets", "keyboard_iki_mean", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "keyboard_iki_cv", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "keyboard_burst_rate", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "keyboard_backspace_rate", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "mouse_speed_mean", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "mouse_jerk_p95", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "mouse_scroll_speed", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "mouse_micro_correction_rate", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "focus_app_entropy", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "focus_active_app_count", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "idle_seconds", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "idle_micro_idle_count", "REAL NOT NULL DEFAULT 0");
    }

    internal static void ApplyBaselineStatsContractV9(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS baseline_stats (
                    metric_name TEXT PRIMARY KEY,
                    mean REAL NOT NULL,
                    std REAL NOT NULL,
                    sample_count INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_baseline_stats_updated_utc ON baseline_stats(updated_utc);
                """;
            cmd.ExecuteNonQuery();
        }

        EnsureColumnExists(connection, "baseline_stats", "metric_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "baseline_stats", "mean", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "baseline_stats", "std", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "baseline_stats", "sample_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "baseline_stats", "updated_utc", "TEXT NOT NULL DEFAULT ''");
    }

    internal static void ApplyBaselineStatsWelfordContractV10(SqliteConnection connection)
    {
        if (!TableExists(connection, "baseline_stats"))
        {
            using var createFresh = connection.CreateCommand();
            createFresh.CommandText =
                """
                CREATE TABLE IF NOT EXISTS baseline_stats (
                    metric_key TEXT PRIMARY KEY,
                    mean REAL NOT NULL,
                    std REAL NOT NULL,
                    sample_count INTEGER NOT NULL,
                    m2 REAL NOT NULL,
                    last_update_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_baseline_stats_last_update_utc ON baseline_stats(last_update_utc);
                """;
            createFresh.ExecuteNonQuery();
            return;
        }

        HashSet<string> columns = GetTableColumns(connection, "baseline_stats");
        bool hasContractShape = IsBaselineStatsV10Shape(connection, columns);
        if (hasContractShape)
        {
            using var createIndex = connection.CreateCommand();
            createIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_baseline_stats_last_update_utc ON baseline_stats(last_update_utc);";
            createIndex.ExecuteNonQuery();
            return;
        }

        bool hasMetricKey = columns.Contains("metric_key");
        bool hasMetricName = columns.Contains("metric_name");
        bool hasMean = columns.Contains("mean");
        bool hasStd = columns.Contains("std");
        bool hasSampleCount = columns.Contains("sample_count");
        bool hasM2 = columns.Contains("m2");
        bool hasLastUpdateUtc = columns.Contains("last_update_utc");
        bool hasUpdatedUtc = columns.Contains("updated_utc");

        string metricKeyExpr = hasMetricKey
            ? "NULLIF(metric_key, '')"
            : "NULL";
        if (hasMetricName)
        {
            metricKeyExpr = $"COALESCE({metricKeyExpr}, NULLIF(metric_name, ''), 'unknown_metric')";
        }
        else
        {
            metricKeyExpr = $"COALESCE({metricKeyExpr}, 'unknown_metric')";
        }

        string meanExpr = hasMean ? "COALESCE(mean, 0)" : "0";
        string stdExpr = hasStd ? "COALESCE(std, 0)" : "0";
        string sampleCountExpr = hasSampleCount ? "COALESCE(sample_count, 0)" : "0";
        string m2Expr = hasM2
            ? "MAX(0, COALESCE(m2, 0))"
            : (hasStd && hasSampleCount
                ? "MAX(0, COALESCE(std, 0) * COALESCE(std, 0) * COALESCE(sample_count, 0))"
                : "0");
        string lastUpdateExpr = hasLastUpdateUtc
            ? "NULLIF(last_update_utc, '')"
            : "NULL";
        if (hasUpdatedUtc)
        {
            lastUpdateExpr = $"COALESCE({lastUpdateExpr}, NULLIF(updated_utc, ''), '1970-01-01T00:00:00.000Z')";
        }
        else
        {
            lastUpdateExpr = $"COALESCE({lastUpdateExpr}, '1970-01-01T00:00:00.000Z')";
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            CREATE TABLE IF NOT EXISTS baseline_stats_v10 (
                metric_key TEXT PRIMARY KEY,
                mean REAL NOT NULL,
                std REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                m2 REAL NOT NULL,
                last_update_utc TEXT NOT NULL
            );

            INSERT INTO baseline_stats_v10 (
                metric_key,
                mean,
                std,
                sample_count,
                m2,
                last_update_utc
            )
            SELECT
                {metricKeyExpr},
                {meanExpr},
                {stdExpr},
                {sampleCountExpr},
                {m2Expr},
                {lastUpdateExpr}
            FROM baseline_stats;

            DROP TABLE baseline_stats;
            ALTER TABLE baseline_stats_v10 RENAME TO baseline_stats;
            CREATE INDEX IF NOT EXISTS idx_baseline_stats_last_update_utc ON baseline_stats(last_update_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyInputBucketsBucketSeqContractV11(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "input_buckets", "bucket_seq", "INTEGER NOT NULL DEFAULT 0");
        BackfillInputBucketSeqFromStartUtc(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_input_bucket_seq ON input_buckets(bucket_seq DESC);";
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyMetricBoundsContractV12(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS metric_bounds (
                    metric_key TEXT PRIMARY KEY,
                    p5 REAL NOT NULL,
                    p95 REAL NOT NULL,
                    bounds_sample_count INTEGER NOT NULL,
                    last_bounds_update_utc TEXT NOT NULL,
                    samples_json TEXT NOT NULL,
                    estimator_ver INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_metric_bounds_last_update_utc ON metric_bounds(last_bounds_update_utc);
                """;
            cmd.ExecuteNonQuery();
        }

        EnsureColumnExists(connection, "metric_bounds", "metric_key", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "metric_bounds", "p5", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "metric_bounds", "p95", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "metric_bounds", "bounds_sample_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "metric_bounds", "last_bounds_update_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "metric_bounds", "samples_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumnExists(connection, "metric_bounds", "estimator_ver", "INTEGER NOT NULL DEFAULT 1");
    }

    internal static void ApplyPrimitiveBucketsJsonContractV13(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "primitive_buckets", "contributors_json", "TEXT NOT NULL DEFAULT '{\"schema_ver\":1}'");
        EnsureColumnExists(connection, "primitive_buckets", "why_json", "TEXT NOT NULL DEFAULT '{\"schema_ver\":1}'");
        EnsureColumnExists(connection, "primitive_buckets", "state_flags_json", "TEXT NOT NULL DEFAULT '{\"schema_ver\":1}'");
    }

    internal static void ApplyStateBucketsDerivedSessionContractV14(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "state_buckets", "sustained_attention_duration_sec", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "state_buckets", "idle_fragmentation_index", "REAL NOT NULL DEFAULT 0");
    }

    internal static void ApplyInputBucketsSessionFieldsContractV15(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "input_buckets", "focus_switch_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "dominant_app_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "input_buckets", "idle_micro_idle_seconds", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "true_break_seconds", "REAL NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "input_buckets", "true_break_flag", "INTEGER NOT NULL DEFAULT 0");
    }

    internal static void ApplyStateBucketsStateFlagsContractV16(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "state_buckets", "state_flags_json", "TEXT NOT NULL DEFAULT '{\"schema_ver\":1}'");
    }

    internal static void ApplyEventsIdempotencyUniqueContractV17(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "events", "idempotency_key", "TEXT NOT NULL DEFAULT ''");

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                UPDATE events
                SET idempotency_key = 'legacy_v17:' || CAST(id AS TEXT)
                WHERE idempotency_key IS NULL OR TRIM(idempotency_key) = '';
                """;
            cmd.ExecuteNonQuery();
        }

        using (var dedupe = connection.CreateCommand())
        {
            dedupe.CommandText =
                """
                DELETE FROM events
                WHERE id NOT IN (
                    SELECT MIN(id)
                    FROM events
                    GROUP BY idempotency_key
                );
                """;
            dedupe.ExecuteNonQuery();
        }

        using var index = connection.CreateCommand();
        index.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_events_idempotency_key
                ON events(idempotency_key);
            """;
        index.ExecuteNonQuery();
    }

    internal static void AssertEventsContract(
        SqliteConnection connection,
        bool requireUniqueIdempotencyIndex = true)
    {
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "idempotency_key",
            "contributors_json",
            "why_json",
            "state_flags_json"
        };

        HashSet<string> actualColumns = new(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(events);";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                actualColumns.Add(reader.GetString(1));
            }
        }

        List<string> missing = requiredColumns.Where(column => !actualColumns.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Events schema assertion failed. Missing required columns: {string.Join(", ", missing)}.");
        }

        HashSet<string> actualIndexes = new(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'events';";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    actualIndexes.Add(reader.GetString(0));
                }
            }
        }

        if (requireUniqueIdempotencyIndex && !actualIndexes.Contains("ux_events_idempotency_key"))
        {
            throw new InvalidOperationException(
                "Events schema assertion failed. Missing unique index ux_events_idempotency_key.");
        }
    }

    internal static void AssertDiagnosticsContract(SqliteConnection connection)
    {
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "key",
            "value",
            "updated_utc"
        };

        HashSet<string> actualColumns = GetTableColumns(connection, "diagnostics");
        List<string> missing = requiredColumns.Where(column => !actualColumns.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Diagnostics schema assertion failed. Missing required columns: {string.Join(", ", missing)}.");
        }
    }

    internal static void AssertInputBucketsContract(SqliteConnection connection)
    {
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp_utc",
            "bucket_seq",
            "start_utc",
            "typing_burst_intensity",
            "scroll_velocity_index",
            "focus_switch_rate",
            "keyboard_iki_mean",
            "keyboard_iki_cv",
            "keyboard_burst_rate",
            "keyboard_backspace_rate",
            "mouse_speed_mean",
            "mouse_jerk_p95",
            "mouse_scroll_speed",
            "mouse_micro_correction_rate",
            "focus_app_entropy",
            "focus_switch_count",
            "dominant_app_hash",
            "focus_active_app_count",
            "idle_seconds",
            "idle_micro_idle_count",
            "idle_micro_idle_seconds",
            "true_break_seconds",
            "true_break_flag",
            "quality_score",
            "partial",
            "contributors_json"
        };

        HashSet<string> actualColumns = GetTableColumns(connection, "input_buckets");
        List<string> missing = requiredColumns.Where(column => !actualColumns.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Input buckets schema assertion failed. Missing required columns: {string.Join(", ", missing)}.");
        }
    }

    internal static void AssertBaselineStatsContract(SqliteConnection connection)
    {
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "metric_key",
            "mean",
            "std",
            "sample_count",
            "m2",
            "last_update_utc"
        };

        HashSet<string> actualColumns = GetTableColumns(connection, "baseline_stats");
        List<string> missing = requiredColumns.Where(column => !actualColumns.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Baseline stats schema assertion failed. Missing required columns: {string.Join(", ", missing)}.");
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(baseline_stats);";
        using SqliteDataReader reader = cmd.ExecuteReader();

        bool metricKeyPrimaryKey = false;
        while (reader.Read())
        {
            string columnName = reader.GetString(1);
            int pkOrder = reader.GetInt32(5);
            if (string.Equals(columnName, "metric_key", StringComparison.OrdinalIgnoreCase))
            {
                metricKeyPrimaryKey = pkOrder > 0;
                break;
            }
        }

        if (!metricKeyPrimaryKey)
        {
            throw new InvalidOperationException(
                "Baseline stats schema assertion failed. metric_key must be the PRIMARY KEY.");
        }
    }

    internal static void AssertMetricBoundsContract(SqliteConnection connection)
    {
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "metric_key",
            "p5",
            "p95",
            "bounds_sample_count",
            "last_bounds_update_utc",
            "samples_json",
            "estimator_ver"
        };

        HashSet<string> actualColumns = GetTableColumns(connection, "metric_bounds");
        List<string> missing = requiredColumns.Where(column => !actualColumns.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Metric bounds schema assertion failed. Missing required columns: {string.Join(", ", missing)}.");
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(metric_bounds);";
        using SqliteDataReader reader = cmd.ExecuteReader();

        bool metricKeyPrimaryKey = false;
        while (reader.Read())
        {
            string columnName = reader.GetString(1);
            int pkOrder = reader.GetInt32(5);
            if (string.Equals(columnName, "metric_key", StringComparison.OrdinalIgnoreCase))
            {
                metricKeyPrimaryKey = pkOrder > 0;
                break;
            }
        }

        if (!metricKeyPrimaryKey)
        {
            throw new InvalidOperationException(
                "Metric bounds schema assertion failed. metric_key must be the PRIMARY KEY.");
        }
    }

    internal static void AssertPrimitiveBucketsContract(SqliteConnection connection)
    {
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "bucket_seq",
            "start_utc",
            "end_utc",
            "primitives_json",
            "contributors_json",
            "why_json",
            "state_flags_json",
            "quality",
            "partial"
        };

        HashSet<string> actualColumns = GetTableColumns(connection, "primitive_buckets");
        List<string> missing = requiredColumns.Where(column => !actualColumns.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Primitive buckets schema assertion failed. Missing required columns: {string.Join(", ", missing)}.");
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(primitive_buckets);";
        using SqliteDataReader reader = cmd.ExecuteReader();

        bool bucketSeqPrimaryKey = false;
        while (reader.Read())
        {
            string columnName = reader.GetString(1);
            int pkOrder = reader.GetInt32(5);
            if (string.Equals(columnName, "bucket_seq", StringComparison.OrdinalIgnoreCase))
            {
                bucketSeqPrimaryKey = pkOrder > 0;
                break;
            }
        }

        if (!bucketSeqPrimaryKey)
        {
            throw new InvalidOperationException(
                "Primitive buckets schema assertion failed. bucket_seq must be the PRIMARY KEY.");
        }
    }

    internal static void AssertStateBucketsDerivedSessionContract(SqliteConnection connection)
    {
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp_utc",
            "sustained_attention_duration_sec",
            "idle_fragmentation_index",
            "state_flags_json"
        };

        HashSet<string> actualColumns = GetTableColumns(connection, "state_buckets");
        List<string> missing = requiredColumns.Where(column => !actualColumns.Contains(column)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"State buckets derived session schema assertion failed. Missing required columns: {string.Join(", ", missing)}.");
        }
    }

    private static bool IsBaselineStatsV10Shape(SqliteConnection connection, HashSet<string>? columns = null)
    {
        columns ??= GetTableColumns(connection, "baseline_stats");
        HashSet<string> requiredColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "metric_key",
            "mean",
            "std",
            "sample_count",
            "m2",
            "last_update_utc"
        };
        if (requiredColumns.Any(column => !columns.Contains(column)))
        {
            return false;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(baseline_stats);";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string columnName = reader.GetString(1);
            int pkOrder = reader.GetInt32(5);
            if (string.Equals(columnName, "metric_key", StringComparison.OrdinalIgnoreCase))
            {
                return pkOrder > 0;
            }
        }

        return false;
    }

    internal static void AssertBucketFinalizeUpsertContract(SqliteConnection connection)
    {
        HashSet<string> requiredIndexes = new(StringComparer.OrdinalIgnoreCase)
        {
            "ux_input_buckets_start_utc",
            "ux_state_buckets_timestamp_utc"
        };

        var actualIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                actualIndexes.Add(reader.GetString(0));
            }
        }

        List<string> missing = requiredIndexes.Where(index => !actualIndexes.Contains(index)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Bucket finalize upsert contract failed. Missing indexes: {string.Join(", ", missing)}.");
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        object? row = cmd.ExecuteScalar();
        return row != null;
    }

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void BackfillColumnFromColumn(
        SqliteConnection connection,
        string tableName,
        string targetColumn,
        string sourceColumn)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE {tableName}
            SET {targetColumn} = {sourceColumn}
            WHERE ({targetColumn} IS NULL OR {targetColumn} = '')
              AND ({sourceColumn} IS NOT NULL AND {sourceColumn} <> '');
            """;
        cmd.ExecuteNonQuery();
    }

    private static void BackfillInputBucketSeqFromStartUtc(SqliteConnection connection)
    {
        var rows = new List<(long Id, string StartUtc)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                """
                SELECT id, start_utc
                FROM input_buckets
                WHERE start_utc IS NOT NULL
                  AND start_utc <> '';
                """;

            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    continue;
                }

                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE input_buckets
            SET bucket_seq = $bucket_seq
            WHERE id = $id;
            """;
        SqliteParameter seqParam = update.Parameters.Add("$bucket_seq", SqliteType.Integer);
        SqliteParameter idParam = update.Parameters.Add("$id", SqliteType.Integer);

        foreach ((long id, string startUtc) in rows)
        {
            if (!DateTimeOffset.TryParse(
                    startUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
            {
                continue;
            }

            long bucketSeq = Time.bucketSeqFromEventTs(parsed.ToUnixTimeMilliseconds());
            seqParam.Value = bucketSeq;
            idParam.Value = id;
            update.ExecuteNonQuery();
        }
    }

    private static void EnsureBucketSeqIndexIfNeeded(SqliteConnection connection, string tableName, string indexName)
    {
        bool hasBucketSeq = false;
        bool bucketSeqIsPrimaryKey = false;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), "bucket_seq", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                hasBucketSeq = true;
                bucketSeqIsPrimaryKey = reader.GetInt32(5) > 0;
                break;
            }
        }

        if (!hasBucketSeq || bucketSeqIsPrimaryKey)
        {
            return;
        }

        using var createIndex = connection.CreateCommand();
        createIndex.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName}(bucket_seq);";
        createIndex.ExecuteNonQuery();
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
}
