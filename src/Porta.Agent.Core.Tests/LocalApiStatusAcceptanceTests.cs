using Porta.Agent.Core;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiStatusAcceptanceTests
{
    [Fact]
    public void Status_ReturnsOperationalFlags_AndAuthFailureFields()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.22d, confidence: 0.31d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.77d,
                Confidence: 0.83d,
                SustainedAttentionDurationSec: 1450d,
                IdleFragmentationIndex: 0.22d,
                FocusAppEntropy: 0.15d,
                OverloadActive: true,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":true,\"deep_work_active\":true}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: true,
                BaselineReadyPct: 1.0d,
                AvgQuality2m: 0.88d,
                ColdStart: false,
                CurrentQualityScore: 0.91d,
                PartialData: false,
                MissingMajorChannels: []),
            NotificationStatus = new AgentNotificationSuppressionStatus(
                Active: true,
                Ready: true,
                ActionState: "active",
                LastError: null,
                BackoffUntilUtc: null,
                EnableCooldownUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                MinOnUntilUtc: DateTimeOffset.UtcNow.AddMinutes(2),
                RestorePending: false),
            BrightnessStatus = new AgentBrightnessActionStatus(
                Supported: true,
                ActionReady: true,
                Active: true,
                RampDirection: "down",
                PreviousLevel: 60,
                TargetLevel: 55,
                CurrentLevel: 58,
                LastError: null,
                BackoffUntilUtc: null,
                CooldownUntilUtc: DateTimeOffset.UtcNow.AddMinutes(10)),
            RecentEvents = [
                new AgentControlEvent(
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Action: "deep_work_notifications_suppressed",
                    TriggerState: "deep_work",
                    TriggerValue: 1,
                    Confidence: 0.8d,
                    DurationSeconds: null,
                    EscalationLevel: "Nudge",
                    Reversible: true,
                    Reason: "test",
                    Contributors: [],
                    WhyJson: "{\"schema_ver\":1,\"reason\":\"Deep work detected\"}")
            ]
        };

        var controller = new AgentCoreController(store);
        controller.SetLocalApiAuthState(false, "TOKEN_READ_FAILED");

        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: false, localApiAuthError: "TOKEN_READ_FAILED");

        Assert.True(dto.overload_active);
        Assert.True(dto.deep_work_active);
        Assert.True(dto.notifications_suppressed);
        Assert.True(dto.brightness_adjusted);
        Assert.True(dto.brightness_supported);
        Assert.False(dto.LocalApiAuthReady);
        Assert.Equal("TOKEN_READ_FAILED", dto.LocalApiAuthError);
        Assert.False(dto.auth.ready);
        Assert.Equal("TOKEN_READ_FAILED", dto.auth.error);
        Assert.False(dto.db_migration_failed);
    }

    [Fact]
    public void Status_ReturnsDbMigrationFailedFlag_FromDiagnosticsSource()
    {
        var store = new LocalApiContractFakeStore
        {
            LatestSnapshot = CreateSnapshot(overload: 0.11d, confidence: 0.51d),
            LatestStateDetails = new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.UtcNow,
                OverloadLikelihood: 0.11d,
                Confidence: 0.51d,
                SustainedAttentionDurationSec: 12d,
                IdleFragmentationIndex: 0.01d,
                FocusAppEntropy: 0d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":false,\"deep_work_active\":false}",
                ContributorsJson: "{\"schema_ver\":1}",
                BaselineReady: false,
                BaselineReadyPct: 0.02d,
                AvgQuality2m: 0.2d,
                ColdStart: true,
                CurrentQualityScore: 0.3d,
                PartialData: true,
                MissingMajorChannels: ["keyboard"]),
            DbMigrationFailed = true
        };

        var controller = new AgentCoreController(store);
        AgentServiceStatusSnapshot status = controller.GetStatus();
        var dto = StatusMapper.Map(status, localApiAuthReady: true, localApiAuthError: null);

        Assert.True(dto.db_migration_failed);
        Assert.True(dto.degraded.degraded);
        Assert.True(dto.degraded.db_migration_failed);
    }

    private static AgentBrainSnapshot CreateSnapshot(double overload, double confidence)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: 0.1d,
            ScrollVelocityIndex: 0.1d,
            FocusSwitchRate: 0.1d,
            SmoothedTypingBurstIntensity: 0.1d,
            SmoothedScrollVelocityIndex: 0.1d,
            SmoothedFocusSwitchRate: 0.1d,
            FocusStrength: 0.5d,
            OverloadLikelihood: overload,
            FatigueProbability: 0.2d,
            CognitiveStability: 0.8d,
            DominantStateValue: 0.5d,
            DominantStateName: "focus",
            Confidence: confidence,
            LastAction: "maintain_focus",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: []);
    }
}
