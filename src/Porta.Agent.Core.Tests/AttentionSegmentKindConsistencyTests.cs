using Porta.Agent.Common.Analysis;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Guards the agent ↔ Control Center agreement on attention-segment kinds.
///
/// The agent (DerivedSessionMetricsComputer) emits 17 attention_segment_kind values. The read
/// side historically recognized only 9; the other 8 (the common category/Phase-3 distractions:
/// social media, passive video, comm monitoring, gaming, etc.) silently collapsed to "focus",
/// so they never rendered as distraction in the Control Center history — even though the live
/// widget still showed them. These tests assert every distraction kind the agent can emit is
/// recognized AND classified as distraction on read, and is never mis-counted as focus time.
/// </summary>
public sealed class AttentionSegmentKindConsistencyTests
{
    // Single source of truth: every distraction kind the agent can emit, defined once in
    // Porta.Agent.Common.Analysis.AttentionSegmentKinds. If a new kind is added there, every
    // assertion below automatically covers it.
    public static readonly IReadOnlyList<string> AgentDistractionKinds = AttentionSegmentKinds.AllDistractionKinds;

    // The 8 kinds that were previously collapsing to "focus" on read.
    public static readonly TheoryData<string> PreviouslyDroppedKinds = new()
    {
        LocalAttentionSegmentAnalytics.SocialMediaDistractionSegmentKind,
        LocalAttentionSegmentAnalytics.SocialMediaLoopSegmentKind,
        LocalAttentionSegmentAnalytics.GamingLauncherDistractionSegmentKind,
        LocalAttentionSegmentAnalytics.ActiveGamingDistractionSegmentKind,
        LocalAttentionSegmentAnalytics.PassiveConsumptionSegmentKind,
        LocalAttentionSegmentAnalytics.CommMonitoringLoopSegmentKind,
        LocalAttentionSegmentAnalytics.FocusCategoryAbsentSegmentKind,
        LocalAttentionSegmentAnalytics.BadSessionStartSegmentKind,
    };

    [Fact]
    public void EveryAgentDistractionKind_IsClassifiedAsDistraction()
    {
        foreach (string kind in AgentDistractionKinds)
        {
            Assert.True(
                LocalAttentionSegmentAnalytics.IsDistractionKind(kind),
                $"Kind '{kind}' is emitted by the agent as a distraction but the read side does not classify it as one. " +
                "Register it in LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind.");

            // Must normalize to a recognized kind (never the silent 'focus' fallback).
            string normalized = LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind(kind);
            Assert.NotEqual(LocalAttentionSegmentAnalytics.FocusSegmentKind, normalized);
        }
    }

    [Theory]
    [MemberData(nameof(PreviouslyDroppedKinds))]
    public void PreviouslyDroppedKind_NormalizesToRecognizedDistraction(string kind)
    {
        string normalized = LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind(kind);

        // Display-mapped onto the off-task distraction bucket the Control Center already renders.
        Assert.Equal(LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind, normalized);
        Assert.True(LocalAttentionSegmentAnalytics.IsDistractionKind(kind));
    }

    [Fact]
    public void NonDistractionKinds_AreNotClassifiedAsDistraction()
    {
        // Guard against over-mapping: focus / away / context-excursion must NOT count as distraction.
        Assert.False(LocalAttentionSegmentAnalytics.IsDistractionKind(LocalAttentionSegmentAnalytics.FocusSegmentKind));
        Assert.False(LocalAttentionSegmentAnalytics.IsDistractionKind(LocalAttentionSegmentAnalytics.AwaySegmentKind));
        Assert.False(LocalAttentionSegmentAnalytics.IsDistractionKind(LocalAttentionSegmentAnalytics.ContextExcursionSegmentKind));

        Assert.Equal(LocalAttentionSegmentAnalytics.AwaySegmentKind,
            LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind(LocalAttentionSegmentAnalytics.AwaySegmentKind));
        Assert.Equal(LocalAttentionSegmentAnalytics.ContextExcursionSegmentKind,
            LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind(LocalAttentionSegmentAnalytics.ContextExcursionSegmentKind));
    }

    [Theory]
    [MemberData(nameof(PreviouslyDroppedKinds))]
    public void DroppedKind_RoundTripsThroughSnapshotMapper_AsDistractionNotFocus(string kind)
    {
        // A finalized bucket where the user was in a category distraction (kind set) while the
        // continuity timer still held a positive value. Pre-fix this was mis-credited as focus.
        string stateFlags =
            "{\"schema_ver\":1," +
            $"\"attention_segment_kind\":\"{kind}\"," +
            "\"attention_segment_seconds\":120," +
            "\"distraction_episode_active\":1," +
            "\"distraction_episode_seconds\":120," +
            "\"attention_segment_reason_text\":\"Specific distraction reason from the agent.\"," +
            "\"task_continuity_seconds\":600}";

        AgentBrainSnapshot snapshot = CreateSnapshot(stateFlags);

        var dto = SnapshotMapper.Map(snapshot, latestState: null);

        // Renders + counts as distraction.
        Assert.Equal(LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind, dto.attention_segment_kind);
        Assert.True(LocalAttentionSegmentAnalytics.IsDistractionEpisode(dto));
        Assert.True(LocalAttentionSegmentAnalytics.IsDistractionKind(dto.attention_segment_kind));

        // Never credited as focus time, despite task_continuity_seconds = 600.
        Assert.Equal(0d, dto.FocusEpisodeSeconds);

        // The agent's specific description survives for the tooltip.
        Assert.Equal("Specific distraction reason from the agent.",
            LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(dto));
    }

    private static AgentBrainSnapshot CreateSnapshot(string stateFlagsJson)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: 0.05d,
            ScrollVelocityIndex: 0.08d,
            FocusSwitchRate: 0.03d,
            SmoothedTypingBurstIntensity: 0.04d,
            SmoothedScrollVelocityIndex: 0.06d,
            SmoothedFocusSwitchRate: 0.02d,
            FocusStrength: 0.51d,
            OverloadLikelihood: 0.1d,
            FatigueProbability: 0.1d,
            CognitiveStability: 0.8d,
            DominantStateValue: 0.51d,
            DominantStateName: "focus",
            Confidence: 0.6d,
            LastAction: "maintain_focus",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: [],
            SustainedAttentionDurationSec: 600d,
            IdleFragmentationIndex: 0.01d,
            OverloadActive: false,
            StateFlagsJson: stateFlagsJson,
            ContributorsJson: "{\"schema_ver\":1}");
    }
}
