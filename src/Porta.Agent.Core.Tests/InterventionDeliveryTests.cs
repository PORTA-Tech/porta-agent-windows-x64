using Microsoft.Data.Sqlite;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

public sealed class InterventionDeliveryTests
{
    [Fact]
    public void PendingInterventions_MapsOrderedRows_ForSession()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        AgentPendingIntervention[] pending =
        [
            new(
                RowId: 3,
                InterventionId: "i-3",
                SessionId: "session-1",
                Action: "stretch_break_suggestion",
                CommandType: "show_stretch_break_toast",
                PayloadJson: "{}",
                DecidedAtUtc: nowUtc.AddSeconds(2),
                ExecutionState: "decided",
                PolicyId: "fatigue-v1",
                Domain: "fatigue",
                Confidence: 0.88d,
                EscalationLevel: "Suggest",
                WhatChanged: "Stretch break suggestion displayed"),
            new(
                RowId: 1,
                InterventionId: "i-1",
                SessionId: "session-1",
                Action: "break_suggestion",
                CommandType: "show_break_toast",
                PayloadJson: "{}",
                DecidedAtUtc: nowUtc,
                ExecutionState: "decided",
                PolicyId: "break-v1",
                Domain: "fatigue",
                Confidence: 0.81d,
                EscalationLevel: "Suggest",
                WhatChanged: "Break suggestion displayed")
        ];

        IReadOnlyList<Porta.Agent.LocalApi.LocalApiInterventionPendingResponse> mapped =
            InterventionMapper.MapPendingOrdered(pending);

