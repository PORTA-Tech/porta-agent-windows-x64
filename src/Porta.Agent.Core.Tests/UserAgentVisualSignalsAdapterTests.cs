using Porta.Agent.Common.Contracts;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Core.Tests;

public sealed class UserAgentVisualSignalsAdapterTests
{
    [Fact]
    public void Build_UsesDeepWorkTrayModeAndStreak_WhenDeepWorkIsActive()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-08T10:00:00Z");
        long startedAtUtcMs = nowUtc.AddMinutes(-17).ToUnixTimeMilliseconds();
        LocalApiStatusResponse status = BuildStatus(
            consentActive: true,
            captureContext: "UserSession",
            deepWorkActive: true,
            overloadActive: false,
            deepWorkStreakSeconds: 0,
            visualMode: "DEEP_WORK");
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            sustainedAttentionSeconds: 780,
            deepWorkActive: true,
            overloadActive: false,
            stateFlagsJson: $$"""{"deep_work_started_at_utc_ms":{{startedAtUtcMs}},"deep_work_active":true}""");

        UserAgentVisualSignals signals = UserAgentVisualSignalsAdapter.Build(
            status,
            snapshot,
            [],
            nowUtc,
            lastRecoveryEventId: 0);

        Assert.Equal(UserAgentVisualMode.DeepWork, signals.Mode);
        Assert.True(signals.ShowDeepWorkStreak);
        Assert.Contains("Deep Work streak:", signals.DeepWorkStreakText, StringComparison.Ordinal);
        Assert.Contains("Deep Work streak:", signals.TooltipText, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ShowsRecoveryToast_OnlyForNewRecentRecoveryEvent()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-08T10:00:00Z");
        LocalApiStatusResponse status = BuildStatus(
            consentActive: true,
            captureContext: "UserSession",
            deepWorkActive: false,
            overloadActive: false,
            deepWorkStreakSeconds: 0,
            visualMode: "NORMAL");
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            sustainedAttentionSeconds: 0,
            deepWorkActive: false,
            overloadActive: false,
            stateFlagsJson: null);
        LocalApiEventResponse recovery = BuildEvent(
            id: 41,
            action: "overload_recovered",
            type: "OVERLOAD_RECOVERED",
            tsUtc: nowUtc.AddSeconds(-20),
            whatChanged: "Recovery complete.");

        UserAgentVisualSignals first = UserAgentVisualSignalsAdapter.Build(
            status,
            snapshot,
            [recovery],
            nowUtc,
            lastRecoveryEventId: 0);
        UserAgentVisualSignals second = UserAgentVisualSignalsAdapter.Build(
            status,
            snapshot,
            [recovery],
            nowUtc,
            lastRecoveryEventId: 41);

        Assert.True(first.ShowRecoveryToast);
        Assert.Equal(41, first.RecoveryEventId);
        Assert.Equal("Recovery complete.", first.RecoveryMessage);
        Assert.False(second.ShowRecoveryToast);
    }

    [Fact]
    public void Build_MarksTrayUnavailable_WhenServiceDataIsMissing()
    {
        UserAgentVisualSignals signals = UserAgentVisualSignalsAdapter.Build(
            status: null,
            snapshot: null,
            recentEvents: [],
            nowUtc: DateTimeOffset.Parse("2026-03-08T10:00:00Z"),
            lastRecoveryEventId: 0);

        Assert.Equal(UserAgentVisualMode.Unavailable, signals.Mode);
        Assert.Equal("PORTA | Waiting for service data", signals.TooltipText);
        Assert.False(signals.ShowRecoveryToast);
    }

    private static LocalApiStatusResponse BuildStatus(
        bool consentActive,
        string captureContext,
        bool deepWorkActive,
        bool overloadActive,
        int deepWorkStreakSeconds,
        string visualMode)
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: consentActive,
            ConsentVersion: "v1",
            ConsentTimestampUtc: DateTimeOffset.Parse("2026-03-08T09:00:00Z"),
            ConsentDisclosureHash: "hash",
            BackendConnected: true,
            FocusStrength: 0.75,
            OverloadLikelihood: overloadActive ? 0.82 : 0.24,
            LastAction: string.Empty,
            AgentVersion: "test",
            SchemaVersion: "1",
            PolicyVersion: "p1",
            LastEventTimestampUtc: null,
            BackendInstanceId: "backend-1",
            BackendProcessId: 42,
            CaptureContext: captureContext,
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 321,
            UserAgentSessionId: "session-1",
            KeyboardSignalActive: true,
            MouseSignalActive: true,
            LastInputEventUtc: DateTimeOffset.Parse("2026-03-08T09:59:55Z"),
            LastInputAgentInstanceId: "ua-1",
            LastInputSessionId: "session-1",
            OwnershipWarning: null,
            CaptureFailureReason: null,
            LastError: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            NotificationSuppressionActive: deepWorkActive,
            NotificationSuppressionReady: true,
            NotificationSuppressionLastError: null,
            NotificationSuppressionError: null,
            NotificationSuppressionBackoffUntilUtc: null,
            NotificationBackoffUntilUtc: null,
            NotificationsSuppressed: deepWorkActive,
            BrightnessActionReady: false,
            BrightnessActionActive: false,
            BrightnessAdjusted: false,
            BrightnessSupported: false,
            BrightnessCurrentLevel: 0,
            BrightnessTargetLevel: 0,
            BrightnessLastError: null,
            BrightnessError: null,
            BrightnessBackoffUntilUtc: null,
            DbMigrationFailed: false,
            BaselineReady: true,
            ColdStart: false,
            OverloadActive: overloadActive,
            DeepWorkActive: deepWorkActive,
            DeepWorkStreakSeconds: deepWorkStreakSeconds,
            VisualMode: visualMode,
            LatestContributorsJson: null,
            LatestWhyJson: null,
            LastUpdatedUtc: DateTimeOffset.Parse("2026-03-08T10:00:00Z"));
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        double sustainedAttentionSeconds,
        bool deepWorkActive,
        bool overloadActive,
        string? stateFlagsJson)
    {
        return new LocalApiCurrentSnapshotResponse(
            TimestampUtc: DateTimeOffset.Parse("2026-03-08T10:00:00Z"),
            TypingBurstIntensity: 0.2,
            ScrollVelocityIndex: 0.1,
            FocusSwitchRate: 0.1,
            SmoothedTypingBurstIntensity: 0.2,
            SmoothedScrollVelocityIndex: 0.1,
            SmoothedFocusSwitchRate: 0.1,
            FocusStrength: 0.78,
            OverloadLikelihood: overloadActive ? 0.81 : 0.2,
            FatigueProbability: 0.12,
            CognitiveStability: 0.77,
            DominantStateValue: 0.78,
            DominantStateName: deepWorkActive ? "focus" : "calm",
            Confidence: 0.83,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus"],
            SustainedAttentionDurationSec: sustainedAttentionSeconds,
            IdleFragmentationIndex: 0.1,
            FocusChoppinessIndex: 0.08,
            OverloadActive: overloadActive,
            StateFlagsJson: stateFlagsJson,
            ContributorsJson: null,
            BaselineReady: true,
            BaselineReadyPct: 1.0,
            AvgQuality2m: 0.9,
            ColdStart: false,
            CurrentQualityScore: 0.9,
            PartialData: false,
            MissingMajorChannels: [],
            CooldownsRemaining: null,
            DeepWorkActive: deepWorkActive,
            NotificationsSuppressed: deepWorkActive,
            BrightnessAdjusted: false,
            FComponent: 0.2,
            IComponent: 0.45,
            VComponent: 0.18);
    }

    private static LocalApiEventResponse BuildEvent(
        long id,
        string action,
        string type,
        DateTimeOffset tsUtc,
        string whatChanged)
    {
        return new LocalApiEventResponse(
            Id: id,
            Type: type,
            TsUtc: tsUtc,
            IdempotencyKey: $"event-{id}",
            WhyJson: "{}",
            StateFlagsJson: null,
            TimestampUtc: tsUtc,
            Action: action,
            TriggerState: "overload",
            TriggerValue: 0.0,
            Confidence: 0.8,
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
            WhatChanged: whatChanged,
            HowToUndo: string.Empty,
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "completed",
            CommandType: string.Empty,
            ExecutionState: "decision_only",
            DecidedAtUtc: tsUtc,
            AppliedAtUtc: null,
            AckedAtUtc: null,
            ExecutionErrorCode: string.Empty,
            ExecutorAgentInstanceId: string.Empty);
    }
}
