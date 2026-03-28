using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserCommunicationRouterTests
{
    [Fact]
    public void ResolveRecentEventToast_RoutesDeepWorkRestoreEvent()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-27T10:00:00Z");
        LocalApiEventResponse restored = BuildEvent(
            id: 42,
            action: "deep_work_notifications_restored",
            tsUtc: nowUtc.AddSeconds(-15));

        UserCommunicationToast? toast = UserCommunicationRouter.ResolveRecentEventToast(
            [restored],
            nowUtc,
            lastEventId: 0);

        Assert.NotNull(toast);
        Assert.Equal(42, toast.EventId);
        Assert.Equal("Ari", toast.Title);
        Assert.Contains("restored Windows notification banners", toast.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRecentEventToast_IgnoresAlreadyPresentedEvent()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-27T10:00:00Z");
        LocalApiEventResponse restored = BuildEvent(
            id: 42,
            action: "deep_work_notifications_restored",
            tsUtc: nowUtc.AddSeconds(-15));

        UserCommunicationToast? toast = UserCommunicationRouter.ResolveRecentEventToast(
            [restored],
            nowUtc,
            lastEventId: 42);

        Assert.Null(toast);
    }

    [Fact]
    public void ResolveCaptureDegradedState_UsesHeartbeatReason()
    {
        LocalApiStatusResponse status = BuildStatus(captureFailureReason: "USER_AGENT_HEARTBEAT_STALE");

        UserCommunicationToast? toast = UserCommunicationRouter.ResolveCaptureDegradedState(
            status,
            snapshot: BuildSnapshot(partialData: false, missingMajorChannels: []));

        Assert.NotNull(toast);
        Assert.Equal("capture:heartbeat_stale", toast.Key);
        Assert.Equal("capture:heartbeat", toast.DeduplicationKey);
        Assert.Equal("Ari limited", toast.Title);
        Assert.Contains("heartbeat stopped updating", toast.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveCaptureDegradedState_DoesNotToastForSingleChannelInactivityAlone()
    {
        LocalApiStatusResponse inactiveStatus = BuildStatus(
            captureFailureReason: null,
            keyboardSignalActive: false,
            mouseSignalActive: true);
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(partialData: false, missingMajorChannels: []);

        UserCommunicationToast? inactiveToast = UserCommunicationRouter.ResolveCaptureDegradedState(inactiveStatus, snapshot);

        Assert.Null(inactiveToast);
    }

    [Fact]
    public void ResolveCaptureDegradedState_UsesPartialCaptureWhenChannelsMissing()
    {
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(
            partialData: true,
            missingMajorChannels: ["keyboard", "mouse"]);

        UserCommunicationToast? toast = UserCommunicationRouter.ResolveCaptureDegradedState(status, snapshot);

        Assert.NotNull(toast);
        Assert.Equal("capture:partial:keyboard-mouse", toast.Key);
        Assert.Contains("Missing keyboard/mouse", toast.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCaptureDegradedState_ReturnsNullWhenHealthy()
    {
        LocalApiStatusResponse status = BuildStatus();
        LocalApiCurrentSnapshotResponse snapshot = BuildSnapshot(partialData: false, missingMajorChannels: []);

        UserCommunicationToast? toast = UserCommunicationRouter.ResolveCaptureDegradedState(status, snapshot);

        Assert.Null(toast);
    }

    private static LocalApiStatusResponse BuildStatus(
        string? captureFailureReason = null,
        string? ownershipWarning = null,
        bool keyboardSignalActive = true,
        bool mouseSignalActive = true,
        string captureContext = "UserSession")
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: true,
            ConsentVersion: "v1",
            ConsentTimestampUtc: DateTimeOffset.Parse("2026-03-27T09:00:00Z"),
            ConsentDisclosureHash: "hash",
            BackendConnected: true,
            FocusStrength: 0.75,
            OverloadLikelihood: 0.24,
            LastAction: string.Empty,
            AgentVersion: "test",
            SchemaVersion: "1",
            PolicyVersion: "p1",
            LastEventTimestampUtc: null,
            BackendInstanceId: "backend-1",
            BackendProcessId: 42,
            CaptureContext: captureContext,
            UserAgentHeartbeatUpdatedAtUtc: DateTimeOffset.Parse("2026-03-27T09:59:58Z"),
            UserAgentInstanceId: "ua-1",
            UserAgentProcessId: 321,
            UserAgentSessionId: "session-1",
            KeyboardSignalActive: keyboardSignalActive,
            MouseSignalActive: mouseSignalActive,
            LastInputEventUtc: DateTimeOffset.Parse("2026-03-27T09:59:55Z"),
            LastInputAgentInstanceId: "ua-1",
            LastInputSessionId: "session-1",
            OwnershipWarning: ownershipWarning,
            CaptureFailureReason: captureFailureReason,
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
            LastUpdatedUtc: DateTimeOffset.Parse("2026-03-27T10:00:00Z"),
            TrustTier: CalibrationTruth.TrustHigh);
    }

    private static LocalApiCurrentSnapshotResponse BuildSnapshot(
        bool partialData,
        IReadOnlyList<string> missingMajorChannels)
    {
        return new LocalApiCurrentSnapshotResponse(
            TimestampUtc: DateTimeOffset.Parse("2026-03-27T10:00:00Z"),
            TypingBurstIntensity: 0.2,
            ScrollVelocityIndex: 0.1,
            FocusSwitchRate: 0.1,
            SmoothedTypingBurstIntensity: 0.2,
            SmoothedScrollVelocityIndex: 0.1,
            SmoothedFocusSwitchRate: 0.1,
            FocusStrength: 0.78,
            OverloadLikelihood: 0.2,
            FatigueProbability: 0.12,
            CognitiveStability: 0.77,
            DominantStateValue: 0.78,
            DominantStateName: "calm",
            Confidence: 0.83,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus"],
            SustainedAttentionDurationSec: 0,
            IdleFragmentationIndex: 0.1,
            FocusChoppinessIndex: 0.08,
            OverloadActive: false,
            StateFlagsJson: null,
            ContributorsJson: null,
            BaselineReady: true,
            BaselineReadyPct: 1.0,
            AvgQuality2m: 0.9,
            ColdStart: false,
            CurrentQualityScore: 0.9,
            PartialData: partialData,
            MissingMajorChannels: missingMajorChannels,
            CooldownsRemaining: null,
            DeepWorkActive: false,
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            FComponent: 0.2,
            IComponent: 0.45,
            VComponent: 0.18);
    }

    private static LocalApiEventResponse BuildEvent(
        long id,
        string action,
        DateTimeOffset tsUtc)
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
            TriggerState: "focus",
            TriggerValue: 0.0,
            Confidence: 0.8,
            DurationSeconds: null,
            EscalationLevel: "Inform",
            Reversible: false,
            Reason: string.Empty,
            Contributors: [],
            InterventionId: string.Empty,
            PolicyId: string.Empty,
            Domain: "focus",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: string.Empty,
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
