using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetStateBuilderTests
{
    [Fact]
    public void Build_UsesLimitedModeWhenCaptureIsDegraded()
    {
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.18,
            sustainedAttentionSeconds: 240,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);
        UserCommunicationToast degraded = new(
            Key: "capture:partial",
            Title: "Ari limited",
            Message: "Ari is receiving partial activity only. Missing keyboard.");

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot, signals, degraded);

        Assert.Equal(UserDesktopWidgetMode.Limited, state.Mode);
        Assert.Equal("Ari Limited", state.Title);
        Assert.Contains("partial activity", state.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Focus 4m", state.PrimaryMetricText);
        Assert.Equal("Overload 18%", state.SecondaryMetricText);
        Assert.Equal("Break n/a", state.TertiaryMetricText);
        Assert.Equal("Ready · High trust", state.QuaternaryMetricText);
        Assert.Equal("Service healthy", state.FooterText);
    }

    [Fact]
    public void Build_UsesPausedModeAndResumeCapability()
    {
        DateTimeOffset pauseUntilUtc = DateTimeOffset.Parse("2026-03-29T14:15:00Z");
        LocalApiStatusResponse status = BuildStatus(
            paused: true,
            pauseUntilUtc: pauseUntilUtc);
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot: null, signals, degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Paused, state.Mode);
        Assert.Equal("Paused", state.Title);
        Assert.True(state.CanResume);
        Assert.False(state.CanPause);
        Assert.Equal("Tracking paused", state.PrimaryMetricText);
        Assert.Equal($"Until {pauseUntilUtc.ToLocalTime():h:mm tt}", state.SecondaryMetricText);
        Assert.Equal("Break now", state.TertiaryMetricText);
        Assert.Equal("Health healthy", state.QuaternaryMetricText);
        Assert.Equal("Service healthy", state.FooterText);
    }

    [Fact]
    public void Build_UsesOverloadMetricsWhenOverloadIsActive()
    {
        LocalApiStatusResponse status = BuildStatus(
            overloadActive: true,
            trustTier: CalibrationTruth.TrustHigh);
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.74,
            sustainedAttentionSeconds: 1500,
            deepWorkActive: false,
            notificationsSuppressed: true,
            brightnessAdjusted: true);
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Overload,
            TooltipText: "PORTA | Overload regulation active",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot, signals, degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Overload, state.Mode);
        Assert.Equal("Overload", state.Title);
        Assert.Equal("Focus 25m", state.PrimaryMetricText);
        Assert.Equal("Overload 74%", state.SecondaryMetricText);
        Assert.Equal("Break n/a", state.TertiaryMetricText);
        Assert.Equal("Ready · High trust", state.QuaternaryMetricText);
        Assert.Equal("Service healthy", state.FooterText);
        Assert.True(state.NotificationsSuppressed);
        Assert.True(state.BrightnessAdjusted);
    }

    [Fact]
    public void Build_UsesTimelineAndSettingsFactsForExpandedWidget()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T12:00:00Z");
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.42,
            sustainedAttentionSeconds: 1500,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);
        LocalApiTimelineResponse timeline = new(
            FromUtc: nowUtc.AddHours(-4),
            ToUtc: nowUtc,
            BucketSeconds: 60,
            Snapshots:
            [
                snapshot with
                {
                    TimestampUtc = nowUtc.AddMinutes(-25),
                    SustainedAttentionDurationSec = 0
                },
                snapshot
            ],
            Interventions: []);
        LocalApiSettingsResponse settings = new(
            RegulationIntensity: "balanced",
            PrivacyLevel: "patterns_only",
            NotificationReductionEnabled: true,
            FocusModeEnabled: true,
            AnimationReductionEnabled: false,
            TemporaryAppRestrictionEnabled: false,
            BaselineRecalibrationActive: false,
            BaselineRecalibrationStartedUtc: null,
            BaselineRecalibrationUntilUtc: null,
            UpdatedAtUtc: nowUtc);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            signals,
            degradedToast: null,
            recentEvents: [],
            timeline: timeline,
            settings: settings,
            nowUtc: nowUtc);

        Assert.Equal("Focus 25m", state.PrimaryMetricText);
        Assert.Equal("Overload 42%", state.SecondaryMetricText);
        Assert.Equal("Break 25m ago", state.TertiaryMetricText);
        Assert.Equal("Ready · High trust", state.QuaternaryMetricText);
        Assert.Equal("Service healthy · Focus mode on, Quiet guidance", state.FooterText);
        Assert.Contains("focus mode enabled", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalApiStatusResponse BuildStatus(
        bool paused = false,
        DateTimeOffset? pauseUntilUtc = null,
        bool overloadActive = false,
        string trustTier = CalibrationTruth.TrustMedium)
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: paused,
            PauseUntilUtc: pauseUntilUtc,
            ConsentActive: true,
            ConsentVersion: "v1",
            ConsentTimestampUtc: DateTimeOffset.Parse("2026-03-29T11:00:00Z"),
            ConsentDisclosureHash: "hash",
            BackendConnected: true,
            FocusStrength: 0.72,
            OverloadLikelihood: overloadActive ? 0.74 : 0.22,
            LastAction: string.Empty,
            AgentVersion: "test",
            SchemaVersion: "1",
            PolicyVersion: "p1",
            LastEventTimestampUtc: null,
            BackendInstanceId: "backend-1",
            BackendProcessId: 42,
            CaptureContext: "UserSession",
            UserAgentHeartbeatUpdatedAtUtc: DateTimeOffset.Parse("2026-03-29T11:59:58Z"),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 321,
            UserAgentSessionId: "session-1",
            KeyboardSignalActive: true,
            MouseSignalActive: true,
            LastInputEventUtc: DateTimeOffset.Parse("2026-03-29T11:59:55Z"),
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
            OverloadActive: overloadActive,
            DeepWorkActive: false,
            DeepWorkStreakSeconds: 0,
            VisualMode: overloadActive ? "OVERLOAD" : "NORMAL",
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: DateTimeOffset.Parse("2026-03-29T12:00:00Z"),
            TrustTier: trustTier);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        double overloadLikelihood,
        double sustainedAttentionSeconds,
        bool deepWorkActive,
        bool notificationsSuppressed,
        bool brightnessAdjusted)
    {
        return new LocalApiCurrentSnapshotResponse(
            TimestampUtc: DateTimeOffset.Parse("2026-03-29T12:00:00Z"),
            TypingBurstIntensity: 0.2,
            ScrollVelocityIndex: 0.1,
            FocusSwitchRate: 0.1,
            SmoothedTypingBurstIntensity: 0.2,
            SmoothedScrollVelocityIndex: 0.1,
            SmoothedFocusSwitchRate: 0.1,
            FocusStrength: 0.78,
            OverloadLikelihood: overloadLikelihood,
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
            OverloadActive: overloadLikelihood >= 0.72d,
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
            NotificationsSuppressed: notificationsSuppressed,
            BrightnessAdjusted: brightnessAdjusted,
            FComponent: 0.2,
            IComponent: 0.45,
            VComponent: 0.18);
    }
}
