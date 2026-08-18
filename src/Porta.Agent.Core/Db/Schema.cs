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
        // longer needed and the rows can be purged to reduce write load and query overhead.
        //
        // NOTE: VACUUM cannot run inside a transaction (ApplyStep wraps migrations in
        // BEGIN IMMEDIATE). The DELETE alone is sufficient to stop further growth; the
        // SqliteBrainStateStore schedules an async VACUUM after startup to reclaim disk space.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM brain_snapshots;";
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyGroundTruthLabelsV20(SqliteConnection connection)
    {
        // Phase 1b: durable ground-truth labels ("was Ari right?"). This is deliberately a
        // SEPARATE table that the 14-day PruneIfNeeded does NOT touch — labels must survive
        // months for the future learned model. Rows are rare (a handful/day) and self-contained
        // (the full feature snapshot lives in features_json), so keeping them forever costs
        // ~single-digit MB/year and never approaches the raw-firehose sizes that force the prune.
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ground_truth_labels (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at_utc        TEXT    NOT NULL,
                bucket_seq            INTEGER NOT NULL DEFAULT -1,
                bucket_timestamp_utc  TEXT    NOT NULL DEFAULT '',
                label_source          TEXT    NOT NULL,
                prompt_kind           TEXT    NOT NULL,
                response_kind         TEXT    NOT NULL,
                label_target          TEXT    NOT NULL,
                label_value           INTEGER NOT NULL,
                response_latency_ms   INTEGER NOT NULL DEFAULT -1,
                intervention_id       TEXT    NOT NULL DEFAULT '',
                features_json         TEXT    NOT NULL,
                pressure_level        REAL    NOT NULL DEFAULT 0,
                overload_likelihood   REAL    NOT NULL DEFAULT 0,
                confidence            REAL    NOT NULL DEFAULT 0,
                dominant_state        TEXT    NOT NULL DEFAULT '',
                calibration_stage     TEXT    NOT NULL DEFAULT '',
                trust_tier            TEXT    NOT NULL DEFAULT '',
                schema_ver            INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_gtl_created
                ON ground_truth_labels(created_at_utc);

            CREATE INDEX IF NOT EXISTS idx_gtl_target
                ON ground_truth_labels(label_target, label_value);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyPromptEventsV21(SqliteConnection connection)
    {
        // Phase 1b — anti-spam: record every ACTIVE prompt Ari SHOWS (not only the ones the user
        // answers), so the governor's daily cap and min-interval cooldown engage on SHOWN prompts
        // and can back off progressively when the user keeps ignoring them. Without this the cadence
        // was read from ground_truth_labels — which only has rows on an ANSWER — so an ignored prompt
        // left the cadence at 0 and Ari re-asked every poll cycle (the "spam" bug).
        //
        // Rows are tiny (a handful/day) and self-contained. Unlike ground_truth_labels this table is
        // NOT needed for the future model, so it is fine for the 14-day prune to trim old rows; the
        // governor only looks back 24h. It IS wiped on local-data reset (privacy).
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS prompt_events (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                prompt_id       TEXT    NOT NULL DEFAULT '',
                created_at_utc  TEXT    NOT NULL,
                label_target    TEXT    NOT NULL DEFAULT '',
                prompt_kind     TEXT    NOT NULL DEFAULT '',
                outcome         TEXT    NOT NULL DEFAULT 'shown',
                resolved_at_utc TEXT    NOT NULL DEFAULT '',
                schema_ver      INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_prompt_events_created
                ON prompt_events(created_at_utc);

            CREATE INDEX IF NOT EXISTS idx_prompt_events_target_outcome
                ON prompt_events(label_target, outcome, created_at_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyRemindersV22(SqliteConnection connection)
    {
        // Phase 2c — user reminders ("standup in 20m"). Lives in the SERVICE so reminders
        // survive user-agent restarts, widget crashes, and agent updates. Rows are tiny and
        // user-authored; text never leaves the device. done/dismissed rows are cleaned up
        // after 30 days by ReadNextReminder's piggyback cleanup (NOT the 14-day raw prune,
        // so a reminder set 3 weeks out still fires). Wiped on local-data reset (privacy).
        //
        // state: pending (waiting) | fired (due, awaiting user ack) | done | dismissed.
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS reminders (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                title           TEXT    NOT NULL,
                due_at_utc      TEXT    NOT NULL,
                created_at_utc  TEXT    NOT NULL,
                state           TEXT    NOT NULL DEFAULT 'pending',
                snooze_count    INTEGER NOT NULL DEFAULT 0,
                fired_at_utc    TEXT    NOT NULL DEFAULT '',
                schema_ver      INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_reminders_state_due
                ON reminders(state, due_at_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyTasksV23(SqliteConnection connection)
    {
        // Workstream 4 — timed tasks ("plan vs. reality"). NOT a to-do app: each task holds
        // the user's plan (title + planned duration) against measured reality (focused /
        // off-task seconds attributed from the bucket pipeline while the task is active).
        // Lives in the SERVICE like reminders so tasks survive user-agent restarts. Titles
        // are user-authored and never leave the device except in the digest payload the
        // user explicitly receives. Wiped on local-data reset (privacy).
        //
        // state: pending | active (at most ONE — enforced in the store) | done | abandoned.
        // active_seconds: attended wall time while active (away buckets contribute nothing),
        // drives the countdown and expiry; focused/offtask split it by bucket classification.
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at_utc    TEXT    NOT NULL,
                title             TEXT    NOT NULL,
                planned_seconds   INTEGER NOT NULL,
                state             TEXT    NOT NULL DEFAULT 'pending',
                order_index       INTEGER NOT NULL DEFAULT 0,
                started_at_utc    TEXT    NOT NULL DEFAULT '',
                ended_at_utc      TEXT    NOT NULL DEFAULT '',
                active_seconds    REAL    NOT NULL DEFAULT 0,
                focused_seconds   REAL    NOT NULL DEFAULT 0,
                offtask_seconds   REAL    NOT NULL DEFAULT 0,
                extended_seconds  INTEGER NOT NULL DEFAULT 0,
                schema_ver        INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_tasks_state_created
                ON tasks(state, created_at_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    // Workstream D — conversational check-ins. One row per local "goal day" (the 5:00-boundary
    // day, keyed by local_date): the morning stated goal + tracking preference, the midday
    // answer (yes/partly/not_yet), and the end-of-day marker + tomorrow's plan. Lives in the
    // SERVICE like tasks/reminders so it survives user-agent restarts; user-authored, wiped on
    // local-data reset (privacy). The goal + midday answer double as Phase-1b ground-truth labels.
    /// <summary>
    /// Complaint 3 (2026-08-11) — the close-out card's "Tomorrow" has to STICK. A card that asks
    /// what moves and then forgets is the thing this project exists to fix: a commitment nobody
    /// follows up on is a note, not a commitment.
    ///
    /// One column on the existing local tasks table. Holds a goal-day key (yyyy-MM-dd): the first
    /// day this task is "back". Empty = not deferred, which is every existing row, so the migration
    /// is a no-op for anything already stored.
    ///
    /// A DAY rather than a flag, deliberately: it self-expires. Tomorrow the key is no longer in the
    /// future, the task is an ordinary open task again, and the morning card proposes it. No cleanup
    /// job, and no flag that can go stale and hide work forever.
    ///
    /// Local only. Nothing is added to the digest or narrative payloads, so deferring a task does
    /// not send anything new off the machine.
    /// </summary>
    internal static void ApplyTasksDeferV25(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "tasks", "deferred_until_day", "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>
    /// Step 9a (2026-08-12) — projects. A real thing a task belongs to, picked by the user and
    /// never guessed by Ari. Untagged is a valid, permanent state, which is why the task column
    /// defaults to 0 rather than being nullable: every existing row is already correct and no
    /// caller anywhere has to handle a null.
    ///
    /// The unique index on name is NOT housekeeping. "Nolan" typed twice, or "Nolan" and "nolan",
    /// would split one client's total in half — the invoice corruption this feature exists to
    /// prevent, arriving through the back door. NOCASE makes the collision impossible to create.
    ///
    /// LOCAL ONLY in 9a. No project field is added to the digest or narrative payloads, so tagging
    /// a task sends nothing new off the machine. That is the whole point of shipping 9a first.
    /// </summary>
    internal static void ApplyProjectsV26(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS projects (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT    NOT NULL,
                created_at_utc  TEXT    NOT NULL,
                archived_at_utc TEXT    NOT NULL DEFAULT '',
                order_index     INTEGER NOT NULL DEFAULT 0,
                schema_ver      INTEGER NOT NULL DEFAULT 1
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_projects_name
                ON projects(name COLLATE NOCASE);
            """;
        cmd.ExecuteNonQuery();

        EnsureColumnExists(connection, "tasks", "project_id", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>
    /// 2026-08-12 — the hourly app-usage rollup. The one table that lets a project's picture
    /// outlive the five-second firehose.
    ///
    /// WHY IT EXISTS: per-app time comes from <c>input_signal_windows</c>, which is pruned at 14
    /// days and costs ~32 MB/day across the capture tables. Keeping months of that is roughly
    /// 12 GB/year and not an option. This rollup answers the same questions at ~15 KB/day.
    ///
    /// KEYED BY TASK, NOT PROJECT, deliberately. Re-tagging a task is allowed at any time
    /// (Yadin, 2026-08-12) and the projects page is current truth. Storing project_id here would
    /// freeze last month's rows at whatever the tag was then, and rebuilding them is only possible
    /// while the source buckets survive — 14 days. Resolving tasks.project_id at READ time makes a
    /// re-tag instant, total, and permanent, with no rebuild. Same rule as the digest summing
    /// BY PROJECT from the tasks it already carries rather than keeping its own totals.
    ///
    /// KEYED BY UTC HOUR, converted at read time — the same contract ReadDayTimeline already uses.
    /// The local hour is a local concept and the store holds UTC; deriving it at write time would
    /// bake one machine's offset into history that outlives it.
    ///
    /// task_id 0 = time not on any task, which is most of a normal day and all of the backfill.
    /// Away time is absent entirely, mirroring AttributeBucketToActiveTask: away is not an app.
    ///
    /// backfilled = 1 marks rows reconstructed from input_signal_windows rather than measured
    /// live. Those carry active_seconds ONLY — the attention split and the active task are not
    /// recoverable after the fact, and a zero there must never be read as a measured zero.
    /// </summary>
    internal static void ApplyAppUsageRollupV27(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS app_usage_rollup (
                hour_start_utc  TEXT    NOT NULL,
                task_id         INTEGER NOT NULL DEFAULT 0,
                app_hash        TEXT    NOT NULL,
                focused_seconds REAL    NOT NULL DEFAULT 0,
                offtask_seconds REAL    NOT NULL DEFAULT 0,
                active_seconds  REAL    NOT NULL DEFAULT 0,
                backfilled      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (hour_start_utc, task_id, app_hash)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_app_usage_rollup_task
                ON app_usage_rollup(task_id);
            """;
        cmd.ExecuteNonQuery();

        BackfillAppUsageRollup(connection);
    }

    /// <summary>
    /// One-shot reconstruction from whatever <c>input_signal_windows</c> still holds — 14 days at
    /// most, and it slides by a day every day. Runs inside the v27 migration so it happens exactly
    /// once, on the only data that will ever be available for those days.
    ///
    /// It fills the APP dimension only. task_id is 0 for every backfilled row and that is not a
    /// shortcut: input_signal_windows has no task column, and tasks record only their FIRST start
    /// and their close, so a task switched away from and resumed looks continuous. Attributing app
    /// time across that span would invent the exact kind of number a client's invoice must never
    /// contain. The project dimension genuinely starts the day projects did.
    /// </summary>
    private static void BackfillAppUsageRollup(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO app_usage_rollup
                (hour_start_utc, task_id, app_hash, focused_seconds, offtask_seconds, active_seconds, backfilled)
            SELECT
                substr(window_start_utc, 1, 13) || ':00:00.0000000+00:00' AS hour_start_utc,
                0,
                focus_app_hash,
                0,
                0,
                SUM(
                    MAX(0,
                        (julianday(window_end_utc) - julianday(window_start_utc)) * 86400.0)),
                1
            FROM input_signal_windows
            WHERE focus_app_hash <> ''
              AND window_end_utc > window_start_utc
            GROUP BY hour_start_utc, focus_app_hash
            ON CONFLICT(hour_start_utc, task_id, app_hash) DO NOTHING;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Step 10 phase A (2026-08-13) — the report's data. Five additive, defaulted columns, and
    /// <b>NOTHING here leaves the machine</b>: no payload gains a field in this phase, which is
    /// what makes it shippable on its own.
    ///
    /// WHY CLIENT-FACING NAMES EXIST AT ALL. The real task titles on this machine are
    /// <c>finish my writting part "all the way in"</c> and <c>working on appolo leads</c> — private
    /// notes with typos, and a title can just as easily name a different client or the reason a day
    /// went badly. None of that belongs in a paying client's inbox, so the user gives the line a
    /// client-facing label once and it is remembered forever (Step10_Report_Spec.md §3).
    ///
    /// <c>projects.client_name</c> is the same idea for the project itself, and it was nearly
    /// missed: §4 puts the project name in the report header, in the largest text on the page,
    /// while the name is typed on a morning card in the same hurry as everything else.
    ///
    /// <c>label_style</c> 0 = use the raw title, 1 = ask for a client-facing label. It decides only
    /// what a NEW task starts as. A label the user has written is never overwritten by this setting
    /// changing later — see SetProjectLabelStyle.
    ///
    /// THE RATE IS MINOR UNITS AND AN INTEGER, deliberately. Money in a float is a rounding error
    /// waiting to appear next to hours this product exists to make defensible. 0 = no rate set.
    /// §7: it is shown only to the user in Control Center and <b>must never appear in any payload,
    /// in any form</b> — a document carrying a money total starts being treated as an invoice.
    ///
    /// Empty string means "not set" and 0 means "no rate", matching deferred_until_day and
    /// project_id: no nullable columns, so no caller anywhere has to handle a null.
    /// </summary>
    internal static void ApplyReportLabelsV28(SqliteConnection connection)
    {
        EnsureColumnExists(connection, "tasks", "client_label", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "projects", "client_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "projects", "label_style", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "projects", "rate_minor", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "projects", "rate_currency", "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>
    /// Step 10 phase C/D (2026-08-14) — the address book, and the local record of what was sent.
    ///
    /// <b>The address book is LOCAL ONLY (§12.4).</b> It is a list of other people's names and
    /// addresses; only the one address on the report being sent ever leaves. A server-side book would
    /// mean one breach spills every user's client list, and it buys convenience across machines that
    /// is worth nothing at one user.
    ///
    /// <b>The sent record exists so a challenge can be answered (§5).</b> It holds exactly what was
    /// sent — the labels, the figures, the recipient, the timestamp — because the draft is derived
    /// and re-deriving it tomorrow may legitimately give a different answer once work is re-tagged.
    /// A sent report is a snapshot and is never regenerated.
    ///
    /// <b>Why the lines and days are copied rather than referenced.</b> Pointing at <c>tasks</c>
    /// would mean a re-tag or a rename silently rewrites history that has already been sent to a
    /// client, which is the one thing this table exists to prevent. Rows here are dead copies on
    /// purpose.
    ///
    /// <c>server_state</c> is how "delete on both sides" stays honest (§12.6.5): a local row whose
    /// server half has not been confirmed deleted is still LISTED AS PRESENT rather than reported as
    /// gone. Never report success on a half-delete.
    /// </summary>
    internal static void ApplyReportSendingV29(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS report_recipients (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                name           TEXT    NOT NULL DEFAULT '',
                email          TEXT    NOT NULL,
                created_at_utc TEXT    NOT NULL,
                last_used_utc  TEXT    NOT NULL DEFAULT '',
                schema_ver     INTEGER NOT NULL DEFAULT 1
            );

            -- Same reasoning as idx_projects_name: two rows differing only in case are one client
            -- split in two, and here it would also mean the address book quietly holds duplicates
            -- of a third party's address.
            CREATE UNIQUE INDEX IF NOT EXISTS idx_report_recipients_email
                ON report_recipients(email COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS sent_reports (
                report_id         TEXT    PRIMARY KEY,
                project_id        INTEGER NOT NULL DEFAULT 0,
                project_name      TEXT    NOT NULL DEFAULT '',
                client_name       TEXT    NOT NULL DEFAULT '',
                range_start_local TEXT    NOT NULL,
                range_end_local   TEXT    NOT NULL,
                recipient_name    TEXT    NOT NULL DEFAULT '',
                recipient_email   TEXT    NOT NULL,
                -- What the client actually saw in the From and Reply-to lines, as resolved at send
                -- time. Recorded, never sent: the payload carries no sender fields (§12.2), but a
                -- dispute asks "who did this appear to come from?" and the answer has to exist.
                from_name         TEXT    NOT NULL DEFAULT '',
                reply_to          TEXT    NOT NULL DEFAULT '',
                total_hours       REAL    NOT NULL DEFAULT 0,
                days_worked       INTEGER NOT NULL DEFAULT 0,
                lines_finished    INTEGER NOT NULL DEFAULT 0,
                lines_total       INTEGER NOT NULL DEFAULT 0,
                requested_at_utc  TEXT    NOT NULL,
                -- Written by the server and stored back on success. Empty until then, which is what
                -- makes a crash mid-send visible instead of silent.
                sent_at_utc       TEXT    NOT NULL DEFAULT '',
                -- pending | sent | failed. 'pending' means recorded-but-unconfirmed, and it exists
                -- because the record is written BEFORE the attempt.
                send_state        TEXT    NOT NULL DEFAULT 'pending',
                send_error        TEXT    NOT NULL DEFAULT '',
                -- present | delete_pending. A local delete whose server half failed stays
                -- delete_pending and the report stays listed. §12.6.5.
                server_state      TEXT    NOT NULL DEFAULT 'present',
                schema_ver        INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_sent_reports_requested
                ON sent_reports(requested_at_utc);

            CREATE TABLE IF NOT EXISTS sent_report_lines (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                report_id    TEXT    NOT NULL,
                line_id      TEXT    NOT NULL,
                client_label TEXT    NOT NULL,
                finished     INTEGER NOT NULL DEFAULT 0,
                hours        REAL    NOT NULL DEFAULT 0,
                order_index  INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_sent_report_lines_report
                ON sent_report_lines(report_id);

            CREATE TABLE IF NOT EXISTS sent_report_days (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                report_id   TEXT    NOT NULL,
                date_local  TEXT    NOT NULL,
                hours       REAL    NOT NULL DEFAULT 0,
                order_index INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_sent_report_days_report
                ON sent_report_days(report_id);
            """;
        cmd.ExecuteNonQuery();
    }

    internal static void ApplyDailyGoalsV24(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS daily_goals (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                local_date     TEXT    NOT NULL,
                created_at_utc TEXT    NOT NULL,
                goal_text      TEXT    NOT NULL DEFAULT '',
                tracking_pref  TEXT    NOT NULL DEFAULT '',
                midday_answer  TEXT    NOT NULL DEFAULT '',
                midday_at_utc  TEXT    NOT NULL DEFAULT '',
                eod_at_utc     TEXT    NOT NULL DEFAULT '',
                plan_tomorrow  TEXT    NOT NULL DEFAULT '',
                schema_ver     INTEGER NOT NULL DEFAULT 1
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_daily_goals_local_date
                ON daily_goals(local_date);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Phase 0.1 + 1.1 + 1.2 (2026-08-17) — everything the client report needs recorded, in one
    /// step because they ship together and a half-applied set is worse than either half.
    ///
    /// <b>task_day_hours is the one that cannot wait.</b> The report's main chart splits each day
    /// by the task the hours went to, and that cross is not stored anywhere honest today.
    /// <c>app_usage_rollup</c> looks like it holds it — it is keyed
    /// <c>(hour_start_utc, task_id, app_hash)</c> — but <c>AccumulateAppUsageRollup</c> returns
    /// early on a bucket with no identifiable application, so it is LOSSY by an unmeasured amount.
    /// That loss is already visible as <c>unattributed_hours</c> on the Reports page. A per-day
    /// split derived from it would not add up to the per-task totals the widget shows, which is
    /// exactly the kind of number a client's invoice must never contain.
    ///
    /// So this table records the same buckets with NO app condition, in the same transaction as
    /// the attribution itself, and it therefore cannot disagree with <c>tasks.active_seconds</c>.
    ///
    /// <b>No backfill, deliberately.</b> The days before this ships have no honest per-day split,
    /// and reconstructing one from the lossy rollup would invent it. The day dimension genuinely
    /// begins today, the same way the project dimension genuinely began on 2026-08-12 (§14).
    /// </summary>
    internal static void ApplyReportContentV30(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            -- 0.1 — hours per task, per DAY. Local only.
            --
            -- Keyed by local_date, not a UTC hour: the chart's unit is a day, and in this product a
            -- day is the 05:00-boundary goal day that daily_goals and tasks.deferred_until_day
            -- already use. A UTC key would let the report's days disagree with the check-in cards'
            -- days at the boundary, and a client reading "you worked Tuesday" must mean the same
            -- Tuesday the user remembers.
            --
            -- No project_id, for the reason app_usage_rollup has none: re-tagging a task must stay
            -- instant and total, so the project is resolved from tasks.project_id at READ time.
            CREATE TABLE IF NOT EXISTS task_day_hours (
                local_date       TEXT    NOT NULL,
                task_id          INTEGER NOT NULL,
                active_seconds   REAL    NOT NULL DEFAULT 0,
                focused_seconds  REAL    NOT NULL DEFAULT 0,
                offtask_seconds  REAL    NOT NULL DEFAULT 0,
                schema_ver       INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (local_date, task_id)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_task_day_hours_task
                ON task_day_hours(task_id);
            """;
        cmd.ExecuteNonQuery();

        // 1.1 — the task record grows.
        //
        // status is a SEPARATE axis from the existing `state`, not a replacement for it. `state`
        // is execution — pending / active / done / abandoned, where 'active' means the timer is
        // running right now. `status` is where the task stands for a reader: a task can be "in
        // progress" all week while its timer is stopped. Collapsing the two would make the widget
        // and the report fight over one column.
        EnsureColumnExists(connection, "tasks", "status", "TEXT NOT NULL DEFAULT 'planned'");
        EnsureColumnExists(connection, "tasks", "completed_at_utc", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "tasks", "expected_finish_day", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "tasks", "result_line", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "tasks", "blocked_reason", "TEXT NOT NULL DEFAULT ''");

        // The SLOT INDEX, never the hex. The palette belongs to the renderer so it can be changed
        // in one place; a stored hex would freeze today's palette into every historical task.
        // 1-8 assigned in fixed order within a project and never recycled; 0 means no slot, which
        // the renderer folds into the neutral "Other" slice.
        EnsureColumnExists(connection, "tasks", "colour_slot", "INTEGER NOT NULL DEFAULT 0");

        // Seed status from the execution state that already exists, so tasks recorded before today
        // do not all read as "planned" in the first report.
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                """
                UPDATE tasks SET status =
                    CASE state
                        WHEN 'done'   THEN 'done'
                        WHEN 'active' THEN 'in_progress'
                        ELSE 'planned'
                    END
                WHERE status = 'planned';

                -- completed_at is a fact we already hold for closed tasks.
                UPDATE tasks SET completed_at_utc = ended_at_utc
                WHERE state = 'done' AND ended_at_utc <> '' AND completed_at_utc = '';
                """;
            seed.ExecuteNonQuery();
        }

        // 1.2 — the project record grows. recipients is a JSON array of addresses and is LOCAL
        // ONLY (§12.4): the address book never leaves the machine, and only the one address on a
        // report being sent ever does.
        EnsureColumnExists(connection, "projects", "status", "TEXT NOT NULL DEFAULT 'active'");
        EnsureColumnExists(connection, "projects", "start_day", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "projects", "completion_day", "TEXT NOT NULL DEFAULT ''");
        EnsureColumnExists(connection, "projects", "recipients", "TEXT NOT NULL DEFAULT ''");

        // Phase 3.2 (2026-08-18) — how often this project is reported on. A statement of intent,
        // read by the Projects page and by nothing else: no scheduler reads it and none ever will,
        // because the report never sends automatically. Additive with a default, so every existing
        // row keeps working and nothing already recorded changes value.
        EnsureColumnExists(connection, "projects", "reporting_frequency", "TEXT NOT NULL DEFAULT 'none'");
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
