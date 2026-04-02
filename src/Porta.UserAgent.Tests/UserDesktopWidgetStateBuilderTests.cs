using System.Linq;
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

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot, null, signals, degraded);

        Assert.Equal(UserDesktopWidgetMode.Limited, state.Mode);
        Assert.Equal("Attention", state.Title);
        Assert.Contains("partial activity", state.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Focus 4m", state.PrimaryMetricText);
        Assert.Equal("Overload 18%", state.SecondaryMetricText);
        Assert.Equal("Today 4m", state.TertiaryMetricText);
        Assert.Equal(string.Empty, state.QuaternaryMetricText);
        Assert.Equal(string.Empty, state.FooterText);
        Assert.False(state.NotificationsSuppressed);
        Assert.False(state.BrightnessAdjusted);
        Assert.NotNull(state.CriticalState);
        Assert.Equal("Open Health", state.CriticalState!.PrimaryLabel);
        Assert.Equal(UserDesktopWidgetAction.OpenHealth, state.CriticalState.PrimaryAction);
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

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, null, null, signals, degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Paused, state.Mode);
        Assert.Equal("Paused", state.Title);
        Assert.True(state.CanResume);
        Assert.False(state.CanPause);
        Assert.Equal($"Paused until {pauseUntilUtc.ToLocalTime():h:mm tt}.", state.StatusText);
        Assert.Equal("Focus paused", state.PrimaryMetricText);
        Assert.Equal("Overload paused", state.SecondaryMetricText);
        Assert.Equal("Today paused", state.TertiaryMetricText);
        Assert.Equal(string.Empty, state.QuaternaryMetricText);
        Assert.Equal(string.Empty, state.FooterText);
    }

    [Fact]
    public void Build_UsesCriticalStateWhenServiceIsOffline()
    {
        LocalApiStatusResponse status = BuildStatus(serviceRunning: false);
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.12,
            sustainedAttentionSeconds: 180,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Unavailable,
            TooltipText: "PORTA | Waiting for service data",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            null,
            signals,
            degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Limited, state.Mode);
        Assert.NotNull(state.CriticalState);
        Assert.Equal("Core service offline", state.CriticalState!.Title);
        Assert.Equal(UserDesktopWidgetAction.OpenHealth, state.CriticalState.PrimaryAction);
        Assert.Equal(UserDesktopWidgetAction.RetryCritical, state.CriticalState.SecondaryAction);
        Assert.Equal(UserDesktopWidgetAction.OpenSettings, state.CriticalState.TertiaryAction);
        Assert.Contains("service offline", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_UsesCriticalStateForRecentNotificationFailure()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T12:00:00Z");
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.32,
            sustainedAttentionSeconds: 600,
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
        LocalApiEventResponse recentFailure = BuildEvent(
            id: 77,
            action: "notification_restore_failed",
            tsUtc: nowUtc.AddMinutes(-4));

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            null,
            signals,
            degradedToast: null,
            recentEvents: [recentFailure],
            timeline: null,
            settings: null,
            nowUtc: nowUtc);

        Assert.Equal(UserDesktopWidgetMode.Limited, state.Mode);
        Assert.NotNull(state.CriticalState);
        Assert.Equal("Notifications need attention", state.CriticalState!.Title);
        Assert.Equal(UserDesktopWidgetAction.OpenSettings, state.CriticalState.PrimaryAction);
        Assert.Equal(UserDesktopWidgetAction.OpenHealth, state.CriticalState.SecondaryAction);
        Assert.Equal(UserDesktopWidgetAction.RetryCritical, state.CriticalState.TertiaryAction);
        Assert.Contains("restore windows notification settings", state.StatusText, StringComparison.OrdinalIgnoreCase);
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

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot, null, signals, degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Overload, state.Mode);
        Assert.Equal(string.Empty, state.Title);
        Assert.Equal("Overload is elevated.", state.StatusText);
        Assert.Equal("Focus 25m", state.PrimaryMetricText);
        Assert.Equal("Overload 74%", state.SecondaryMetricText);
        Assert.Equal("Today 25m", state.TertiaryMetricText);
        Assert.Equal(string.Empty, state.QuaternaryMetricText);
        Assert.Equal(string.Empty, state.FooterText);
        Assert.False(state.NotificationsSuppressed);
        Assert.False(state.BrightnessAdjusted);
    }

    [Fact]
    public void Build_UsesTimelineFactsForExpandedWidget()
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
        LocalApiCurrentSnapshotResponse[] timelineSnapshots = Enumerable
            .Range(0, 120)
            .Select(index => snapshot with
            {
                TimestampUtc = nowUtc.AddMinutes(-(119 - index)),
                SustainedAttentionDurationSec = 0
            })
            .ToArray();
        LocalApiTimelineResponse timeline = new(
            FromUtc: nowUtc.AddHours(-2),
            ToUtc: nowUtc,
            BucketSeconds: 60,
            Snapshots: timelineSnapshots,
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
            null,
            signals,
            degradedToast: null,
            recentEvents: [],
            timeline: timeline,
            settings: settings,
            nowUtc: nowUtc);

        Assert.Equal("Focus 25m", state.PrimaryMetricText);
        Assert.Equal("Overload 42%", state.SecondaryMetricText);
        Assert.Equal("Today 2h 00m", state.TertiaryMetricText);
        Assert.Equal(string.Empty, state.QuaternaryMetricText);
        Assert.Equal(string.Empty, state.FooterText);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void Build_UsesCachedSnapshotVocabularyWhenRuntimeStateIsStale()
    {
        LocalApiStatusResponse status = BuildStatus(
            runtimeStateFreshness: "stale",
            runtimeStateRefreshError: "db_busy");
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.42,
            sustainedAttentionSeconds: 900,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false,
            runtimeStateFreshness: "stale",
            runtimeStateRefreshError: "db_busy");
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot, null, signals, degradedToast: null);

        Assert.Equal("Cached snapshot. DB busy, retrying.", state.StatusText);
        Assert.Null(state.CriticalState);
    }

    [Fact]
    public void Build_PrefersFreshLiveSessionFactsForWidgetSurface()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.42,
            sustainedAttentionSeconds: 900,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        LocalApiLiveSessionStateResponse liveSession = new(
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-17),
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            AgentInstanceId: "ua-1",
            SessionId: "session-1",
            KeyboardActive: true,
            MouseActive: false,
            UserActive: true,
            Idle: false,
            IdleSeconds: 1.2d,
            FocusAppHash: "a1b2c3d4",
            Freshness: "fresh",
            DegradedReason: null,
            FocusStartedAtUtc: nowUtc.AddMinutes(-3));
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            liveSession,
            signals,
            degradedToast: null,
            nowUtc: nowUtc);

        Assert.Equal("Focused now | live 3m | typing active | mouse idle.", state.StatusText);
        Assert.Equal("Focus 3m", state.PrimaryMetricText);
        Assert.Equal("Overload 42%", state.SecondaryMetricText);
        Assert.StartsWith("Input ", state.TertiaryMetricText, StringComparison.Ordinal);
        Assert.StartsWith("Session 17m | Typing active | Mouse idle", state.FooterText, StringComparison.Ordinal);
        Assert.Null(state.CriticalState);
    }

    [Fact]
    public void Build_HoldsOverloadMetricPendingUntilAnalyzedSnapshotIsReady()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.50,
            sustainedAttentionSeconds: 0,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false,
            analyzedStateAvailable: false);
        LocalApiLiveSessionStateResponse liveSession = new(
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-11),
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            AgentInstanceId: "ua-1",
            SessionId: "session-1",
            KeyboardActive: true,
            MouseActive: true,
            UserActive: true,
            Idle: false,
            IdleSeconds: 0.4d,
            FocusAppHash: "abc123",
            Freshness: "fresh",
            DegradedReason: null,
            FocusStartedAtUtc: nowUtc.AddMinutes(-2));
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            liveSession,
            signals,
            degradedToast: null,
            nowUtc: nowUtc);

        Assert.Equal("Focus 2m", state.PrimaryMetricText);
        Assert.Equal("Overload pending", state.SecondaryMetricText);
    }

    [Fact]
    public void Build_UsesProcessedSnapshotForOverloadEvenWhenLiveSessionIsFresh()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus(overloadActive: false);
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.74,
            sustainedAttentionSeconds: 420,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        LocalApiLiveSessionStateResponse liveSession = new(
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-9),
            LastInputEventUtc: nowUtc.AddSeconds(-2),
            AgentInstanceId: "ua-1",
            SessionId: "session-1",
            KeyboardActive: false,
            MouseActive: true,
            UserActive: true,
            Idle: false,
            IdleSeconds: 0.8d,
            FocusAppHash: null,
            Freshness: "fresh",
            DegradedReason: null);
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            liveSession,
            signals,
            degradedToast: null,
            nowUtc: nowUtc);

        Assert.Equal("Overload 74%", state.SecondaryMetricText);
        Assert.Equal("Focus active", state.PrimaryMetricText);
        Assert.StartsWith("Input ", state.TertiaryMetricText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UsesRecentCriticalEventEvenWhenLiveSessionIsFresh()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.28,
            sustainedAttentionSeconds: 720,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        LocalApiLiveSessionStateResponse liveSession = new(
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-21),
            LastInputEventUtc: nowUtc.AddSeconds(-1),
            AgentInstanceId: "ua-1",
            SessionId: "session-1",
            KeyboardActive: true,
            MouseActive: true,
            UserActive: true,
            Idle: false,
            IdleSeconds: 0.5d,
            FocusAppHash: "a1",
            Freshness: "fresh",
            DegradedReason: null);
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);
        LocalApiEventResponse recentFailure = BuildEvent(
            id: 91,
            action: "notification_restore_failed",
            tsUtc: nowUtc.AddMinutes(-2));

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            liveSession,
            signals,
            degradedToast: null,
            recentEvents: [recentFailure],
            nowUtc: nowUtc);

        Assert.NotNull(state.CriticalState);
        Assert.Equal("Notifications need attention", state.CriticalState!.Title);
        Assert.Contains("restore windows notification settings", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalApiStatusResponse BuildStatus(
        bool paused = false,
        DateTimeOffset? pauseUntilUtc = null,
        bool overloadActive = false,
        string trustTier = CalibrationTruth.TrustMedium,
        bool serviceRunning = true,
        string runtimeStateFreshness = "fresh",
        string? runtimeStateRefreshError = null)
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: serviceRunning,
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
            TrustTier: trustTier,
            RuntimeStateFreshness: runtimeStateFreshness,
            RuntimeStateRefreshError: runtimeStateRefreshError);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        double overloadLikelihood,
        double sustainedAttentionSeconds,
        bool deepWorkActive,
        bool notificationsSuppressed,
        bool brightnessAdjusted,
        string runtimeStateFreshness = "fresh",
        string? runtimeStateRefreshError = null,
        bool analyzedStateAvailable = true)
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
            VComponent: 0.18,
            RuntimeStateFreshness: runtimeStateFreshness,
            RuntimeStateRefreshError: runtimeStateRefreshError,
            AnalyzedStateAvailable: analyzedStateAvailable,
            ModelSource: analyzedStateAvailable ? "analyzed_state" : "brain_fallback");
    }

    private static LocalApiEventResponse BuildEvent(long id, string action, DateTimeOffset tsUtc)
    {
        return new LocalApiEventResponse(
            Id: id,
            Type: action.ToUpperInvariant(),
            TsUtc: tsUtc,
            IdempotencyKey: $"event-{id}",
            WhyJson: "{}",
            StateFlagsJson: null,
            TimestampUtc: tsUtc,
            Action: action,
            TriggerState: "runtime",
            TriggerValue: 0.0,
            Confidence: 0.8,
            DurationSeconds: null,
            EscalationLevel: "Inform",
            Reversible: false,
            Reason: string.Empty,
            Contributors: [],
            InterventionId: $"intv-{id}",
            PolicyId: "policy-v1",
            Domain: "runtime",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: action,
            HowToUndo: string.Empty,
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "activated",
            CommandType: string.Empty,
            ExecutionState: "applied",
            DecidedAtUtc: tsUtc,
            AppliedAtUtc: tsUtc,
            AckedAtUtc: tsUtc,
            ExecutionErrorCode: string.Empty,
            ExecutorAgentInstanceId: "ua-1");
    }
}
