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

        // Routine capture degradation: title stays "Today", mode stays Monitoring,
        // badge is shown, metrics remain visible.
        Assert.Equal(UserDesktopWidgetMode.Monitoring, state.Mode);
        Assert.Equal("Today", state.Title);
        Assert.True(state.ShowLimitedBadge);
        Assert.Contains("partial capture", state.StatusText, StringComparison.OrdinalIgnoreCase);
        AssertMetric(state.PrimaryMetric, "Work today", "—");
        AssertMetric(state.SecondaryMetric, "Last focus session", "4m");
        AssertMetric(state.TertiaryMetric, "Pressure", "18%");
        Assert.Equal("Confidence High | Tracking quality strong", state.FooterText);
        Assert.Null(state.CriticalState);
        AssertRoutineWidgetText(state);
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
        AssertMetric(state.PrimaryMetric, "Work today", "Paused");
        AssertMetric(state.SecondaryMetric, "Last focus session", "Paused");
        AssertMetric(state.TertiaryMetric, "Pressure", "Paused");
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
        Assert.Contains("service is offline", state.StatusText, StringComparison.OrdinalIgnoreCase);
        AssertMetric(state.PrimaryMetric, "Work today", "—");
        AssertMetric(state.SecondaryMetric, "Last focus session", "3m");
        AssertMetric(state.TertiaryMetric, "Pressure", "12%");
    }

    [Fact]
    public void Build_DoesNotPromoteNormalStartupIntoCriticalState()
    {
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Unavailable,
            TooltipText: "PORTA | Waiting for core service",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status: null,
            snapshot: null,
            liveSession: null,
            signals,
            degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Monitoring, state.Mode);
        Assert.Equal("Today", state.Title);
        Assert.Equal("Starting Ari locally.", state.StatusText);
        Assert.Null(state.CriticalState);
        AssertMetric(state.PrimaryMetric, "Work today", "—");
        AssertMetric(state.SecondaryMetric, "Last focus session", "—");
        AssertMetric(state.TertiaryMetric, "Pressure", "—");
        Assert.Equal("Confidence updating | Tracking quality updating", state.FooterText);
        AssertRoutineWidgetText(state);
    }

    [Fact]
    public void Build_DoesNotTakeOverForRecentNotificationFailure()
    {
        // Per Notification_Inventory.md section G, a runtime notification failure is
        // informational — it lives in recent events / Health / activity log and must NOT
        // seize the widget with a critical takeover. The widget stays in its normal mode.
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

        Assert.NotEqual(UserDesktopWidgetMode.Limited, state.Mode);
        Assert.Null(state.CriticalState);
        Assert.NotEqual(UserDesktopWidgetMessageSeverity.Critical, state.MessageSeverity);
        Assert.DoesNotContain("restore windows notification settings", state.StatusText, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("Today", state.Title);
        Assert.Equal("Overload is elevated.", state.StatusText);
        AssertMetric(state.PrimaryMetric, "Work today", "—");
        AssertMetric(state.SecondaryMetric, "Last focus session", "25m");
        AssertMetric(state.TertiaryMetric, "Pressure", "74%");
        Assert.Equal("Confidence High | Tracking quality strong", state.FooterText);
    }

    [Fact]
    public void Build_UsesPressureLevelWhenAvailable_AndKeepsOverloadModeOff()
    {
        LocalApiStatusResponse status = BuildStatus(overloadActive: false);
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.12,
            sustainedAttentionSeconds: 900,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false) with
        {
            PressureLevel = 0.88,
            PressureConfidence = 0.66
        };
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot, null, signals, degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Monitoring, state.Mode);
        AssertMetric(state.TertiaryMetric, "Pressure", "88%");
    }

    [Fact]
    public void Build_UsesLastFocusSessionLabel_ForParityWithControlCenter()
    {
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.18,
            sustainedAttentionSeconds: 420,
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

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(status, snapshot, null, signals, degradedToast: null);

        Assert.Equal("Last focus session", state.SecondaryMetric.Label);
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

        AssertMetric(state.PrimaryMetric, "Work today", "2h 00m");
        AssertMetric(state.SecondaryMetric, "Last focus session", "25m");
        AssertMetric(state.TertiaryMetric, "Pressure", "42%");
        Assert.Equal("Confidence High | Tracking quality strong", state.FooterText);
        Assert.Equal("Today is updating normally.", state.StatusText);
    }

    [Fact]
    public void Build_ProjectsWorkTodayForwardWhileLiveSessionRemainsActive()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-04-10T09:00:20Z");
        DateTimeOffset snapshotUtc = nowUtc.AddSeconds(-20);
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.42,
            sustainedAttentionSeconds: 1500,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false) with
        {
            TimestampUtc = snapshotUtc
        };
        LocalApiLiveSessionStateResponse liveSession = new(
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-17),
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
            FocusStartedAtUtc: nowUtc.AddMinutes(-3));
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);
        LocalApiTimelineResponse timeline = new(
            FromUtc: nowUtc.AddHours(-2),
            ToUtc: snapshotUtc,
            BucketSeconds: 60,
            Snapshots: Enumerable
                .Range(0, 120)
                .Select(index => snapshot with
                {
                    TimestampUtc = nowUtc.AddMinutes(-(119 - index)).AddSeconds(-20),
                    SustainedAttentionDurationSec = 0
                })
                .ToArray(),
            Interventions: []);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            liveSession,
            signals,
            degradedToast: null,
            recentEvents: [],
            timeline: timeline,
            settings: null,
            nowUtc: nowUtc);

        AssertMetric(state.PrimaryMetric, "Work today", "2h 00m 20s");
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

        Assert.Equal("Updating local view while analysis catches up.", state.StatusText);
        Assert.Equal("Confidence High | Tracking quality delayed", state.FooterText);
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
            brightnessAdjusted: false) with
        {
            TimestampUtc = nowUtc
        };
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

        Assert.Equal("Active now | typing active | mouse idle.", state.StatusText);
        AssertMetric(state.PrimaryMetric, "Work today", "—");
        AssertMetric(state.SecondaryMetric, "Last focus session", "15m");
        AssertMetric(state.TertiaryMetric, "Pressure", "42%");
        Assert.Equal("Confidence High | Tracking quality strong", state.FooterText);
        Assert.Null(state.CriticalState);
    }

    [Fact]
    public void Build_ProjectsFocusEpisodeForwardWhileLiveSessionRemainsActive()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-04-09T08:30:20Z");
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.42,
            sustainedAttentionSeconds: 25,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false) with
        {
            TimestampUtc = nowUtc.AddSeconds(-20),
            FocusEpisodeSeconds = 25,
            TaskContinuitySeconds = 25,
            FocusEpisodeState = "keep"
        };
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

        AssertMetric(state.SecondaryMetric, "Last focus session", "45s");
    }

    [Fact]
    public void Build_ShowsPressureEvenWhenConfidenceIsStillSettling()
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

        AssertMetric(state.PrimaryMetric, "Work today", "—");
        // Live session has been running 11 min — shown as proxy until first bucket analyzes.
        AssertMetric(state.SecondaryMetric, "Last focus session", "11m");
        AssertMetric(state.TertiaryMetric, "Pressure", "50%");
        Assert.Equal("Confidence High | Tracking quality pending", state.FooterText);
    }

    [Fact]
    public void Build_KeepsLastFocusDurationVisibleWhenLiveSessionIsIdle()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.30,
            sustainedAttentionSeconds: 180,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        LocalApiLiveSessionStateResponse liveSession = new(
            UpdatedAtUtc: nowUtc,
            SessionStartedAtUtc: nowUtc.AddMinutes(-12),
            LastInputEventUtc: nowUtc.AddMinutes(-2),
            AgentInstanceId: "ua-1",
            SessionId: "session-1",
            KeyboardActive: false,
            MouseActive: false,
            UserActive: false,
            Idle: true,
            IdleSeconds: 120d,
            FocusAppHash: null,
            Freshness: "fresh",
            DegradedReason: null,
            FocusStartedAtUtc: nowUtc.AddMinutes(-5));
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

        AssertMetric(state.SecondaryMetric, "Last focus session", "3m");
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
            brightnessAdjusted: false) with
        {
            TimestampUtc = nowUtc
        };
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

        AssertMetric(state.PrimaryMetric, "Work today", "—");
        AssertMetric(state.SecondaryMetric, "Last focus session", "7m");
        AssertMetric(state.TertiaryMetric, "Pressure", "74%");
        Assert.Equal("Confidence High | Tracking quality strong", state.FooterText);
    }

    [Fact]
    public void Build_DoesNotTakeOverForRecentNotificationFailureEvenWhenLiveSessionIsFresh()
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

        // The notification failure is informational (recent events / Health / activity log),
        // so it never seizes the widget. With a fresh live session the widget keeps showing
        // live activity rather than a critical takeover message.
        Assert.Null(state.CriticalState);
        Assert.NotEqual(UserDesktopWidgetMode.Limited, state.Mode);
        Assert.DoesNotContain("restore windows notification settings", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ShowsBadgeNotTakeover_ForRoutineStaleAndPartialStates()
    {
        // Stale analysis and partial data are routine — widget stays in Monitoring with
        // a badge, title stays "Today", and all 3 metrics remain visible.
        LocalApiStatusResponse status = BuildStatus(
            runtimeStateFreshness: "stale",
            runtimeStateRefreshError: "db_busy");
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.38,
            sustainedAttentionSeconds: 720,
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

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status, snapshot, null, signals, degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.Monitoring, state.Mode);
        Assert.Equal("Today", state.Title);
        Assert.True(state.ShowLimitedBadge);
        Assert.Null(state.CriticalState);
        Assert.Equal(UserDesktopWidgetMessageSeverity.Neutral, state.MessageSeverity);
        AssertMetric(state.SecondaryMetric, "Last focus session", "12m");
        AssertMetric(state.TertiaryMetric, "Pressure", "38%");
        AssertRoutineWidgetText(state);
    }

    [Fact]
    public void Build_KeepsUsefulMetricsVisible_WhenCaptureIsPartiallyDegraded()
    {
        // A degraded toast (keyboard missing) is routine — widget stays Monitoring with
        // badge, keeps showing pressure and last focus session from snapshot.
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.44,
            sustainedAttentionSeconds: 600,
            deepWorkActive: false,
            notificationsSuppressed: false,
            brightnessAdjusted: false);
        UserCommunicationToast degraded = new(
            Key: "capture:keyboard_missing",
            Title: "Ari limited",
            Message: "Keyboard capture is retrying.");
        UserAgentVisualSignals signals = new(
            Mode: UserAgentVisualMode.Normal,
            TooltipText: "PORTA | Watching activity",
            ShowDeepWorkStreak: false,
            DeepWorkStreakText: string.Empty,
            ShowRecoveryToast: false,
            RecoveryEventId: 0,
            RecoveryMessage: string.Empty);

        UserDesktopWidgetState state = UserDesktopWidgetStateBuilder.Build(
            status, snapshot, null, signals, degraded);

        Assert.Equal(UserDesktopWidgetMode.Monitoring, state.Mode);
        Assert.Equal("Today", state.Title);
        Assert.True(state.ShowLimitedBadge);
        Assert.Null(state.CriticalState);
        AssertMetric(state.SecondaryMetric, "Last focus session", "10m");
        AssertMetric(state.TertiaryMetric, "Pressure", "44%");
        Assert.DoesNotContain("n/a", state.SecondaryMetric.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n/a", state.TertiaryMetric.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_UsesSharedPresentationMode_InsteadOfIncomingSignalsMode()
    {
        LocalApiStatusResponse status = BuildStatus(
            deepWorkActive: true,
            visualMode: "DEEP_WORK");
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            overloadLikelihood: 0.24,
            sustainedAttentionSeconds: 960,
            deepWorkActive: true,
            notificationsSuppressed: true,
            brightnessAdjusted: false);
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
            liveSession: null,
            signals,
            degradedToast: null);

        Assert.Equal(UserDesktopWidgetMode.DeepWork, state.Mode);
        Assert.Equal("Today", state.Title);
        Assert.Equal("Confidence High | Tracking quality strong", state.FooterText);
    }

    private static void AssertMetric(UserDesktopWidgetMetric actual, string expectedLabel, string expectedValue)
    {
        Assert.Equal(expectedLabel, actual.Label);
        Assert.Equal(expectedValue, actual.Value);
    }

    private static void AssertRoutineWidgetText(UserDesktopWidgetState state)
    {
        Assert.DoesNotContain("unavailable", state.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", state.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", state.FooterText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n/a", state.PrimaryMetric.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n/a", state.SecondaryMetric.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n/a", state.TertiaryMetric.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalApiStatusResponse BuildStatus(
        bool paused = false,
        DateTimeOffset? pauseUntilUtc = null,
        bool overloadActive = false,
        bool deepWorkActive = false,
        string? visualMode = null,
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
            DeepWorkActive: deepWorkActive,
            DeepWorkStreakSeconds: 0,
            VisualMode: visualMode ?? (overloadActive ? "OVERLOAD" : (deepWorkActive ? "DEEP_WORK" : "NORMAL")),
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
            TaskContinuityScore: sustainedAttentionSeconds > 0d ? 0.78d : 0d,
            TaskContinuitySeconds: sustainedAttentionSeconds,
            ContinuityState: sustainedAttentionSeconds > 0d ? "keep" : "reset",
            RuntimeStateFreshness: runtimeStateFreshness,
            RuntimeStateRefreshError: runtimeStateRefreshError,
            AnalyzedStateAvailable: analyzedStateAvailable,
            ModelSource: analyzedStateAvailable ? "analyzed_state" : "brain_fallback",
            FocusEpisodeQuality: sustainedAttentionSeconds > 0d ? 0.78d : 0d,
            FocusEpisodeSeconds: sustainedAttentionSeconds,
            FocusEpisodeState: sustainedAttentionSeconds > 0d ? "keep" : "reset");
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
