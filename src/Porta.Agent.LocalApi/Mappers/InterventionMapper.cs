using Porta.Agent.Core;

namespace Porta.Agent.LocalApi.Mappers;

internal static class InterventionMapper
{
    public static IReadOnlyList<LocalApiInterventionPendingResponse> MapPendingOrdered(
        IEnumerable<AgentPendingIntervention> interventions)
    {
        return interventions
            .OrderBy(static item => item.DecidedAtUtc)
            .ThenBy(static item => item.RowId)
            .Select(Map)
            .ToList();
    }

    public static LocalApiInterventionPendingResponse Map(AgentPendingIntervention item)
    {
        return new LocalApiInterventionPendingResponse(
            Id: item.RowId,
            InterventionId: item.InterventionId,
            SessionId: item.SessionId,
            Action: item.Action,
            CommandType: item.CommandType,
            PayloadJson: item.PayloadJson,
            DecidedAtUtc: item.DecidedAtUtc,
            ExecutionState: item.ExecutionState,
            PolicyId: item.PolicyId,
            Domain: item.Domain,
            Confidence: item.Confidence,
            EscalationLevel: item.EscalationLevel,
            WhatChanged: item.WhatChanged);
    }
}
