using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class TodayPageCopyBuilderTests
{
    [Fact]
    public void ResolveTrackingState_ReturnsTrackingLimited_WhenSnapshotIsStale()
    {
        LocalApiStatusResponse status = BuildStatus(freshness: "fresh");
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(freshness: "stale", partialData: false);
        LocalApiLiveSessionStateResponse liveSession = BuildLiveSession(freshness: "fresh");

        TodayTrackingState state = TodayPageCopyBuilder.ResolveTrackingState(status, snapshot, liveSession);

        Assert.Equal(TodayTrackingState.TrackingLimited, state);
    }

    [Fact]
    public void BuildReadableExplanation_TranslatesCachedSnapshotIntoUserLanguage()
    {
        string explanation = TodayPageCopyBuilder.BuildReadableExplanation(
            TodayTrackingState.TrackingLimited,
            "Service alive, using cached snapshot.");

        Assert.Equal(
            "Ari is still tracking, but this page is using a cached snapshot while fresh analysis catches up.",
            explanation);
    }

    [Fact]
    public void BuildReadableExplanation_ExplainsUnavailableAuthFailure()
    {
        string explanation = TodayPageCopyBuilder.BuildReadableExplanation(
            TodayTrackingState.Unavailable,
            "API auth mismatch");

        Assert.Equal(
            "Control Center cannot verify access to the local service. Open System to reconnect it.",
            explanation);
    }

    [Fact]
    public void BuildStateLabel_ShowsPausedUntil_WhenPauseDeadlineExists()
    {
        DateTimeOffset pauseUntil = DateTimeOffset.UtcNow.AddMinutes(20);

        string label = TodayPageCopyBuilder.BuildStateLabel(TodayTrackingState.Paused, pauseUntil);

        Assert.StartsWith("Paused until ", label, StringComparison.Ordinal);
    }

    private static LocalApiStatusResponse BuildStatus(string freshness)
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "v1",
            ConsentTimestampUtc: DateTimeOffset.UtcNow,
            ConsentDisclosureHash: "hash",
            BackendConnected: true,
            FocusStrength: 0.7,
            OverloadLikelihood: 0.2,
            LastAction: string.Empty,
            AgentVersion: "test",
            SchemaVersion: "1",
            PolicyVersion: "p1",
            LastEventTimestampUtc: null,
            BackendInstanceId: "backend-1",
            BackendProcessId: 1,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: DateTimeOffset.UtcNow,
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 123,
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
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: "NORMAL",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: DateTimeOffset.UtcNow,
            TrustTier: CalibrationTruth.TrustMedium,
            RuntimeStateFreshness: freshness,
            RuntimeStateRefreshError: null);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(string freshness, bool partialData)
    {
        return new LocalApiCurrentSnapshotResponse(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: 0.2,
            ScrollVelocityIndex: 0.1,
            FocusSwitchRate: 0.1,
            SmoothedTypingBurstIntensity: 0.2,
            SmoothedScrollVelocityIndex: 0.1,
            SmoothedFocusSwitchRate: 0.1,
            FocusStrength: 0.75,
            OverloadLikelihood: 0.25,
            FatigueProbability: 0.10,
            CognitiveStability: 0.8,
            DominantStateValue: 0.75,
            DominantStateName: "focus",
            Confidence: 0.85,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus"],
            SustainedAttentionDurationSec: 300d,
            IdleFragmentationIndex: 0.15,
            FocusChoppinessIndex: 0.10,
            OverloadActive: false,
            StateFlagsJson: null,
            ContributorsJson: null,
            BaselineReady: true,
            BaselineReadyPct: 1d,
            AvgQuality2m: 0.9,
            ColdStart: false,
            CalibrationStage: CalibrationTruth.StageCalibrated,
            SystemActionsEnabled: true,
            TrustTier: CalibrationTruth.TrustHigh,
            CurrentQualityScore: 0.91,
            PartialData: partialData,
            MissingMajorChannels: [],
            CooldownsRemaining: null,
            DeepWorkActive: false,
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            FComponent: 0.1,
            IComponent: 0.2,
            VComponent: 0.1,
            RuntimeStateFreshness: freshness,
            RuntimeStateRefreshError: null,
            AnalyzedStateAvailable: true,
            ModelSource: "analyzed_state");
    }

    private static LocalApiLiveSessionStateResponse BuildLiveSession(string freshness)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new LocalApiLiveSessionStateResponse(
            UpdatedAtUtc: now,
            SessionStartedAtUtc: now.AddMinutes(-10),
            LastInputEventUtc: now.AddSeconds(-1),
            AgentInstanceId: "ua-1",
            SessionId: "session-1",
            KeyboardActive: true,
            MouseActive: false,
            UserActive: true,
            Idle: false,
            IdleSeconds: 0.5d,
            FocusAppHash: "focus-hash",
            Freshness: freshness,
            DegradedReason: null,
            FocusStartedAtUtc: now.AddMinutes(-3));
    }
}
