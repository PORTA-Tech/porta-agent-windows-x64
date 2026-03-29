using System.Text.Json;
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

            using var verifyConnection = new SqliteConnection($"Data Source={dbPath}");
            verifyConnection.Open();
            using var cmd = verifyConnection.CreateCommand();
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
    public void SqliteStore_AcceptsPromptOutcomes_AsTerminalExecutionStates()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-interventions-accepted-{Guid.NewGuid():N}.db");
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
                InterventionId: "break-accepted-1",
                PolicyId: "break-policy-v1",
                Domain: "fatigue",
                WhatChanged: "Break suggestion displayed to user."));

            bool acked = store.AcknowledgeIntervention(new AgentInterventionAck(
                InterventionId: "break-accepted-1",
                SessionId: "session-9",
                AgentInstanceId: "agent-9",
                Result: "accepted",
                ErrorCode: null,
                AppliedAtUtc: eventUtc.AddSeconds(2)));

            Assert.True(acked);
            Assert.Empty(store.ReadPendingInterventions("session-9", 10));

            AgentControlEvent storedEvent = Assert.Single(store.ReadRecentEvents(10));
            Assert.Equal("accepted", storedEvent.ExecutionState);
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
    public void SqliteStore_DeactivatedSuggestion_DoesNotQueueSecondToast()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-interventions-deactivated-{Guid.NewGuid():N}.db");
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
                InterventionId: "break-repeat-1",
                PolicyId: "break-policy-v1",
                Domain: "fatigue",
                WhatChanged: "Break suggestion displayed to user."));

            bool acked = store.AcknowledgeIntervention(new AgentInterventionAck(
                InterventionId: "break-repeat-1",
                SessionId: "session-9",
                AgentInstanceId: "agent-9",
                Result: "applied",
                ErrorCode: null,
                AppliedAtUtc: eventUtc.AddSeconds(2)));
            Assert.True(acked);

            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc.AddSeconds(5),
                Action: "break_suggestion",
                TriggerState: "overload",
                TriggerValue: 0.25d,
                Confidence: 0.85d,
                DurationSeconds: 5,
                EscalationLevel: "Inform",
                Reversible: true,
                Reason: "deactivated_due_to:monitor_only",
                Contributors: ["recovery", "auto_reversal"],
                InterventionId: "break-repeat-1",
                PolicyId: "break-policy-v1",
                Domain: "fatigue",
                WhatChanged: "Break suggestion no longer active.",
                HowToUndo: "No-op",
                ReversalApplied: true,
                ActualEndTimestampUtc: eventUtc.AddSeconds(5),
                LifecycleState: "deactivated"));

            Assert.Empty(store.ReadPendingInterventions("session-9", 10));

            using var verifyConnection = new SqliteConnection($"Data Source={dbPath}");
            verifyConnection.Open();
            using var cmd = verifyConnection.CreateCommand();
            cmd.CommandText =
                """
                SELECT COUNT(*)
                FROM interventions
                WHERE intervention_id = 'break-repeat-1';
                """;
            long interventionRows = (long)(cmd.ExecuteScalar() ?? 0L);
            Assert.Equal(1L, interventionRows);
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
    public void SqliteStore_QueuesInformAction_AsStatusToast()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-status-interventions-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            DateTimeOffset eventUtc = DateTimeOffset.UtcNow;
            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc,
                Action: "inform_overload_risk",
                TriggerState: "overload",
                TriggerValue: 0.72d,
                Confidence: 0.87d,
                DurationSeconds: 60,
                EscalationLevel: "Inform",
                Reversible: false,
                Reason: "test",
                Contributors: ["test"],
                InterventionId: "inform-1",
                PolicyId: "overload-relief-v1",
                Domain: "overload",
                WhatChanged: "Overload risk logged for transparency; no direct system setting changed.",
                HowToUndo: "No undo required; this action is informational only."));

            AgentPendingIntervention pending = Assert.Single(store.ReadPendingInterventions("session-13", 10));
            Assert.Equal("show_status_toast", pending.CommandType);
            Assert.Equal("Overload watch", ReadPayloadString(pending, "title"));
            Assert.Equal(
                "Ari sees overload rising, but the signal has not stayed high long enough to change anything yet. Slow down or open Now for details. You can mute this guidance in Settings.",
                ReadPayloadString(pending, "message"));
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
    public void SqliteStore_QueuesDeepWorkSuppression_WithUserFacingToastPayload()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-deepwork-payload-{Guid.NewGuid():N}.db");
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
                TriggerValue: 0.88d,
                Confidence: 0.88d,
                DurationSeconds: null,
                EscalationLevel: "Auto",
                Reversible: true,
                Reason: "queued",
                Contributors: [],
                InterventionId: "dw-payload-1",
                PolicyId: "deep_work_notifications_v1",
                Domain: "output_action",
                WhatChanged: "Notification suppression requested for deep work protection.",
                HowToUndo: "Automatically restored when deep work exits."));

            AgentPendingIntervention pending = Assert.Single(store.ReadPendingInterventions("session-14", 10));
            Assert.Equal("suppress_notifications", pending.CommandType);
            Assert.Equal("Deep work notifications", ReadPayloadString(pending, "title"));
            Assert.Equal(
                "Ari detected a sustained focus block and muted Windows notification banners to reduce interruptions. Keep this block going or restore notifications when you are ready. You can turn this feature off in Settings.",
                ReadPayloadString(pending, "message"));
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
    public void SqliteStore_QueuesSuggestionPayload_WithPromptMetadata()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-suggestion-prompt-payload-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            DateTimeOffset eventUtc = DateTimeOffset.UtcNow;
            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc,
                Action: "focus_lock_in_suggestion",
                TriggerState: "focus",
                TriggerValue: 0.68d,
                Confidence: 0.89d,
                DurationSeconds: null,
                EscalationLevel: "Suggest",
                Reversible: false,
                Reason: "test",
                Contributors: ["test"],
                InterventionId: "focus-prompt-1",
                PolicyId: "focus-v1",
                Domain: "focus",
                WhatChanged: "Focus lock-in suggestion displayed to user."));

            AgentPendingIntervention pending = Assert.Single(store.ReadPendingInterventions("session-21", 10));
            Assert.Equal("Focus suggestion", ReadPayloadString(pending, "title"));
            Assert.Equal(
                "Ari sees steady focus building and thinks staying on one task will help. Keep this task in front of you for a bit? Choose Later if not now, or turn off this tip.",
                ReadPayloadString(pending, "message"));
            Assert.Equal("focus_lock_in_suggestion", ReadPayloadString(pending, "tip_key"));
            Assert.Equal("choice_prompt", ReadPayloadString(pending, "interaction_kind"));
            Assert.Equal("Yes", ReadPayloadString(pending, "accept_label"));
            Assert.Equal("No", ReadPayloadString(pending, "dismiss_label"));
            Assert.Equal("Later", ReadPayloadString(pending, "snooze_label"));
            Assert.Equal("Turn off this tip", ReadPayloadString(pending, "suppress_label"));
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
    public void SqliteStore_QueuesOverloadRestriction_WithPlainLanguageCopy()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-overload-restrict-payload-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            DateTimeOffset eventUtc = DateTimeOffset.UtcNow;
            store.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: eventUtc,
                Action: "temporary_app_restriction_enabled",
                TriggerState: "overload",
                TriggerValue: 0.83d,
                Confidence: 0.92d,
                DurationSeconds: 180,
                EscalationLevel: "Restrict",
                Reversible: true,
                Reason: "test",
                Contributors: ["test"],
                InterventionId: "overload-restrict-1",
                PolicyId: "overload-relief-v1",
                Domain: "overload",
                WhatChanged: "Temporary app restriction became active.",
                HowToUndo: "Open Settings to disable temporary app restriction."));

            AgentPendingIntervention pending = Assert.Single(store.ReadPendingInterventions("session-31", 10));
            Assert.Equal("show_status_toast", pending.CommandType);
            Assert.Equal("Temporary app limit", ReadPayloadString(pending, "title"));
            Assert.Equal(
                "Ari saw the risk stay high long enough to limit distracting apps for a short block. Stay on task or open Settings to change this feature. You can turn it off in Settings.",
                ReadPayloadString(pending, "message"));
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
    public void SqliteStore_DoesNotReturnSupersededOrExpiredBreakSuggestions_AsPending()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-interventions-stale-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                InsertInterventionRow(
                    connection,
                    interventionId: "break-stale-1",
                    timestampUtc: "2026-03-20T09:38:45.000Z",
                    decidedAtUtc: "2026-03-20T09:38:45.000Z",
                    plannedEndUtc: "2026-03-20T09:43:45.000Z",
                    lifecycleState: "activated",
                    executionState: "decided");
                InsertInterventionRow(
                    connection,
                    interventionId: "break-stale-1",
                    timestampUtc: "2026-03-20T09:43:50.000Z",
                    decidedAtUtc: "2026-03-20T09:43:50.000Z",
                    plannedEndUtc: "2026-03-20T09:43:45.000Z",
                    lifecycleState: "deactivated",
                    executionState: "applied",
                    appliedAtUtc: "2026-03-24T09:00:54.4235492+00:00",
                    ackedAtUtc: "2026-03-24T09:00:54.4235492+00:00",
                    executorAgentInstanceId: "useragent-14948-a329ebb13efe43ada9db12e157796e20");
                InsertInterventionRow(
                    connection,
                    interventionId: "break-expired-1",
                    timestampUtc: "2026-03-21T09:30:00.000Z",
                    decidedAtUtc: "2026-03-21T09:30:00.000Z",
                    plannedEndUtc: "2026-03-21T09:35:04.9985198+00:00",
                    lifecycleState: "activated",
                    executionState: "decided");
            }

            Assert.Empty(store.ReadPendingInterventions("session-9", 10));
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
    public void SqliteStore_AcknowledgeIntervention_ResolvesOlderDecidedSiblingRows()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-interventions-ack-siblings-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPath);

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                InsertInterventionRow(
                    connection,
                    interventionId: "break-sibling-1",
                    timestampUtc: "2026-03-20T09:38:45.000Z",
                    decidedAtUtc: "2026-03-20T09:38:45.000Z",
                    plannedEndUtc: "2026-03-20T09:43:45.000Z",
                    lifecycleState: "activated",
                    executionState: "decided");
                InsertInterventionRow(
                    connection,
                    interventionId: "break-sibling-1",
                    timestampUtc: "2026-03-20T09:43:50.000Z",
                    decidedAtUtc: "2026-03-20T09:43:50.000Z",
                    plannedEndUtc: "2026-03-20T09:43:45.000Z",
                    lifecycleState: "deactivated",
                    executionState: "applied",
                    appliedAtUtc: "2026-03-24T09:00:54.4235492+00:00",
                    ackedAtUtc: "2026-03-24T09:00:54.4235492+00:00",
                    executorAgentInstanceId: "useragent-14948-a329ebb13efe43ada9db12e157796e20");
            }

            bool acked = store.AcknowledgeIntervention(new AgentInterventionAck(
                InterventionId: "break-sibling-1",
                SessionId: "session-9",
                AgentInstanceId: "agent-9",
                Result: "applied",
                ErrorCode: null,
                AppliedAtUtc: DateTimeOffset.Parse("2026-03-24T10:00:00.000+00:00")));

            Assert.True(acked);

            using var verifyConnection = new SqliteConnection($"Data Source={dbPath}");
            verifyConnection.Open();
            using var cmd = verifyConnection.CreateCommand();
            cmd.CommandText =
                """
                SELECT COUNT(*)
                FROM interventions
                WHERE intervention_id = 'break-sibling-1'
                  AND execution_state = 'decided';
                """;
            long decidedRows = (long)(cmd.ExecuteScalar() ?? 0L);
            Assert.Equal(0L, decidedRows);
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

    private static string ReadPayloadString(AgentPendingIntervention pending, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(pending.PayloadJson);
        Assert.True(document.RootElement.TryGetProperty(propertyName, out JsonElement node));
        return node.GetString() ?? string.Empty;
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

    private static void InsertInterventionRow(
        SqliteConnection connection,
        string interventionId,
        string timestampUtc,
        string decidedAtUtc,
        string plannedEndUtc,
        string lifecycleState,
        string executionState,
        string sessionId = "session-9",
        string action = "break_suggestion",
        string commandType = "show_break_toast",
        string? appliedAtUtc = null,
        string? ackedAtUtc = null,
        string errorCode = "",
        int retryCount = 0,
        string executorAgentInstanceId = "")
    {
        using var cmd = connection.CreateCommand();
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
                lifecycle_state,
                session_id,
                command_type,
                payload_json,
                execution_state,
                decided_at_utc,
                applied_at_utc,
                acked_at_utc,
                error_code,
                retry_count,
                executor_agent_instance_id
            )
            VALUES (
                $timestamp_utc,
                $action,
                'fatigue',
                0.55,
                0.91,
                300,
                'Suggest',
                0,
                'test',
                '{"schema_ver":1,"contributors":["test"]}',
                $intervention_id,
                'overload-relief-v1',
                'overload',
                $planned_end_timestamp_utc,
                '',
                '{}',
                'Break suggestion displayed to user.',
                'Dismiss the suggestion overlay or wait for auto-expiry.',
                0,
                '',
                $lifecycle_state,
                $session_id,
                $command_type,
                '{"schema_ver":1,"action":"break_suggestion","command_type":"show_break_toast","title":"PORTA","message":"Take a short break.","duration_seconds":300,"confidence":0.91,"policy_id":"overload-relief-v1","domain":"overload","intervention_id":"placeholder"}',
                $execution_state,
                $decided_at_utc,
                $applied_at_utc,
                $acked_at_utc,
                $error_code,
                $retry_count,
                $executor_agent_instance_id
            );
            """;
        cmd.Parameters.AddWithValue("$timestamp_utc", timestampUtc);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$intervention_id", interventionId);
        cmd.Parameters.AddWithValue("$planned_end_timestamp_utc", plannedEndUtc);
        cmd.Parameters.AddWithValue("$lifecycle_state", lifecycleState);
        cmd.Parameters.AddWithValue("$session_id", sessionId);
        cmd.Parameters.AddWithValue("$command_type", commandType);
        cmd.Parameters.AddWithValue("$execution_state", executionState);
        cmd.Parameters.AddWithValue("$decided_at_utc", decidedAtUtc);
        cmd.Parameters.AddWithValue("$applied_at_utc", appliedAtUtc ?? string.Empty);
        cmd.Parameters.AddWithValue("$acked_at_utc", ackedAtUtc ?? string.Empty);
        cmd.Parameters.AddWithValue("$error_code", errorCode);
        cmd.Parameters.AddWithValue("$retry_count", retryCount);
        cmd.Parameters.AddWithValue("$executor_agent_instance_id", executorAgentInstanceId);
        cmd.ExecuteNonQuery();
    }
}
