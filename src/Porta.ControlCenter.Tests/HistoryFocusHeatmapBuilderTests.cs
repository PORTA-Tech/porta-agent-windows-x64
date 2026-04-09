using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class HistoryFocusHeatmapBuilderTests
{
    [Fact]
    public void Build_AggregatesSnapshotsIntoDayHourCells()
    {
        DateTimeOffset firstUtc = DateTimeOffset.Parse("2026-04-04T08:10:00Z");
        DateTimeOffset secondUtc = firstUtc.AddMinutes(20);
        DateTimeOffset thirdUtc = firstUtc.AddDays(1).AddHours(2);

        LocalApiTimelineResponse timeline = new(
            FromUtc: firstUtc.AddHours(-1),
            ToUtc: thirdUtc.AddHours(1),
            BucketSeconds: 300,
            Snapshots:
            [
                BuildSnapshot(firstUtc, focusStrength: 0.80d, sustainedAttentionSeconds: 900d, overloadLikelihood: 0.20d, focusEpisodeSeconds: 900d),
                BuildSnapshot(secondUtc, focusStrength: 0.40d, sustainedAttentionSeconds: 480d, overloadLikelihood: 0.35d, attentionSegmentKind: LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind, distractionEpisodeActive: true, distractionEpisodeSeconds: 300d, attentionSegmentReasonText: "Stayed outside focus context for 5m."),
                BuildSnapshot(thirdUtc, focusStrength: 0.65d, sustainedAttentionSeconds: 1200d, overloadLikelihood: 0.25d, focusEpisodeSeconds: 1200d)
            ],
            Interventions:
            [
                BuildEvent(secondUtc, "break_suggestion"),
                BuildEvent(thirdUtc, "notification_reduced")
            ]);

        HistoryFocusHeatmap heatmap = HistoryFocusHeatmapBuilder.Build(
            timeline,
            fallbackFromUtc: firstUtc.AddDays(-1),
            fallbackToUtc: thirdUtc.AddDays(1));

        Assert.True(heatmap.HasTimeline);
        Assert.True(heatmap.HasAnyTracking);
        Assert.Equal(2, heatmap.ActionMarkerCount);
        Assert.True(heatmap.Days.Count >= 2);

        DateTime firstLocalDate = firstUtc.ToLocalTime().Date;
        int firstLocalHour = firstUtc.ToLocalTime().Hour;
        HistoryFocusHeatmapCell? firstCell = heatmap.Cells.FirstOrDefault(cell =>
            cell.LocalDate == firstLocalDate && cell.Hour == firstLocalHour);

        Assert.NotNull(firstCell);
        Assert.Equal(360d, firstCell!.FocusedSeconds, precision: 3);
        Assert.Equal(600d, firstCell.TrackedSeconds, precision: 3);
        Assert.Equal(60d, firstCell.AverageFocusPercent, precision: 3);
        Assert.Single(firstCell.Interventions);
        Assert.True(firstCell.Episodes.Count >= 1);

        HistoryFocusHeatmapCell? bestCell = heatmap.BestFocusCell;
        Assert.NotNull(bestCell);
        Assert.Equal(firstLocalDate, bestCell!.LocalDate);
        Assert.Equal(firstLocalHour, bestCell.Hour);
    }

    [Fact]
    public void Build_DerivesEpisodeDrilldownAcrossFocusDistractionAwayAndOverload()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-04T09:00:00Z");
        LocalApiTimelineResponse timeline = new(
            FromUtc: baseUtc,
            ToUtc: baseUtc.AddMinutes(30),
            BucketSeconds: 300,
            Snapshots:
            [
                BuildSnapshot(baseUtc, focusStrength: 0.88d, sustainedAttentionSeconds: 600d, overloadLikelihood: 0.22d, focusEpisodeSeconds: 600d),
                BuildSnapshot(baseUtc.AddMinutes(5), focusStrength: 0.82d, sustainedAttentionSeconds: 900d, overloadLikelihood: 0.24d, focusEpisodeSeconds: 900d),
                BuildSnapshot(baseUtc.AddMinutes(10), focusStrength: 0.24d, sustainedAttentionSeconds: 120d, overloadLikelihood: 0.41d, attentionSegmentKind: LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind, distractionEpisodeActive: true, distractionEpisodeSeconds: 300d, attentionSegmentReasonText: "Stayed outside focus context for 5m."),
                BuildSnapshot(baseUtc.AddMinutes(15), focusStrength: 0.12d, sustainedAttentionSeconds: 0d, overloadLikelihood: 0.18d, attentionSegmentKind: LocalAttentionSegmentAnalytics.AwaySegmentKind, attentionSegmentReasonText: "Away for 5m.", focusEpisodeReasonText: "Long break ended the focus episode after 5m away."),
                BuildSnapshot(baseUtc.AddMinutes(20), focusStrength: 0.35d, sustainedAttentionSeconds: 180d, overloadLikelihood: 0.82d, overloadActive: true, attentionSegmentKind: LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind, distractionEpisodeActive: true, distractionEpisodeSeconds: 300d, attentionSegmentReasonText: "Rapid switching / fragmentation for 5m."),
                BuildSnapshot(baseUtc.AddMinutes(25), focusStrength: 0.30d, sustainedAttentionSeconds: 120d, overloadLikelihood: 0.86d, overloadActive: true, attentionSegmentKind: LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind, distractionEpisodeActive: true, distractionEpisodeSeconds: 600d, attentionSegmentReasonText: "Rapid switching / fragmentation for 10m.")
            ],
            Interventions:
            [
                BuildEvent(baseUtc.AddMinutes(22), "focus_mode_enabled")
            ]);

        HistoryFocusHeatmap heatmap = HistoryFocusHeatmapBuilder.Build(
            timeline,
            fallbackFromUtc: baseUtc,
            fallbackToUtc: baseUtc.AddHours(1));

        Assert.Contains(heatmap.Episodes, episode => episode.Kind == HistoryTimelineEpisodeKind.FocusEpisode);
        Assert.Contains(heatmap.Episodes, episode => episode.Kind == HistoryTimelineEpisodeKind.OffTaskDistraction);
        Assert.Contains(heatmap.Episodes, episode => episode.Kind == HistoryTimelineEpisodeKind.Away);
        Assert.Contains(heatmap.Episodes, episode => episode.Kind == HistoryTimelineEpisodeKind.OverloadEpisode);
        Assert.True(heatmap.FocusEpisodeCount >= 1);
        Assert.True(heatmap.DistractionEpisodeCount >= 1);
        Assert.True(heatmap.AwayEpisodeCount >= 1);
        Assert.True(heatmap.OverloadEpisodeCount >= 1);

        DateTime localDate = baseUtc.ToLocalTime().Date;
        int localHour = baseUtc.ToLocalTime().Hour;
        HistoryFocusHeatmapCell? cell = heatmap.Cells.FirstOrDefault(candidate =>
            candidate.LocalDate == localDate && candidate.Hour == localHour);

        Assert.NotNull(cell);
        Assert.True(cell!.Episodes.Count >= 4);
        Assert.Contains(cell.Episodes, episode => episode.Interventions.Count == 1);
    }

    [Fact]
    public void Build_CreatesPlaceholderCells_WhenTimelineIsMissing()
    {
        DateTimeOffset fromUtc = DateTimeOffset.Parse("2026-04-05T00:00:00Z");
        DateTimeOffset toUtc = fromUtc.AddDays(1);

        HistoryFocusHeatmap heatmap = HistoryFocusHeatmapBuilder.Build(
            timeline: null,
            fallbackFromUtc: fromUtc,
            fallbackToUtc: toUtc);

        Assert.False(heatmap.HasTimeline);
        Assert.False(heatmap.HasAnyTracking);
        Assert.True(heatmap.Days.Count >= 1);
        Assert.Equal(heatmap.Days.Count * 24, heatmap.Cells.Count);
        Assert.All(heatmap.Cells, cell => Assert.False(cell.HasTracking));
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        DateTimeOffset timestampUtc,
        double focusStrength,
        double sustainedAttentionSeconds,
        double overloadLikelihood,
        double focusEpisodeSeconds = 0d,
        bool overloadActive = false,
        string attentionSegmentKind = LocalAttentionSegmentAnalytics.FocusSegmentKind,
        bool distractionEpisodeActive = false,
        double distractionEpisodeSeconds = 0d,
        string? attentionSegmentReasonText = null,
        string? focusEpisodeReasonText = null)
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
            OverloadLikelihood: overloadLikelihood,
            FatigueProbability: 0.10,
            CognitiveStability: 0.80,
            DominantStateValue: focusStrength,
            DominantStateName: "focus",
            Confidence: 0.85,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus", "typing"],
            SustainedAttentionDurationSec: sustainedAttentionSeconds,
            IdleFragmentationIndex: 0.15,
            FocusChoppinessIndex: 0.10,
            OverloadActive: overloadActive,
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
            FocusEpisodeQuality: focusEpisodeSeconds > 0d ? 0.82d : 0d,
            FocusEpisodeSeconds: focusEpisodeSeconds,
            FocusEpisodeState: focusEpisodeSeconds > 0d ? "keep" : "reset",
            AttentionSegmentKind: attentionSegmentKind,
            AttentionSegmentSeconds: attentionSegmentKind == LocalAttentionSegmentAnalytics.FocusSegmentKind ? 0d : 300d,
            DistractionEpisodeActive: distractionEpisodeActive,
            DistractionEpisodeSeconds: distractionEpisodeSeconds,
            AttentionSegmentReasonText: attentionSegmentReasonText ?? "Steady focus episode.",
            FocusEpisodeReasonText: focusEpisodeReasonText ?? (focusEpisodeSeconds > 0d ? "Focus episode active." : "Focus episode ended."));
    }

    private static LocalApiEventResponse BuildEvent(DateTimeOffset timestampUtc, string action)
    {
        return new LocalApiEventResponse(
            Id: timestampUtc.Ticks,
            Type: "intervention",
            TsUtc: timestampUtc,
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            WhyJson: "{}",
            StateFlagsJson: null,
            TimestampUtc: timestampUtc,
            Action: action,
            TriggerState: "focus",
            TriggerValue: 0.5,
            Confidence: 0.7,
            DurationSeconds: null,
            EscalationLevel: "low",
            Reversible: false,
            Reason: "reason",
            Contributors: ["focus"],
            InterventionId: Guid.NewGuid().ToString("N"),
            PolicyId: "p1",
            Domain: "desktop",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: action,
            HowToUndo: string.Empty,
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "applied",
            CommandType: "",
            ExecutionState: "applied",
            DecidedAtUtc: timestampUtc,
            AppliedAtUtc: timestampUtc,
            AckedAtUtc: timestampUtc,
            ExecutionErrorCode: "",
            ExecutorAgentInstanceId: "ua-1");
    }
}
