using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Core.Tests;

public sealed class LocalCurrentExperienceStateAdapterTests
{
    [Fact]
    public void Build_UsesDurableFocusEpisode_WhenFreshLiveTimerIsDifferent()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalCurrentExperienceState state = LocalCurrentExperienceStateAdapter.Build(
            BuildStatus(),
            BuildSnapshot(
                sustainedAttentionSeconds: 10d,
                focusEpisodeSeconds: 600d,
                analyzedStateAvailable: false),
            BuildLiveSession(nowUtc, nowUtc.AddMinutes(-4)),
            recentEvents: [],
            nowUtc: nowUtc);

        Assert.True(state.LiveSessionFresh);
        Assert.False(state.CurrentFocusSessionUsesLiveSignal);
        Assert.True(state.CurrentFocusSessionSeconds.HasValue);
        Assert.Equal(600d, state.CurrentFocusSessionSeconds.Value);
        Assert.False(state.SnapshotAnalysisReady);
    }

    [Fact]
    public void Build_ComputesDeepWorkModeAndStreak_FromCanonicalPresentationState()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-04-04T10:00:00Z");
        long startedAtUtcMs = nowUtc.AddMinutes(-17).ToUnixTimeMilliseconds();
        LocalCurrentExperienceState state = LocalCurrentExperienceStateAdapter.Build(
            BuildStatus(visualMode: "DEEP_WORK"),
            BuildSnapshot(
                sustainedAttentionSeconds: 780d,
                analyzedStateAvailable: true,
                stateFlagsJson: $$"""{"schema_ver":1,"deep_work_active":true,"deep_work_started_at_utc_ms":{{startedAtUtcMs}}}""",
                deepWorkActive: true),
            liveSession: null,
            recentEvents: [],
            nowUtc: nowUtc);

        Assert.Equal(LocalCurrentExperienceMode.DeepWork, state.Mode);
        Assert.True(state.DeepWorkActive);
        Assert.False(state.OverloadActive);
        Assert.True(state.ShowDeepWorkStreak);
        Assert.Contains("Deep Work streak: 17m", state.DeepWorkStreakText, StringComparison.Ordinal);
        Assert.Equal("High", state.ConfidenceLabel);
    }

    [Fact]
    public void Build_FindsRecentRecoveryEvent_AndPreservesPartialCaptureFacts()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-04-04T10:00:00Z");
        LocalApiEventResponse recoveryEvent = new(
            Id: 72,
            Type: "OVERLOAD_RECOVERED",
            TsUtc: nowUtc.AddSeconds(-25),
            IdempotencyKey: "event-72",
            WhyJson: "{}",
            StateFlagsJson: null,
            TimestampUtc: nowUtc.AddSeconds(-25),
            Action: "overload_recovered",
            TriggerState: "overload",
            TriggerValue: 0.12d,
            Confidence: 0.8d,
            DurationSeconds: null,
            EscalationLevel: "Inform",
            Reversible: false,
            Reason: string.Empty,
            Contributors: [],
            InterventionId: string.Empty,
            PolicyId: string.Empty,
            Domain: "overload",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: "Recovery complete.",
            HowToUndo: string.Empty,
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "completed");
        LocalCurrentExperienceState state = LocalCurrentExperienceStateAdapter.Build(
            BuildStatus(),
            BuildSnapshot(
                sustainedAttentionSeconds: 420d,
                analyzedStateAvailable: true,
                partialData: true,
                missingMajorChannels: ["keyboard"]),
            liveSession: null,
            recentEvents: [recoveryEvent],
            nowUtc: nowUtc);

        Assert.True(state.PartialData);
        Assert.Equal(["keyboard"], state.MissingMajorChannels);
        Assert.True(state.HasRecentRecoveryEvent);
        Assert.Equal(72, state.RecentRecoveryEventId);
        Assert.Equal("Recovery complete.", state.RecentRecoveryMessage);
    }

    private static LocalApiStatusResponse BuildStatus(string visualMode = "NORMAL")
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "v1",
            ConsentTimestampUtc: DateTimeOffset.Parse("2026-04-04T09:00:00Z"),
            ConsentDisclosureHash: "hash",
            BackendConnected: true,
            FocusStrength: 0.75d,
            OverloadLikelihood: 0.24d,
            LastAction: string.Empty,
            AgentVersion: "test",
            SchemaVersion: "1",
            PolicyVersion: "p1",
            LastEventTimestampUtc: null,
            BackendInstanceId: "backend-1",
            BackendProcessId: 42,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: DateTimeOffset.Parse("2026-04-04T09:59:58Z"),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 321,
            UserAgentSessionId: "session-1",
            KeyboardSignalActive: true,
            MouseSignalActive: true,
            LastInputEventUtc: DateTimeOffset.Parse("2026-04-04T09:59:55Z"),
            LastInputAgentInstanceId: "ua-1",
            LastInputSessionId: "session-1",
            OwnershipWarning: null,
            CaptureFailureReason: null,
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: false,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: false,
            BrightnessActionReady: true,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: true,
            BrightnessCurrentLevel: 70,
            BrightnessTargetLevel: 70,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: true,
            ColdStart: false,
            OverloadActive: false,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: visualMode,
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: DateTimeOffset.Parse("2026-04-04T10:00:00Z"),
            TrustTier: CalibrationTruth.TrustMedium,
            RuntimeStateFreshness: "fresh",
            RuntimeStateRefreshError: null);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        double sustainedAttentionSeconds,
        bool analyzedStateAvailable,
        double? focusEpisodeSeconds = null,
        string? stateFlagsJson = null,
        bool deepWorkActive = false,
        bool partialData = false,
        IReadOnlyList<string>? missingMajorChannels = null)
    {
        return new LocalApiCurrentSnapshotResponse(
            TimestampUtc: DateTimeOffset.Parse("2026-04-04T10:00:00Z"),
            TypingBurstIntensity: 0.2d,
            ScrollVelocityIndex: 0.1d,
            FocusSwitchRate: 0.1d,
            SmoothedTypingBurstIntensity: 0.2d,
            SmoothedScrollVelocityIndex: 0.1d,
            SmoothedFocusSwitchRate: 0.1d,
            FocusStrength: 0.78d,
            OverloadLikelihood: 0.22d,
            FatigueProbability: 0.15d,
            CognitiveStability: 0.77d,
            DominantStateValue: 0.78d,
            DominantStateName: "focus",
            Confidence: 0.81d,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus"],
            SustainedAttentionDurationSec: sustainedAttentionSeconds,
            IdleFragmentationIndex: 0.1d,
            FocusChoppinessIndex: 0.08d,
            OverloadActive: false,
            StateFlagsJson: stateFlagsJson,
            ContributorsJson: "{\"schema_ver\":1}",
            BaselineReady: true,
            BaselineReadyPct: 1.0d,
            AvgQuality2m: 0.9d,
            ColdStart: false,
            CalibrationStage: CalibrationTruth.StageCalibrated,
            SystemActionsEnabled: true,
            TrustTier: CalibrationTruth.TrustHigh,
            CurrentQualityScore: 0.9d,
            PartialData: partialData,
            MissingMajorChannels: missingMajorChannels ?? [],
            CooldownsRemaining: null,
            DeepWorkActive: deepWorkActive,
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            FComponent: 0.2d,
            IComponent: 0.45d,
            VComponent: 0.18d,
            TaskContinuityScore: focusEpisodeSeconds.HasValue && focusEpisodeSeconds.Value > 0d ? 0.78d : 0d,
            TaskContinuitySeconds: focusEpisodeSeconds ?? sustainedAttentionSeconds,
            ContinuityState: focusEpisodeSeconds.HasValue && focusEpisodeSeconds.Value > 0d ? "keep" : "reset",
            RuntimeStateFreshness: "fresh",
            RuntimeStateRefreshError: null,
            AnalyzedStateAvailable: analyzedStateAvailable,
            ModelSource: analyzedStateAvailable ? "analyzed_state" : "brain_fallback",
            FocusEpisodeQuality: focusEpisodeSeconds.HasValue && focusEpisodeSeconds.Value > 0d ? 0.78d : 0d,
            FocusEpisodeSeconds: focusEpisodeSeconds ?? sustainedAttentionSeconds,
            FocusEpisodeState: focusEpisodeSeconds.HasValue && focusEpisodeSeconds.Value > 0d ? "keep" : "reset");
    }

    private static LocalApiLiveSessionStateResponse BuildLiveSession(DateTimeOffset nowUtc, DateTimeOffset focusStartedAtUtc)
    {
        return new LocalApiLiveSessionStateResponse(
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-20),
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            AgentInstanceId: "ua-1",
            SessionId: "session-1",
            KeyboardActive: true,
            MouseActive: false,
            UserActive: true,
            Idle: false,
            IdleSeconds: 0.5d,
            FocusAppHash: "a1b2c3d4",
            Freshness: "fresh",
            DegradedReason: null,
            FocusStartedAtUtc: focusStartedAtUtc);
    }
}
