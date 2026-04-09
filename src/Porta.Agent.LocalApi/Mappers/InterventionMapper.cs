using Porta.Agent.Core;
using System.Text.Json;

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
        PendingPayloadProjection payload = PendingPayloadProjection.Parse(item.PayloadJson, item.Action);
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
            WhatChanged: item.WhatChanged,
            InteractionKind: payload.InteractionKind ?? item.InteractionKind,
            DeliverySurface: payload.DeliverySurface ?? item.DeliverySurface,
            TipKey: payload.TipKey ?? item.TipKey,
            BreakKind: payload.BreakKind ?? item.BreakKind,
            CountdownSeconds: payload.CountdownSeconds ?? item.CountdownSeconds);
    }

    private sealed record PendingPayloadProjection(
        string? InteractionKind,
        string? DeliverySurface,
        string? TipKey,
        string? BreakKind,
        int? CountdownSeconds)
    {
        public static PendingPayloadProjection Parse(string? payloadJson, string? action)
        {
            string? interactionKind = null;
            string? deliverySurface = null;
            string? tipKey = null;
            string? breakKind = null;
            int? countdownSeconds = null;

            if (!string.IsNullOrWhiteSpace(payloadJson))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(payloadJson);
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        interactionKind = ReadString(root, "interaction_kind");
                        deliverySurface = ReadString(root, "delivery_surface");
                        tipKey = ReadString(root, "tip_key");
                        breakKind = ReadString(root, "break_kind");
                        countdownSeconds = ReadInt(root, "countdown_seconds");
                    }
                }
                catch
                {
                }
            }

            string normalizedAction = Normalize(action);
            tipKey ??= normalizedAction;
            breakKind ??= Porta.Agent.Common.PortaBreakSemantics.ResolveBreakKind(tipKey ?? normalizedAction);
            interactionKind ??= normalizedAction is "break_suggestion" or "stretch_break_suggestion" or "focus_lock_in_suggestion"
                ? "choice_prompt"
                : "status";
            deliverySurface ??= breakKind != null
                ? "break_overlay"
                : normalizedAction == "focus_lock_in_suggestion"
                    ? "widget"
                    : "toast";
            countdownSeconds ??= breakKind != null
                ? (int)Porta.Agent.Common.PortaBreakSemantics.ResolveBreakCountdownDuration(breakKind).TotalSeconds
                : null;
            return new PendingPayloadProjection(interactionKind, deliverySurface, tipKey, breakKind, countdownSeconds);
        }

        private static string? ReadString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement node) || node.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = node.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static int? ReadInt(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out JsonElement node) && node.TryGetInt32(out int value)
                ? value
                : null;
        }

        private static string Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }
}
