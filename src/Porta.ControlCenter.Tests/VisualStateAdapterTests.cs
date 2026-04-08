using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class VisualStateAdapterTests
{
    [Fact]
    public void Build_UsesDurableFocusEpisode_WhenLiveTimerWouldBounce()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            sustainedAttentionSeconds: 10d,
            focusEpisodeSeconds: 600d,
            analyzedStateAvailable: false);
        LocalApiLiveSessionStateResponse liveSession = BuildLiveSession(
            nowUtc,
            focusStartedAtUtc: nowUtc.AddMinutes(-4));

        PortaVisualState state = VisualStateAdapter.Build(
            status,
            snapshot,
            liveSession,
            recentEvents: [],
            fallbackExplanation: string.Empty,
            nowUtc: nowUtc);

        Assert.True(state.CurrentFocusSessionSeconds.HasValue);
        Assert.Equal(600d, state.CurrentFocusSessionSeconds.Value);
        Assert.Equal("Current focus session: 10 min", state.CurrentFocusSessionText);
        Assert.False(state.SnapshotAnalysisReady);
    }

    [Fact]
    public void Build_FallsBackToAnalyzedSnapshotFocus_WhenLiveTimerIsUnavailable()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            sustainedAttentionSeconds: 180d,
            analyzedStateAvailable: true);

        PortaVisualState state = VisualStateAdapter.Build(
            status,
            snapshot,
            liveSession: null,
            recentEvents: [],
            fallbackExplanation: string.Empty,
            nowUtc: nowUtc);

        Assert.Equal(180d, state.CurrentFocusSessionSeconds);
        Assert.Equal("Current focus session: 3 min", state.CurrentFocusSessionText);
        Assert.True(state.SnapshotAnalysisReady);
    }

    [Fact]
    public void Build_UsesSharedPresentedMode_WhenSnapshotAndStatusNeedCanonicalMapping()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus(visualMode: "DEEP_WORK", deepWorkActive: true);
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            sustainedAttentionSeconds: 600d,
            analyzedStateAvailable: true,
            deepWorkActive: true);

        PortaVisualState state = VisualStateAdapter.Build(
            status,
            snapshot,
            liveSession: null,
            recentEvents: [],
            fallbackExplanation: string.Empty,
            nowUtc: nowUtc);

        Assert.Equal(PortaVisualMode.DeepWork, state.Mode);
        Assert.True(state.DeepWorkActive);
        Assert.False(state.OverloadActive);
        Assert.Equal("High", state.ConfidenceLabel);
    }

    private static LocalApiStatusResponse BuildStatus(string visualMode = "NORMAL", bool deepWorkActive = false)
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "v1",
            ConsentTimestampUtc: DateTimeOffset.Parse("2026-03-29T11:00:00Z"),
            ConsentDisclosureHash: "hash",
            BackendConnected: true,
            FocusStrength: 0.72,
            OverloadLikelihood: 0.22,
            LastAction: string.Empty,
            AgentVersion: "test",
            SchemaVersion: "1",
            PolicyVersion: "p1",
            LastEventTimestampUtc: null,
            BackendInstanceId: "backend-1",
            BackendProcessId: 42,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: DateTimeOffset.UtcNow,
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 321,
            UserAgentSessionId: "session-1",
            KeyboardSignalActive: true,
            MouseSignalActive: true,
            LastInputEventUtc: DateTimeOffset.UtcNow,
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
            DeepWorkActive: deepWorkActive,
            DeepWorkStreakSeconds: 0,
            VisualMode: visualMode,
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: DateTimeOffset.UtcNow,
            TrustTier: CalibrationTruth.TrustMedium,
            RuntimeStateFreshness: "fresh",
            RuntimeStateRefreshError: null);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        double sustainedAttentionSeconds,
        bool analyzedStateAvailable,
        double? focusEpisodeSeconds = null,
        bool deepWorkActive = false)
    {
        return new LocalApiCurrentSnapshotResponse(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: 0.2,
            ScrollVelocityIndex: 0.1,
            FocusSwitchRate: 0.1,
            SmoothedTypingBurstIntensity: 0.2,
            SmoothedScrollVelocityIndex: 0.1,
            SmoothedFocusSwitchRate: 0.1,
            FocusStrength: 0.78,
            OverloadLikelihood: 0.32,
            FatigueProbability: 0.15,
            CognitiveStability: 0.77,
            DominantStateValue: 0.78,
            DominantStateName: "focus",
            Confidence: 0.81,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus"],
            SustainedAttentionDurationSec: sustainedAttentionSeconds,
            IdleFragmentationIndex: 0.1,
            FocusChoppinessIndex: 0.08,
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
            CurrentQualityScore: 0.9,
            PartialData: false,
            MissingMajorChannels: [],
            CooldownsRemaining: null,
            DeepWorkActive: deepWorkActive,
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            FComponent: 0.2,
            IComponent: 0.45,
            VComponent: 0.18,
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

    private static LocalApiLiveSessionStateResponse BuildLiveSession(
        DateTimeOffset nowUtc,
        DateTimeOffset focusStartedAtUtc)
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
