using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class OnboardingCopyBuilderTests
{
    [Fact]
    public void BuildStatusText_UsesTrackingQualityLanguage_WhenCaptureIsLimited()
    {
        LocalApiStatusResponse status = BuildStatus();

        string text = OnboardingCopyBuilder.BuildStatusText(
            serviceRunning: true,
            trayRunning: true,
            apiReachable: true,
            status: status,
            statusFresh: true,
            captureHint: "keyboard activity is not being detected right now",
            statusHttpCode: 200,
            dbBusyRetrying: false);

        Assert.Equal(
            "Ari is running, but tracking quality is limited right now: keyboard activity is not being detected right now.",
            text);
    }

    [Fact]
    public void BuildSystemText_UsesSystemCheckVocabulary()
    {
        LocalApiStatusResponse status = BuildStatus();

        string text = OnboardingCopyBuilder.BuildSystemText(
            serviceRunning: true,
            trayRunning: true,
            apiReachable: true,
            status: status,
            statusFresh: true,
            captureHint: null,
            statusHttpCode: 200);

        Assert.Equal(
            "System check: service on, desktop session connected, local service reachable, and tracking active.",
            text);
    }

    [Fact]
    public void BuildGuidanceText_PointsToTrackingAndSystem_WhenCaptureIsLimited()
    {
        LocalApiStatusResponse status = BuildStatus();

        string text = OnboardingCopyBuilder.BuildGuidanceText(
            serviceRunning: true,
            trayRunning: true,
            apiReachable: true,
            status: status,
            statusFresh: true,
            captureHint: "mouse activity is not being detected right now",
            statusHttpCode: 200,
            dbBusyRetrying: false);

        Assert.Equal(
            "What to do: keep using the keyboard or mouse for a few seconds while Ari reconnects. Open Tracking if you want to review tracking quality, then open System if recovery stays stuck.",
            text);
    }

    [Fact]
    public void BuildGuidanceText_PointsToTracking_WhenConsentIsNeeded()
    {
        LocalApiStatusResponse status = BuildStatus(consentActive: false);

        string text = OnboardingCopyBuilder.BuildGuidanceText(
            serviceRunning: true,
            trayRunning: true,
            apiReachable: true,
            status: status,
            statusFresh: true,
            captureHint: null,
            statusHttpCode: 200,
            dbBusyRetrying: false);

        Assert.Equal(
            "What to do: open Tracking and grant consent when you are ready to start tracking.",
            text);
    }

    private static LocalApiStatusResponse BuildStatus(bool consentActive = true)
    {
        return new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            PauseUntilUtc: null,
            ConsentActive: consentActive,
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
            RuntimeStateFreshness: "fresh",
            RuntimeStateRefreshError: null);
    }
}
