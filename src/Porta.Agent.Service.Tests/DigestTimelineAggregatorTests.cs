using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

/// <summary>
/// Part 1: the digest's FocusSeconds must be focus-episode time (the same number the check-in
/// card and Control Center show), and FocusQualityScore must be weighted ÷ episode — identical
/// to LocalTodaySummaryMetricsBuilder so the email can never disagree with the card.
/// </summary>
public sealed class DigestTimelineAggregatorTests
{
    [Fact]
    public void Aggregate_FocusSeconds_IsEpisodeTime_NotStrengthWeighted()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-03T08:00:00Z");
        LocalApiTimelineResponse timeline = BuildTimeline(baseUtc);

        DigestTimelineAggregation agg = DigestTimelineAggregator.Aggregate(timeline);
        LocalTodaySummaryMetrics summary = LocalTodaySummaryMetricsBuilder.Build(timeline);

        Assert.Equal(summary.FocusedTodaySeconds, agg.FocusSeconds, precision: 3);
        Assert.Equal(summary.FocusQualityPercent / 100d, agg.FocusQualityScore, precision: 6);

        // Sanity: the strength-weighted sum (Σ bucket × strength) must NOT be the focus number.
        double weighted = 300d * (0.8d + 0.7d + 0.4d);
        Assert.NotEqual(weighted, agg.FocusSeconds, precision: 3);
    }

    [Fact]
    public void Aggregate_EmptyTimeline_ReturnsZeroFocus()
    {
        LocalApiTimelineResponse timeline = new(
            FromUtc: DateTimeOffset.UtcNow,
            ToUtc: DateTimeOffset.UtcNow,
            BucketSeconds: 60,
            Snapshots: [],
            Interventions: []);

        DigestTimelineAggregation agg = DigestTimelineAggregator.Aggregate(timeline);

        Assert.Equal(0d, agg.FocusSeconds);
        Assert.Equal(0d, agg.FocusQualityScore);
    }

    private static LocalApiTimelineResponse BuildTimeline(DateTimeOffset baseUtc)
    {
        return new LocalApiTimelineResponse(
            FromUtc: baseUtc,
            ToUtc: baseUtc.AddMinutes(15),
            BucketSeconds: 300,
            Snapshots:
            [
                BuildSnapshot(baseUtc, focusStrength: 0.8d, focusEpisodeSeconds: 300d),
                BuildSnapshot(baseUtc.AddMinutes(5), focusStrength: 0.7d, focusEpisodeSeconds: 600d),
                BuildSnapshot(baseUtc.AddMinutes(10), focusStrength: 0.4d, focusEpisodeSeconds: 0d,
                    attentionSegmentKind: LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind,
                    distractionEpisodeActive: true)
            ],
            Interventions: []);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        DateTimeOffset timestampUtc,
        double focusStrength,
        double focusEpisodeSeconds,
        string attentionSegmentKind = LocalAttentionSegmentAnalytics.FocusSegmentKind,
        bool distractionEpisodeActive = false)
    {
        return new LocalApiCurrentSnapshotResponse(
            TimestampUtc: timestampUtc,
            TypingBurstIntensity: 0.2,
            ScrollVelocityIndex: 0.1,
            FocusSwitchRate: 0.1,
            SmoothedTypingBurstIntensity: 0.2,
            SmoothedScrollVelocityIndex: 0.1,
            SmoothedFocusSwitchRate: 0.1,
            FocusStrength: focusStrength,
            OverloadLikelihood: 0.25,
            FatigueProbability: 0.10,
            CognitiveStability: 0.80,
            DominantStateValue: focusStrength,
            DominantStateName: "focus",
            Confidence: 0.85,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus"],
            SustainedAttentionDurationSec: 600d,
            IdleFragmentationIndex: 0.15,
            FocusChoppinessIndex: 0.10,
            OverloadActive: false,
            StateFlagsJson: null,
            ContributorsJson: null,
            BaselineReady: true,
            BaselineReadyPct: 1.0,
            AvgQuality2m: 0.92,
            ColdStart: false,
            CalibrationStage: CalibrationTruth.StageCalibrated,
            SystemActionsEnabled: true,
            TrustTier: CalibrationTruth.TrustHigh,
            CurrentQualityScore: 0.91,
            PartialData: false,
            MissingMajorChannels: [],
            CooldownsRemaining: null,
            DeepWorkActive: false,
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            FComponent: 0.15,
            IComponent: 0.30,
            VComponent: 0.12,
            RuntimeStateFreshness: "fresh",
            RuntimeStateRefreshError: null,
            AnalyzedStateAvailable: true,
            ModelSource: "analyzed_state",
            FocusEpisodeSeconds: focusEpisodeSeconds,
            AttentionSegmentKind: attentionSegmentKind,
            AttentionSegmentSeconds: attentionSegmentKind == LocalAttentionSegmentAnalytics.FocusSegmentKind ? 0d : 300d,
            DistractionEpisodeActive: distractionEpisodeActive,
            DistractionEpisodeSeconds: distractionEpisodeActive ? 300d : 0d);
    }
}
