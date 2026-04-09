using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

public sealed class InterventionEventFilteringTests
{
    [Fact]
    public void ReadRecentEvents_HidesLegacyNoneActions_AndWriteControlEventSkipsNoOpActions()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-event-filter-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: nowUtc,
                Action: "break_suggestion",
                TriggerState: "fatigue",
                TriggerValue: 0.8,
                Confidence: 0.9,
                DurationSeconds: 300,
                EscalationLevel: "Suggest",
                Reversible: false,
                Reason: "real_action",
                Contributors: ["fatigue"],
                InterventionId: "break-1",
                PolicyId: "break-v1",
                Domain: "fatigue",
                PlannedEndTimestampUtc: nowUtc.AddMinutes(5),
                ActualEndTimestampUtc: null,
                PreviousSystemStateJson: "{\"action\":\"none\"}",
                WhatChanged: "Long break suggestion displayed.",
                HowToUndo: "Dismiss.",
                ReversalApplied: false,
                CooldownEndTimestampUtc: null,
                LifecycleState: "activated"));

            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: nowUtc.AddSeconds(1),
                Action: "none",
                TriggerState: "overload",
                TriggerValue: 0.3,
                Confidence: 0.5,
                DurationSeconds: null,
                EscalationLevel: "Inform",
                Reversible: false,
                Reason: "should_skip",
                Contributors: [],
                InterventionId: "none-1",
                PolicyId: "n/a",
                Domain: "control",
                PlannedEndTimestampUtc: null,
                ActualEndTimestampUtc: null,
                PreviousSystemStateJson: "{\"action\":\"none\"}",
                WhatChanged: "Action none applied.",
                HowToUndo: "No-op",
                ReversalApplied: false,
                CooldownEndTimestampUtc: null,
                LifecycleState: "activated"));

            InsertLegacyNoneEvent(dbPath, nowUtc.AddSeconds(2));

            IReadOnlyList<AgentControlEvent> recent = store.ReadRecentEvents(limit: 20);

            AgentControlEvent displayed = Assert.Single(recent);
            Assert.Equal("break_suggestion", displayed.Action);
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void InsertLegacyNoneEvent(string dbPath, DateTimeOffset tsUtc)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO events (
                timestamp_utc,
                action,
                why,
                escalation,
                reversible,
                trigger_state,
                trigger_value,
                confidence,
                contributors_json,
                intervention_id,
                policy_id,
                domain,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                lifecycle_state,
                ts_utc,
                idempotency_key,
                why_json,
                state_flags_json
            ) VALUES (
                $ts,
                'none',
                'legacy_none',
                'Inform',
                0,
                'overload',
                0.35,
                0.61,
                '{\"schema_ver\":1,\"contributors\":[]}',
                'legacy-none-1',
                'n/a',
                'control',
                '{\"action\":\"none\"}',
                'Action none applied.',
                'No-op',
                'activated',
                $ts,
                'legacy-none-1',
                '{\"schema_ver\":1,\"reason\":\"legacy_none\"}',
                '{\"schema_ver\":1}'
            );
            """;
        cmd.Parameters.AddWithValue("$ts", tsUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
