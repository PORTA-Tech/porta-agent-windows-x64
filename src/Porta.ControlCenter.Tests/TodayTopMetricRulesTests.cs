using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class TodayTopMetricRulesTests
{
    [Fact]
    public void ResolveTodayLastFocusMetricText_PreservesEpisodeDurationEvenWhenLiveSessionIsFresh()
    {
        LocalApiStatusResponse status = BuildStatus();

        string text = MainWindow.ResolveTodayLastFocusMetricText(
            TodayTrackingState.StillTracking,
            focusEpisodeSeconds: 180d,
            liveSessionFresh: true,
            status);

        Assert.Equal("Last focus session: 3m", text);
    }

    [Fact]
    public void ResolveTodayLastFocusMetricText_UsesZeroWhenLiveSessionIsFreshButEpisodeIsMissing()
    {
        LocalApiStatusResponse status = BuildStatus();

        string text = MainWindow.ResolveTodayLastFocusMetricText(
            TodayTrackingState.StillTracking,
            focusEpisodeSeconds: null,
            liveSessionFresh: true,
            status);

        Assert.Equal("Last focus session: 0s", text);
    }

    private static LocalApiStatusResponse BuildStatus()
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
