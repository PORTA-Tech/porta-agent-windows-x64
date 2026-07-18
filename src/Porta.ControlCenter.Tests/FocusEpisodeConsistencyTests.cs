using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

/// <summary>
/// Part 1 guarantee: every surface's "focused" number equals the Control Center's focus-episode
/// total, and focus quality equals weighted ÷ episode. These tests pin LocalFocusEpisodeMetrics
/// (the single source of truth) against HistoryTimelineEpisodeBuilder (what the Control Center
/// History view renders) so the two can never drift apart again.
/// </summary>
public sealed class FocusEpisodeConsistencyTests
{
    [Fact]
    public void ComputeEpisodeFocusSeconds_SingleRun_SpansFirstToLastBucketEnd()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-03T08:00:00Z");
        var snapshots = new List<LocalApiCurrentSnapshotResponse>
        {
            BuildSnapshot(baseUtc, focusStrength: 0.9d, focusEpisodeSeconds: 300d),
            BuildSnapshot(baseUtc.AddMinutes(5), focusStrength: 0.8d, focusEpisodeSeconds: 600d),
            BuildSnapshot(baseUtc.AddMinutes(10), focusStrength: 0.7d, focusEpisodeSeconds: 900d)
        };

        double seconds = LocalFocusEpisodeMetrics.ComputeEpisodeFocusSeconds(snapshots, bucketSeconds: 300d);

        // One contiguous run: t0 .. t10m + one bucket = 15 minutes.
        Assert.Equal(900d, seconds, precision: 3);
    }

    [Fact]
    public void ComputeEpisodeFocusSeconds_GapBeyondGrace_SplitsIntoTwoRuns()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-03T08:00:00Z");
        var snapshots = new List<LocalApiCurrentSnapshotResponse>
        {
            BuildSnapshot(baseUtc, focusStrength: 0.9d, focusEpisodeSeconds: 300d),
            // 20-minute gap > bucket (300s) + grace (60s) → new run.
            BuildSnapshot(baseUtc.AddMinutes(20), focusStrength: 0.8d, focusEpisodeSeconds: 300d)
        };

        double seconds = LocalFocusEpisodeMetrics.ComputeEpisodeFocusSeconds(snapshots, bucketSeconds: 300d);

        // Two single-bucket runs of 300s each.
        Assert.Equal(600d, seconds, precision: 3);
    }

    [Fact]
    public void ComputeEpisodeFocusSeconds_InactiveBuckets_DoNotCount()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-03T08:00:00Z");
        var snapshots = new List<LocalApiCurrentSnapshotResponse>
        {
            BuildSnapshot(baseUtc, focusStrength: 0.9d, focusEpisodeSeconds: 0d),
            BuildSnapshot(baseUtc.AddMinutes(5), focusStrength: 0.8d, focusEpisodeSeconds: 0.4d)
        };

        Assert.Equal(0d, LocalFocusEpisodeMetrics.ComputeEpisodeFocusSeconds(snapshots, 300d));
    }

    [Fact]
    public void ComputeFocusQualityPercent_IsWeightedOverEpisode_ClampedTo100()
    {
        Assert.Equal(53d, LocalFocusEpisodeMetrics.ComputeFocusQualityPercent(53d, 100d), precision: 3);
        Assert.Equal(100d, LocalFocusEpisodeMetrics.ComputeFocusQualityPercent(150d, 100d), precision: 3);
        Assert.Equal(0d, LocalFocusEpisodeMetrics.ComputeFocusQualityPercent(50d, 0d), precision: 3);
        Assert.Equal(0d, LocalFocusEpisodeMetrics.ComputeFocusQualityPercent(0d, 100d), precision: 3);
    }

    [Fact]
    public void SummaryBuilderFocus_EqualsControlCenterEpisodeTotal_ForTheSameTimeline()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-03T08:00:00Z");
        // Mixed day: an episode run, a distraction break, a second episode run after a gap.
        LocalApiTimelineResponse timeline = new(
            FromUtc: baseUtc,
            ToUtc: baseUtc.AddHours(2),
            BucketSeconds: 300,
            Snapshots:
            [
                BuildSnapshot(baseUtc, focusStrength: 0.9d, focusEpisodeSeconds: 300d),
                BuildSnapshot(baseUtc.AddMinutes(5), focusStrength: 0.7d, focusEpisodeSeconds: 600d),
                BuildSnapshot(baseUtc.AddMinutes(10), focusStrength: 0.4d, focusEpisodeSeconds: 0d,
                    attentionSegmentKind: LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind,
                    distractionEpisodeActive: true),
                BuildSnapshot(baseUtc.AddMinutes(45), focusStrength: 0.8d, focusEpisodeSeconds: 300d),
                BuildSnapshot(baseUtc.AddMinutes(50), focusStrength: 0.6d, focusEpisodeSeconds: 600d)
            ],
            Interventions: []);

        LocalTodaySummaryMetrics summary = LocalTodaySummaryMetricsBuilder.Build(timeline);
        double controlCenterEpisodeTotal = HistoryTimelineEpisodeBuilder.Build(timeline)
            .Where(static episode => episode.Kind == HistoryTimelineEpisodeKind.FocusEpisode)
            .Sum(static episode => episode.DurationSeconds);

        Assert.Equal(controlCenterEpisodeTotal, summary.FocusedTodaySeconds, precision: 3);

        // Quality == weighted ÷ episode (weighted = Σ bucket × strength over ALL buckets).
        double weighted = 300d * (0.9d + 0.7d + 0.4d + 0.8d + 0.6d);
        double expectedQuality = weighted / summary.FocusedTodaySeconds * 100d;
        Assert.Equal(Math.Clamp(expectedQuality, 0d, 100d), summary.FocusQualityPercent, precision: 3);
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
