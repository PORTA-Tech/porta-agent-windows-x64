using Porta.Agent.Core;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

public sealed class MachineActionMapperTests
{
    [Fact]
    public void Map_ClassifiesSuggestionActionsAndPreservesExecutionTruth()
    {
        var mapper = new MachineActionMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var ev = new AgentControlEvent(
            TimestampUtc: nowUtc,
            Action: "break_suggestion",
            TriggerState: "fatigue",
            TriggerValue: 0.81,
            Confidence: 0.74,
            DurationSeconds: 300,
            EscalationLevel: "Suggest",
            Reversible: false,
            Reason: "effective_focus_threshold_met",
            Contributors: ["continuity"],
            InterventionId: "int-1",
            PolicyId: "break-policy",
            WhatChanged: "Break suggestion displayed to user.",
            HowToUndo: "Dismiss the suggestion overlay or let it auto-expire.",
            EventId: 42,
            ExecutionState: "applied",
            AppliedAtUtc: nowUtc.AddSeconds(2));

        MachineActionEventPayload payload = mapper.Map(ev, "alice");

        Assert.Equal("suggestion", payload.ActionCategory);
        Assert.Equal("decided", payload.DecisionState);
        Assert.Equal("applied", payload.ExecutionState);
        Assert.Equal("not_applicable", payload.VerifiedState);
        Assert.Equal("int-1", payload.InterventionId);
    }

    [Fact]
    public void Map_ClassifiesAutomaticChangesAndTracksVerificationState()
    {
        var mapper = new MachineActionMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var ev = new AgentControlEvent(
            TimestampUtc: nowUtc,
            Action: "deep_work_notifications_suppressed",
            TriggerState: "deep_work",
            TriggerValue: 0.92,
            Confidence: 0.88,
            DurationSeconds: 600,
            EscalationLevel: "Nudge",
            Reversible: true,
            Reason: "deep_work_entered",
            Contributors: ["continuity"],
            InterventionId: "int-2",
            PolicyId: "deep-work-policy",
            EventId: 43,
            ExecutionState: "verified");

        MachineActionEventPayload payload = mapper.Map(ev, "alice");

        Assert.Equal("automatic_change", payload.ActionCategory);
        Assert.Equal("verified", payload.ExecutionState);
        Assert.Equal("verified", payload.VerifiedState);
    }

    [Fact]
    public void Map_UsesDecisionRecordFallbackWhenNoInterventionIdExists()
    {
        var mapper = new MachineActionMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var ev = new AgentControlEvent(
            TimestampUtc: nowUtc,
            Action: "inform_overload_risk",
            TriggerState: "overload",
            TriggerValue: 0.44,
            Confidence: 0.61,
            DurationSeconds: null,
            EscalationLevel: "Inform",
            Reversible: false,
            Reason: "monitor_only",
            Contributors: ["typing"],
            EventId: 9,
            ExecutionState: "decision_only");

        MachineActionEventPayload payload = mapper.Map(ev, "alice");

        Assert.Equal("decision_record", payload.ActionCategory);
        Assert.Equal("event-9", payload.InterventionId);
        Assert.Equal("decision_only", payload.ExecutionState);
    }
}
