using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Db;

internal static class Schema
{
    internal static void ApplyBaseSchema(SqliteConnection connection)
    {
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
