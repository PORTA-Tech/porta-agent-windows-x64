using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class HistoryPageInsightsBuilderTests
{
    [Fact]
    public void Build_ComputesHumanHistoryMetrics_FromTimeline()
    {
        DateTimeOffset baseUtc = DateTimeOffset.Parse("2026-04-03T09:00:00Z");
        LocalApiTimelineResponse timeline = new(
            FromUtc: baseUtc,
            ToUtc: baseUtc.AddMinutes(15),
            BucketSeconds: 300,
            Snapshots:
            [
                BuildSnapshot(baseUtc, focusStrength: 0.90d, sustainedAttentionSeconds: 900d),
                BuildSnapshot(baseUtc.AddMinutes(5), focusStrength: 0.30d, sustainedAttentionSeconds: 240d),
                BuildSnapshot(baseUtc.AddMinutes(10), focusStrength: 0.20d, sustainedAttentionSeconds: 120d)
            ],
            Interventions:
            [
                BuildEvent(baseUtc.AddMinutes(3), "break_suggestion", whatChanged: string.Empty),
                BuildEvent(baseUtc.AddMinutes(12), "notification_reduced", whatChanged: "Notifications reduced")
            ]);

        HistoryPageInsights insights = HistoryPageInsightsBuilder.Build(timeline);

        Assert.True(insights.HasTimeline);
        Assert.Equal(420d, insights.FocusedSeconds, precision: 3);
        Assert.Equal(480d, insights.DistractionSeconds, precision: 3);
        Assert.Equal(900d, insights.TrackedSeconds, precision: 3);
        Assert.Equal(900d, insights.LongestFocusBlockSeconds, precision: 3);
        Assert.Equal(2, insights.ActionMarkerCount);
        Assert.Equal(1, insights.SuggestionCount);
        Assert.Equal(1, insights.ChangeCount);
        Assert.Equal("afternoon", insights.MostDistractedPeriodLabel);
    }

    [Fact]
    public void Build_ReturnsEmptyHistoryMetrics_WhenTimelineMissing()
    {
        HistoryPageInsights insights = HistoryPageInsightsBuilder.Build(null);

        Assert.False(insights.HasTimeline);
        Assert.Equal(0d, insights.FocusedSeconds);
        Assert.Equal(0d, insights.DistractionSeconds);
        Assert.Equal(0d, insights.LongestFocusBlockSeconds);
        Assert.Equal("Not available", insights.MostDistractedPeriodLabel);
        Assert.Equal(0, insights.ActionMarkerCount);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        DateTimeOffset timestampUtc,
        double focusStrength,
        double sustainedAttentionSeconds)
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
            FatigueProbability: 0.1,
            CognitiveStability: 0.8,
            DominantStateValue: focusStrength,
            DominantStateName: "focus",
            Confidence: 0.82,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus"],
            SustainedAttentionDurationSec: sustainedAttentionSeconds,
            IdleFragmentationIndex: 0.2,
            FocusChoppinessIndex: 0.1,
            OverloadActive: false,
            StateFlagsJson: null,
            ContributorsJson: null,
            BaselineReady: true,
            BaselineReadyPct: 1.0,
            AvgQuality2m: 0.9,
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
            FComponent: 0.1,
            IComponent: 0.2,
            VComponent: 0.1,
            RuntimeStateFreshness: "fresh",
            RuntimeStateRefreshError: null,
            AnalyzedStateAvailable: true,
            ModelSource: "analyzed_state");
    }

    private static LocalApiEventResponse BuildEvent(DateTimeOffset timestampUtc, string action, string whatChanged)
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
            WhatChanged: whatChanged,
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
