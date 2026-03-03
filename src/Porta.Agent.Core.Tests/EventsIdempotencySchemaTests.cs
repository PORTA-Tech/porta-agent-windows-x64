using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Core.Tests;

public sealed class EventsIdempotencySchemaTests
{
    [Fact]
    public void Migrate_EnforcesUniqueEventsIdempotencyKey()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Migrations.Migrate(connection);

        InsertEvent(connection, idempotencyKey: "dup_key_v1");
        var ex = Assert.Throws<SqliteException>(() => InsertEvent(connection, idempotencyKey: "dup_key_v1"));
        Assert.True(ex.SqliteErrorCode == 19 || ex.SqliteExtendedErrorCode == 2067);
    }

    private static void InsertEvent(SqliteConnection connection, string idempotencyKey)
    {
        using var cmd = connection.CreateCommand();
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
                $ts, $ts, $action, NULL, $why, $why_json, $escalation, 0, $trigger_state, $trigger_value, $confidence, $contributors_json, $state_flags_json, $idempotency_key,
                $intervention_id, $policy_id, $domain, '', '', '{}', '', '', 0, '', 'activated'
            );
            """;
        cmd.Parameters.AddWithValue("$ts", "2026-03-03T00:00:00.000Z");
        cmd.Parameters.AddWithValue("$action", "break_suggestion");
        cmd.Parameters.AddWithValue("$why", "test");
        cmd.Parameters.AddWithValue("$why_json", "{\"schema_ver\":1}");
        cmd.Parameters.AddWithValue("$escalation", "Suggest");
        cmd.Parameters.AddWithValue("$trigger_state", "overload");
        cmd.Parameters.AddWithValue("$trigger_value", 0.9d);
        cmd.Parameters.AddWithValue("$confidence", 0.9d);
        cmd.Parameters.AddWithValue("$contributors_json", "{\"schema_ver\":1,\"contributors\":[]}");
        cmd.Parameters.AddWithValue("$state_flags_json", "{\"schema_ver\":1}");
        cmd.Parameters.AddWithValue("$idempotency_key", idempotencyKey);
        cmd.Parameters.AddWithValue("$intervention_id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$policy_id", "break-policy-v1");
        cmd.Parameters.AddWithValue("$domain", "fatigue");
        cmd.ExecuteNonQuery();
    }
}
