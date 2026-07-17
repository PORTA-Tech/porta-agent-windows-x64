using Porta.Agent.LocalApi;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

// A3 — "0 of 6 taken". The old counters matched Action for "break" AND "ack", but the ack
// lifecycle event writes the ORIGINAL action ("short_break") to Action and the outcome
// ("break_accepted") to Reason — so "taken" was structurally always 0, and every lifecycle row of
// one break counted as another "suggested". These tests pin both halves.
public sealed class BreakComplianceCounterTests
{
    [Fact]
    public void Count_AcceptedBreak_CountsAsTaken()
    {
        var events = new[]
        {
            BreakEvent("intv-1", "short_break", "break_suggested"),
            BreakEvent("intv-1", "short_break", "break_accepted"),
        };

        BreakCompliance result = BreakComplianceCounter.Count(events);

        Assert.Equal(1, result.Suggested);
        Assert.Equal(1, result.Taken);
    }

    [Fact]
    public void Count_DismissedBreak_IsSuggestedButNotTaken()
    {
        var events = new[]
        {
            BreakEvent("intv-1", "short_break", "break_suggested"),
            BreakEvent("intv-1", "short_break", "break_skipped"),
        };

        BreakCompliance result = BreakComplianceCounter.Count(events);

        Assert.Equal(1, result.Suggested);
        Assert.Equal(0, result.Taken);
    }

    [Fact]
    public void Count_CompletedOverlay_CountsAsTakenOnceNotTwice()
    {
        // Accepting then running the countdown to the end writes BOTH break_accepted and
        // break_completed for the same intervention — that is one break taken, not two.
        var events = new[]
        {
            BreakEvent("intv-1", "long_break", "break_suggested"),
            BreakEvent("intv-1", "long_break", "break_accepted"),
            BreakEvent("intv-1", "long_break", "break_completed"),
        };

        BreakCompliance result = BreakComplianceCounter.Count(events);

        Assert.Equal(1, result.Suggested);
        Assert.Equal(1, result.Taken);
    }

    [Fact]
    public void Count_ManyLifecycleRowsForOneBreak_CountAsOneSuggestion()
    {
        // The inflated-denominator half of the bug: the old string match counted every one of
        // these rows as another suggestion, producing "0 of 6" for what was really one break.
        var events = new[]
        {
            BreakEvent("intv-1", "short_break", "overlay_started"),
            BreakEvent("intv-1", "short_break", "break_suggested"),
            BreakEvent("intv-1", "short_break", "prompt_snoozed"),
            BreakEvent("intv-1", "short_break", "break_accepted"),
            BreakEvent("intv-1", "short_break", "break_completed"),
        };

        BreakCompliance result = BreakComplianceCounter.Count(events);

        Assert.Equal(1, result.Suggested);
        Assert.Equal(1, result.Taken);
    }

    [Fact]
    public void Count_DistinctBreaks_AreCountedSeparately()
    {
        var events = new[]
        {
            BreakEvent("intv-1", "short_break", "break_accepted"),
            BreakEvent("intv-2", "long_break", "break_skipped"),
            BreakEvent("intv-3", "short_break", "break_accepted"),
        };

        BreakCompliance result = BreakComplianceCounter.Count(events);

        Assert.Equal(3, result.Suggested);
        Assert.Equal(2, result.Taken);
    }

    [Fact]
    public void Count_LegacyBreakActions_AreStillCounted()
    {
        var events = new[]
        {
            BreakEvent("intv-1", "break_suggestion", "break_accepted"),          // legacy long
            BreakEvent("intv-2", "stretch_break_suggestion", "break_skipped"),   // legacy short
        };

        BreakCompliance result = BreakComplianceCounter.Count(events);

        Assert.Equal(2, result.Suggested);
        Assert.Equal(1, result.Taken);
    }

    [Fact]
    public void Count_NonBreakEvents_AreIgnored()
    {
        var events = new[]
        {
            BreakEvent("intv-1", "focus_lock_in_suggestion", "prompt_dismissed"),
            BreakEvent("intv-2", "temporary_app_restriction_enabled", "intervention_applied"),
        };

        BreakCompliance result = BreakComplianceCounter.Count(events);

        Assert.Equal(0, result.Suggested);
        Assert.Equal(0, result.Taken);
    }

    private static LocalApiEventResponse BreakEvent(string interventionId, string action, string reason)
        => new(
            Id: 1,
            Type: action.ToUpperInvariant(),
            TsUtc: DateTimeOffset.Parse("2026-07-16T10:00:00Z"),
            IdempotencyKey: $"{interventionId}:{reason}",
            WhyJson: "{}",
            StateFlagsJson: null,
            TimestampUtc: DateTimeOffset.Parse("2026-07-16T10:00:00Z"),
            Action: action,
            TriggerState: "focus",
            TriggerValue: 0.5,
            Confidence: 0.9,
            DurationSeconds: null,
            EscalationLevel: "Suggest",
            Reversible: false,
            Reason: reason,
            Contributors: Array.Empty<string>(),
            InterventionId: interventionId,
            PolicyId: "policy-v1",
            Domain: "control",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: string.Empty,
            WhatChanged: string.Empty,
            HowToUndo: string.Empty,
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "activated",
            CommandType: "show_break_overlay",
            ExecutionState: "accepted",
            DecidedAtUtc: null,
            AppliedAtUtc: null,
            AckedAtUtc: null,
            ExecutionErrorCode: string.Empty,
            ExecutorAgentInstanceId: "agent-1");
}
