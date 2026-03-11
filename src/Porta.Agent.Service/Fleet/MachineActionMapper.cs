using System;
using Porta.Agent.Common.Identity;
using Porta.Agent.Common;
using Porta.Agent.Core;

namespace Porta.Agent.Service.Fleet;

internal sealed class MachineActionMapper
{
    private readonly string _machineName = Environment.MachineName;

    public MachineActionEventPayload Map(AgentControlEvent controlEvent, LocalIdentityLinkSnapshot identityLink, string userName)
    {
        ArgumentNullException.ThrowIfNull(controlEvent);
        ArgumentNullException.ThrowIfNull(identityLink);

        string category = ResolveCategory(controlEvent.Action, controlEvent.CommandType);
        string executionState = NormalizeExecutionState(controlEvent.ExecutionState);
        string verifiedState = ResolveVerifiedState(category, executionState);
        string interventionId = !string.IsNullOrWhiteSpace(controlEvent.InterventionId)
            ? controlEvent.InterventionId
            : $"event-{Math.Max(0L, controlEvent.EventId)}";
        string? accountId = IsLinked(identityLink) ? identityLink.AccountId : null;
        string? orgId = IsLinked(identityLink) ? identityLink.OrgId : null;

        return new MachineActionEventPayload(
            DeviceId: identityLink.DeviceId,
            AccountId: accountId,
            OrgId: orgId,
            MachineId: _machineName,
            UserName: string.IsNullOrWhiteSpace(userName) ? "unknown" : userName.Trim(),
            InterventionId: interventionId,
            Action: controlEvent.Action,
            ActionCategory: category,
            DecisionState: "decided",
            ExecutionState: executionState,
            VerifiedState: verifiedState,
            TimestampUtc: ToUtcString(controlEvent.TimestampUtc),
            AppliedAtUtc: ToUtcStringOrNull(controlEvent.AppliedAtUtc),
            ErrorCode: NullIfWhiteSpace(controlEvent.ExecutionErrorCode),
            PolicyId: string.IsNullOrWhiteSpace(controlEvent.PolicyId) ? "n/a" : controlEvent.PolicyId,
            WhatChanged: NullIfWhiteSpace(controlEvent.WhatChanged),
            HowToUndo: NullIfWhiteSpace(controlEvent.HowToUndo));
    }

    private static string ResolveCategory(string action, string? commandType)
    {
        string classification = PortaActionSemantics.ClassifyAction(action, commandType);
        return classification switch
        {
            "Real Action" when IsSuggestionAction(action) => "suggestion",
            "Real Action" => "automatic_change",
            "Policy Intent" => "policy_intent",
            "Unsupported" => "unsupported",
            _ => "decision_record"
        };
    }

    private static string ResolveVerifiedState(string category, string executionState)
    {
        if (!string.Equals(category, "automatic_change", StringComparison.Ordinal))
        {
            return "not_applicable";
        }

        return executionState switch
        {
            "verified" => "verified",
            "applied" or "failed" => "unverified",
            _ => "pending"
        };
    }

    private static bool IsSuggestionAction(string? action)
    {
        string normalized = action?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "break_suggestion"
            or "focus_lock_in_suggestion"
            or "stretch_break_suggestion";
    }

    private static string NormalizeExecutionState(string? executionState) =>
        executionState?.Trim().ToLowerInvariant() switch
        {
            "decided" => "decided",
            "applied" => "applied",
            "verified" => "verified",
            "failed" => "failed",
            _ => "decision_only"
        };

    private static string ToUtcString(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string? ToUtcStringOrNull(DateTimeOffset? value) =>
        value.HasValue ? ToUtcString(value.Value) : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsLinked(LocalIdentityLinkSnapshot snapshot) =>
        string.Equals(snapshot.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(snapshot.AccountId)
        && !string.IsNullOrWhiteSpace(snapshot.OrgId);
}
