using Porta.Agent.Common.Identity;
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
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Linked,
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            UpdatedAtUtc: nowUtc,
            LinkedAtUtc: nowUtc.AddMinutes(-2));
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
            WhatChanged: "Long break suggestion displayed to user.",
            HowToUndo: "Dismiss the suggestion overlay or let it auto-expire.",
            EventId: 42,
            ExecutionState: "applied",
            AppliedAtUtc: nowUtc.AddSeconds(2));

        MachineActionEventPayload payload = mapper.Map(ev, identityLink, "alice");

        Assert.Equal("6f390ec2-31b7-4f49-9c11-e508d477b633", payload.DeviceId);
        Assert.Equal("2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b", payload.AccountId);
        Assert.Equal("6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1", payload.OrgId);
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
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Linked,
            AccountId: "2094e8ad-74f6-45fc-a044-c9ab6f9f0a4b",
            OrgId: "6b7fd26e-ae90-4d4c-9d8e-e95c15db89e1",
            UpdatedAtUtc: nowUtc,
            LinkedAtUtc: nowUtc.AddMinutes(-2));
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

        MachineActionEventPayload payload = mapper.Map(ev, identityLink, "alice");

        Assert.Equal("automatic_change", payload.ActionCategory);
        Assert.Equal("verified", payload.ExecutionState);
        Assert.Equal("verified", payload.VerifiedState);
    }

    [Fact]
    public void Map_UsesDecisionRecordFallbackWhenNoInterventionIdExists()
    {
        var mapper = new MachineActionMapper();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-10T10:00:00Z");
        var identityLink = new LocalIdentityLinkSnapshot(
            SchemaVersion: LocalIdentityLinkStore.CurrentSchemaVersion,
            DeviceId: "6f390ec2-31b7-4f49-9c11-e508d477b633",
            LinkStatus: LocalIdentityLinkStatuses.Unlinked,
            AccountId: null,
            OrgId: null,
            UpdatedAtUtc: nowUtc);
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

        MachineActionEventPayload payload = mapper.Map(ev, identityLink, "alice");

        Assert.Equal("decision_record", payload.ActionCategory);
        Assert.Equal("event-9", payload.InterventionId);
        Assert.Equal("decision_only", payload.ExecutionState);
        Assert.Equal("6f390ec2-31b7-4f49-9c11-e508d477b633", payload.DeviceId);
        Assert.Null(payload.AccountId);
        Assert.Null(payload.OrgId);
    }
}
