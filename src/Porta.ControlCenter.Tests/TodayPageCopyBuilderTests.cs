using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class TodayPageCopyBuilderTests
{
    /// <summary>
    /// Task attribution (2026-09-01) — the line that stops the change reading as lost work.
    ///
    /// Yadin's wording, kept exactly: "1h 20m worked, not assigned to a task". The number comes
    /// from the same formatter the Work Today card uses, so two figures on one screen can never be
    /// rounded differently.
    /// </summary>
    [Fact]
    public void BuildUnassignedLine_ReadsAsYadinWroteIt()
    {
        Assert.Equal(
            "1h 20m worked, not assigned to a task",
            TodayPageCopyBuilder.BuildUnassignedLine(4800d));
    }

    [Fact]
    public void BuildUnassignedLine_SaysNothingOnAGoodDay()
    {
        // Zero unassigned time is the normal case. A line reading "0m not assigned" every day is
        // noise, and noise is how a line that matters gets ignored on the day it appears.
        Assert.Null(TodayPageCopyBuilder.BuildUnassignedLine(0d));

        // Under a minute is a rounding artefact, not a finding: the gate needs ten continuous
        // minutes before it holds anything, so a real hold can never be this small.
        Assert.Null(TodayPageCopyBuilder.BuildUnassignedLine(45d));

        // And a broken reading is not a claim about the day.
        Assert.Null(TodayPageCopyBuilder.BuildUnassignedLine(double.NaN));
        Assert.Null(TodayPageCopyBuilder.BuildUnassignedLine(-600d));
    }

    [Fact]
    public void BuildUnassignedLine_NeverSaysUnattributed()
    {
        // unattributed_hours already exists and means something else entirely. One word on the
        // screen computed two incompatible ways is worse than no word at all.
        string line = TodayPageCopyBuilder.BuildUnassignedLine(3600d)!;
        Assert.DoesNotContain("unattributed", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not assigned to a task", line, StringComparison.Ordinal);
    }

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
            "Today is showing the last confirmed update while your recent activity catches up.",
            explanation);
    }

    [Fact]
    public void BuildReadableExplanation_ExplainsUnavailableAuthFailure()
    {
        string explanation = TodayPageCopyBuilder.BuildReadableExplanation(
            TodayTrackingState.Unavailable,
            "API auth mismatch");

        Assert.Equal(
            "Today cannot verify access to the local service. Open System to reconnect it.",
            explanation);
    }

    [Fact]
    public void BuildEmptyStateGuidance_PointsToTrackingAndSystem_WhenTrackingIsLimited()
    {
        string guidance = TodayPageCopyBuilder.BuildEmptyStateGuidance(TodayTrackingState.TrackingLimited);

        Assert.Equal(
            "Keep working for a minute, open Tracking to review tracking quality, or open System for recovery details.",
            guidance);
    }

    [Fact]
    public void BuildStateLabel_ShowsPausedUntil_WhenPauseDeadlineExists()
    {
        DateTimeOffset pauseUntil = DateTimeOffset.UtcNow.AddMinutes(20);

        string label = TodayPageCopyBuilder.BuildStateLabel(TodayTrackingState.Paused, pauseUntil);

        Assert.StartsWith("Paused until ", label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StillTracking", "Still tracking")]
    [InlineData("TrackingLimited", "Tracking limited")]
    [InlineData("Paused", "Paused")]
    [InlineData("Unavailable", "Unavailable")]
    public void BuildStateLabel_UsesLockedUserFacingLabels(string stateName, string expected)
    {
        TodayTrackingState state = Enum.Parse<TodayTrackingState>(stateName, ignoreCase: false);
        string label = TodayPageCopyBuilder.BuildStateLabel(state, pauseUntilUtc: null);

        Assert.Equal(expected, label);
    }

    [Fact]
    public void BuildReadableExplanation_ExplainsNotCollectingWithoutRawPrefix()
    {
        string explanation = TodayPageCopyBuilder.BuildReadableExplanation(
            TodayTrackingState.TrackingLimited,
            "Not collecting: keyboard input is missing from live capture");

        Assert.Equal(
            "Ari is not tracking right now: keyboard input is missing from live capture.",
            explanation);
    }

    [Fact]
    public void BuildReadableExplanation_ExplainsLiveWarmupWithoutCaptureJargon()
    {
        string explanation = TodayPageCopyBuilder.BuildReadableExplanation(
            TodayTrackingState.TrackingLimited,
            "Waiting for live capture: waiting for first keyboard signal");

        Assert.Equal(
            "Ari is connected, but activity is still warming up.",
            explanation);
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