        Assert.Equal(new[] { "i-1", "i-3" }, mapped.Select(static x => x.InterventionId).ToArray());
    }

    [Fact]
    public void Controller_PendingAndAck_RouteThroughStore()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var store = new LocalApiContractFakeStore
        {
            PendingInterventions =
            [
                new AgentPendingIntervention(
                    RowId: 1,
                    InterventionId: "intv-1",
                    SessionId: "session-1",
                    Action: "break_suggestion",
                    CommandType: "show_break_toast",
                    PayloadJson: "{\"title\":\"PORTA\",\"message\":\"Take a short break.\"}",
                    DecidedAtUtc: nowUtc,
                    ExecutionState: "decided",
                    PolicyId: "break-v1",
                    Domain: "fatigue",
                    Confidence: 0.84d,
                    EscalationLevel: "Suggest",
                    WhatChanged: "Break suggestion displayed")
            ]
        };

        var controller = new AgentCoreController(
            store,
            new FakeUserAgentSessionContextStore(
                new UserAgentSessionContextSnapshot(
                    SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                    CaptureContext: UserAgentCaptureContexts.UserSession,
                    ProcessId: 4321,
                    SessionId: 1,
                    UpdatedAtUtc: nowUtc,
                    AgentInstanceId: "agent-1")));

        IReadOnlyList<AgentPendingIntervention> pending = controller.GetPendingInterventions("session-1", limit: 10);
        Assert.Single(pending);
        Assert.Equal("show_break_toast", pending[0].CommandType);

        controller.AcknowledgeIntervention(new AgentInterventionAck(
            InterventionId: "intv-1",
            SessionId: "session-1",
            AgentInstanceId: "agent-1",
            Result: "applied",
            ErrorCode: null,
            AppliedAtUtc: nowUtc.AddSeconds(1)));

        Assert.Single(store.AckedInterventions);
        Assert.Equal("applied", store.AckedInterventions[0].Result);
    }

    [Fact]
    public void SqliteStore_QueuesSuggestionAsPending_AndAcksApplied()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-interventions-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            DateTimeOffset eventUtc = DateTimeOffset.UtcNow;
            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc,
                Action: "break_suggestion",
                TriggerState: "fatigue",
                TriggerValue: 0.82d,
                Confidence: 0.91d,
                DurationSeconds: 300,
                EscalationLevel: "Suggest",
                Reversible: false,
                Reason: "test",
                Contributors: ["test"],
                InterventionId: "break-1",
                PolicyId: "break-policy-v1",
                Domain: "fatigue",
                WhatChanged: "Break suggestion displayed to user."));

            IReadOnlyList<AgentPendingIntervention> pending = store.ReadPendingInterventions("session-9", 10);
            AgentPendingIntervention intervention = Assert.Single(pending);
            Assert.Equal("session-9", intervention.SessionId);
            Assert.Equal("show_break_toast", intervention.CommandType);
            Assert.Equal("decided", intervention.ExecutionState);

            bool acked = store.AcknowledgeIntervention(new AgentInterventionAck(
                InterventionId: "break-1",
                SessionId: "session-9",
                AgentInstanceId: "agent-9",
                Result: "applied",
                ErrorCode: null,
                AppliedAtUtc: eventUtc.AddSeconds(2)));
            Assert.True(acked);
            Assert.Empty(store.ReadPendingInterventions("session-9", 10));

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT execution_state, session_id, executor_agent_instance_id
                FROM interventions
                WHERE intervention_id = 'break-1'
                LIMIT 1;
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("applied", reader.GetString(0));
            Assert.Equal("session-9", reader.GetString(1));
            Assert.Equal("agent-9", reader.GetString(2));

            AgentControlEvent storedEvent = Assert.Single(store.ReadRecentEvents(10));
            Assert.Equal("break-1", storedEvent.InterventionId);
            Assert.Equal("show_break_toast", storedEvent.CommandType);
            Assert.Equal("applied", storedEvent.ExecutionState);
            Assert.Equal("agent-9", storedEvent.ExecutorAgentInstanceId);
            Assert.NotNull(storedEvent.AppliedAtUtc);
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

    [Fact]
    public void SqliteStore_QueuesNotificationSuppression_ForUserAgentAndTracksAckedState()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-notify-interventions-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            DateTimeOffset eventUtc = DateTimeOffset.UtcNow;
            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc,
                Action: "deep_work_notifications_suppressed",
                TriggerState: "deep_work",
                TriggerValue: 0.77d,
                Confidence: 0.77d,
                DurationSeconds: null,
                EscalationLevel: "Auto",
                Reversible: true,
                Reason: "queued",
                Contributors: [],
                InterventionId: "dw-1",
                PolicyId: "deep_work_notifications_v1",
                Domain: "output_action",
                WhatChanged: "Notification suppression requested for deep work protection.",
                HowToUndo: "Automatically restored when deep work exits."));

            AgentPendingIntervention pending = Assert.Single(store.ReadPendingInterventions("session-11", 10));
            Assert.Equal("suppress_notifications", pending.CommandType);
            Assert.Equal("decided", pending.ExecutionState);

            bool suppressAcked = store.AcknowledgeIntervention(new AgentInterventionAck(
                InterventionId: "dw-1",
                SessionId: "session-11",
                AgentInstanceId: "useragent-11",
                Result: "verified",
                ErrorCode: null,
                AppliedAtUtc: eventUtc.AddSeconds(2)));
            Assert.True(suppressAcked);

            AgentNotificationSuppressionStatus activeStatus = store.ReadNotificationSuppressionStatus();
            Assert.True(activeStatus.Active);
            Assert.Equal("verified_active", activeStatus.ActionState);

            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc.AddSeconds(30),
                Action: "deep_work_notifications_restored",
                TriggerState: "deep_work",
                TriggerValue: 0.45d,
                Confidence: 0.45d,
                DurationSeconds: null,
                EscalationLevel: "Auto",
                Reversible: true,
                Reason: "queued",
                Contributors: [],
                InterventionId: "dw-2",
                PolicyId: "deep_work_notifications_v1",
                Domain: "output_action",
                WhatChanged: "Notification restore requested after deep work ended.",
                HowToUndo: "No manual undo needed."));

            AgentPendingIntervention restorePending = Assert.Single(store.ReadPendingInterventions("session-11", 10));
            Assert.Equal("restore_notifications", restorePending.CommandType);

            bool restoreAcked = store.AcknowledgeIntervention(new AgentInterventionAck(
                InterventionId: "dw-2",
                SessionId: "session-11",
                AgentInstanceId: "useragent-11",
                Result: "verified",
                ErrorCode: null,
                AppliedAtUtc: eventUtc.AddSeconds(35)));
            Assert.True(restoreAcked);

            AgentNotificationSuppressionStatus inactiveStatus = store.ReadNotificationSuppressionStatus();
            Assert.False(inactiveStatus.Active);
            Assert.Equal("verified_inactive", inactiveStatus.ActionState);
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

    [Fact]
    public void SqliteStore_MapsVerifiedExecutionState_IntoRecentEvents()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-verified-interventions-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            DateTimeOffset eventUtc = DateTimeOffset.UtcNow;
            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc,
                Action: "deep_work_notifications_suppressed",
                TriggerState: "deep_work",
                TriggerValue: 0.77d,
                Confidence: 0.77d,
                DurationSeconds: null,
                EscalationLevel: "Auto",
                Reversible: true,
                Reason: "queued",
                Contributors: [],
                InterventionId: "dw-verified",
                PolicyId: "deep_work_notifications:local-default",
                Domain: "output_action",
                WhatChanged: "Notification suppression requested for deep work protection.",
                HowToUndo: "Automatically restored when deep work exits."));

            bool acked = store.AcknowledgeIntervention(new AgentInterventionAck(
                InterventionId: "dw-verified",
                SessionId: "session-11",
                AgentInstanceId: "useragent-11",
                Result: "verified",
                ErrorCode: null,
                AppliedAtUtc: eventUtc.AddSeconds(1)));

            Assert.True(acked);
            AgentControlEvent ev = Assert.Single(store.ReadRecentEvents(10));
            Assert.Equal("verified", ev.ExecutionState);
            Assert.NotNull(ev.AppliedAtUtc);
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

    private sealed class FakeUserAgentSessionContextStore : IUserAgentSessionContextStore
    {
        private readonly UserAgentSessionContextSnapshot? _snapshot;

        public FakeUserAgentSessionContextStore(UserAgentSessionContextSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public UserAgentSessionContextSnapshot? ReadCurrent() => _snapshot;

        public void WriteCurrent(UserAgentSessionContextSnapshot snapshot)
        {
        }
    }
}
