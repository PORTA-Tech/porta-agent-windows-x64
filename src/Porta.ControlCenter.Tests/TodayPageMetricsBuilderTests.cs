using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class TodayPageMetricsBuilderTests
{
    [Fact]
    public void Build_ComputesFocusedAndDistractionTotals_FromProcessedTimeline()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-03T08:00:00Z");
        LocalApiTimelineResponse timeline = new(
            FromUtc: baseUtc,
            ToUtc: baseUtc.AddMinutes(15),
            BucketSeconds: 300,
            Snapshots:
            [
                BuildSnapshot(baseUtc, focusStrength: 0.80d, sustainedAttentionSeconds: 900d, attentionSegmentKind: LocalAttentionSegmentAnalytics.FocusSegmentKind),
                BuildSnapshot(baseUtc.AddMinutes(5), focusStrength: 0.40d, sustainedAttentionSeconds: 300d, attentionSegmentKind: LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind, distractionEpisodeActive: true, distractionEpisodeSeconds: 300d),
                BuildSnapshot(baseUtc.AddMinutes(10), focusStrength: 0.20d, sustainedAttentionSeconds: 120d, attentionSegmentKind: LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind, distractionEpisodeActive: true, distractionEpisodeSeconds: 300d)
            ],
            Interventions: []);

        TodayPageMetrics metrics = TodayPageMetricsBuilder.Build(timeline);

        Assert.True(metrics.HasProcessedTimeline);
        Assert.Equal(420d, metrics.FocusedTodaySeconds, precision: 3);
        Assert.Equal(600d, metrics.DistractionTodaySeconds, precision: 3);
        Assert.Equal(900d, metrics.BestFocusBlockSeconds, precision: 3);
        Assert.Equal(900d, metrics.TrackedTodaySeconds, precision: 3);
        Assert.Equal(46.666666666666664d, metrics.AverageFocusPercent, precision: 6);
        int localHour = baseUtc.ToLocalTime().Hour;
        Assert.Equal($"{localHour:00}:00-{localHour:00}:59", metrics.MostDistractedHourLabel);
    }

    [Fact]
    public void Build_ReturnsEmptyMetrics_WhenTimelineIsMissing()
    {
        TodayPageMetrics metrics = TodayPageMetricsBuilder.Build(null);

        Assert.False(metrics.HasProcessedTimeline);
        Assert.Equal(0d, metrics.FocusedTodaySeconds);
        Assert.Equal(0d, metrics.DistractionTodaySeconds);
        Assert.Equal(0d, metrics.BestFocusBlockSeconds);
        Assert.Equal(0d, metrics.TrackedTodaySeconds);
        Assert.Equal(0d, metrics.AverageFocusPercent);
        Assert.Equal("Not available", metrics.MostDistractedHourLabel);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        DateTimeOffset timestampUtc,
        double focusStrength,
        double sustainedAttentionSeconds,
        string attentionSegmentKind = LocalAttentionSegmentAnalytics.FocusSegmentKind,
        bool distractionEpisodeActive = false,
        double distractionEpisodeSeconds = 0d)
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
            SustainedAttentionDurationSec: sustainedAttentionSeconds,
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
            AttentionSegmentKind: attentionSegmentKind,
            AttentionSegmentSeconds: attentionSegmentKind == LocalAttentionSegmentAnalytics.FocusSegmentKind ? 0d : 300d,
            DistractionEpisodeActive: distractionEpisodeActive,
            DistractionEpisodeSeconds: distractionEpisodeSeconds);
    }
}
