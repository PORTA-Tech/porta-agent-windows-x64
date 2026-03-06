using Porta.Agent.Core;

namespace Porta.Agent.LocalApi.Mappers;

internal static class EventMapper
{
    public static LocalApiEventResponse Map(AgentControlEvent ev)
    {
        return new LocalApiEventResponse(
            ev.EventId,
            ToEventType(ev.Action),
            ev.TimestampUtc,
            ev.IdempotencyKey,
            ev.WhyJson,
            string.IsNullOrWhiteSpace(ev.StateFlagsJson) ? null : ev.StateFlagsJson,
            ev.TimestampUtc,
            ev.Action,
            ev.TriggerState,
            ev.TriggerValue,
            ev.Confidence,
            ev.DurationSeconds,
            ev.EscalationLevel,
            ev.Reversible,
            ev.Reason,
            ev.Contributors,
            ev.InterventionId,
            ev.PolicyId,
            ev.Domain,
            ev.PlannedEndTimestampUtc,
            ev.ActualEndTimestampUtc,
            ev.PreviousSystemStateJson,
            ev.WhatChanged,
            ev.HowToUndo,
            ev.ReversalApplied,
            ev.CooldownEndTimestampUtc,
            ev.LifecycleState);
    }

    private static string ToEventType(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return "UNKNOWN";
        }

        return action.Trim().ToUpperInvariant();
    }
}
