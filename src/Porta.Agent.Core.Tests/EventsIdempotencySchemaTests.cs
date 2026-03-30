using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Db;
using System.Text.Json;
using Porta.Agent.LocalApi;

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

    [Fact]
    public void Migrate_CreatesLatestInterventionLookupIndex()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Migrations.Migrate(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA index_list('interventions');";
        using SqliteDataReader reader = cmd.ExecuteReader();

        var indexNames = new List<string>();
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(1));
        }

        Assert.Contains("ix_interventions_delivery_latest", indexNames);
    }

    [Fact]
    public void LocalApiHealthResponse_UsesStableSnakeCaseJson()
    {
        var dto = new LocalApiHealthResponse(
            Alive: true,
            TimestampUtc: DateTimeOffset.Parse("2026-03-30T16:00:00Z"),
            ProcessId: 1234,
            AgentVersion: "1.0.25");

        string json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"alive\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"timestamp_utc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"process_id\":1234", json, StringComparison.Ordinal);
        Assert.Contains("\"agent_version\":\"1.0.25\"", json, StringComparison.Ordinal);

        LocalApiHealthResponse? roundTrip = JsonSerializer.Deserialize<LocalApiHealthResponse>(json);
        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.Alive);
        Assert.Equal(1234, roundTrip.ProcessId);
        Assert.Equal("1.0.25", roundTrip.AgentVersion);
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
