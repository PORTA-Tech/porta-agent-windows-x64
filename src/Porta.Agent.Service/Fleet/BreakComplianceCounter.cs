using System;
using System.Collections.Generic;
using Porta.Agent.Common;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// A3 — counts break compliance (suggested / taken) for the daily digest and the daily-stats
/// upload. Single source of truth so both paths report the SAME numbers.
///
/// Root cause this replaces: both reporters matched <c>Action</c> for the substrings "break" AND
/// "ack". But the intervention-ack lifecycle event (SqliteBrainStateStore) writes the ORIGINAL
/// action ("short_break") to <c>action</c> and the outcome to <c>why</c> → surfaced as
/// <see cref="LocalApiEventResponse.Reason"/> ("break_accepted"). No break action ever contains
/// "ack", so "taken" was structurally always 0 — the "0 of 6 taken" bug.
///
/// The same string match also counted EVERY lifecycle row (decided, accepted, dismissed, snoozed,
/// completed…) of one break as another "suggested", inflating the denominator. Both counts are
/// therefore deduplicated by intervention id: one suggestion = one break, however many lifecycle
/// rows it produced.
/// </summary>
internal static class BreakComplianceCounter
{
    /// <summary>Lifecycle reasons that mean the user actually took the break.</summary>
    private static readonly HashSet<string> TakenReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "break_accepted",   // user pressed "Take break"
        "break_completed",  // the countdown ran to the end
    };

    public static BreakCompliance Count(IEnumerable<LocalApiEventResponse> interventions)
    {
        var suggested = new HashSet<string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (LocalApiEventResponse ev in interventions)
        {
            if (!PortaBreakSemantics.IsBreakSuggestionAction(ev.Action))
            {
                continue;
            }

            // One break = one intervention id, regardless of how many lifecycle rows it wrote.
            // Fall back to the event id so a legacy row with no intervention id still counts once.
            string key = string.IsNullOrWhiteSpace(ev.InterventionId)
                ? $"event:{ev.Id}"
                : ev.InterventionId.Trim();

            suggested.Add(key);
            if (!string.IsNullOrWhiteSpace(ev.Reason) && TakenReasons.Contains(ev.Reason.Trim()))
            {
                taken.Add(key);
            }
        }

        return new BreakCompliance(suggested.Count, taken.Count);
    }
}

internal readonly record struct BreakCompliance(int Suggested, int Taken);
