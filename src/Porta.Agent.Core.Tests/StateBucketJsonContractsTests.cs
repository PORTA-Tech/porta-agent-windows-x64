using System.Text.Json;
using Porta.Agent.Core;
using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

public sealed class StateBucketJsonContractsTests
{
    [Fact]
    public void BuildOverloadContributorsJson_ContainsTopTwoKeysByWeightedContribution()
    {
        var overload = new OverloadInferenceResult(
            OverloadLikelihood: 0.42d,
            Confidence: 0.66d,
            FComponent: 0.55d,
            IComponent: 0.48d,
            VComponent: 0.37d,
            FTopPrimitives:
            [
                new OverloadTopPrimitive("focus_switch_rate", 0.50d, 0.50d, 0.25d),
                new OverloadTopPrimitive("typing_burst_intensity", 0.90d, 0.15d, 0.135d)
            ],
            ITopPrimitives:
            [
                new OverloadTopPrimitive("typing_burst_intensity", 0.90d, 0.55d, 0.495d),
                new OverloadTopPrimitive("scroll_velocity_index", 0.10d, 0.30d, 0.03d)
            ],
            VTopPrimitives:
            [
                new OverloadTopPrimitive("focus_switch_rate", 0.50d, 0.30d, 0.15d),
                new OverloadTopPrimitive("typing_burst_intensity", 0.90d, 0.15d, 0.135d)
            ],
            Quality: 0.82d,
            Agreement: 0.74d,
            PrimitiveCoverage: 1.0d,
            MissingPrimitiveCount: 0,
            UsedSnapshotFallback: false,
            Source: "bucket_finalizer",
            ComponentsRegistryVersion: 1);

        string json = SqliteBrainStateStore.BuildOverloadContributorsJson(["test"], overload);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_ver").GetInt32());
        Assert.Equal(0.82d, root.GetProperty("quality").GetDouble(), precision: 6);
        Assert.Equal(0.74d, root.GetProperty("agreement").GetDouble(), precision: 6);

        JsonElement fTop = root.GetProperty("components").GetProperty("F").GetProperty("top");
        Assert.Equal(2, fTop.GetArrayLength());
        Assert.Equal("focus_switch_rate", fTop[0].GetProperty("key").GetString());
        Assert.Equal("typing_burst_intensity", fTop[1].GetProperty("key").GetString());
    }

    [Fact]
    public void BuildStateBucketStateFlagsJson_WritesPinnedFlags()
    {
        string json = SqliteBrainStateStore.BuildStateBucketStateFlagsJson(
            coldStart: true,
            partial: true,
            saResetReason: "context_changed",
            taskContinuityScore: 0.72d,
            taskContinuitySeconds: 1810d,
            continuityState: "keep",
            continuityConfidence: 0.81d,
            continuityAnchorAppHash: "app-anchor",
            signalQualityScore: 0.90d,
            modelConfidence: 0.91d,
            overloadInferenceSource: "bucket_finalizer",
            calibrationStage: CalibrationTruth.StageProvisional,
            trustTier: CalibrationTruth.TrustMedium,
            guidanceEnabled: true,
            suggestionsEnabled: true,
            systemActionsEnabled: false,
            overloadActive: true,
            lowQuality: true,
            dwellOnMs: 15000,
            dwellOffMs: 5000,
            gapMs: 10000,
            deepWorkActive: true,
            deepWorkDwellOnMs: 10000,
            deepWorkDwellOffMs: 5000,
            deepWorkGapMs: 5000,
            deepWorkStartedAtUtcMs: 45000,
            deepWorkCandidate: true,
            deepWorkExitCandidate: false,
            deepWorkLowQualityPaused: true,
            notificationSuppressionActive: true,
            notificationSuppressionActionState: "active_with_restore_error",
            notificationSuppressionRestorePending: true,
            notificationSuppressionLastErrorCode: "notification_restore_failed",
            notificationSuppressionEnableCooldownUntilUtcMs: 100000,
            notificationSuppressionMinOnUntilUtcMs: 90000,
            notificationSuppressionBackoffUntilUtcMs: 120000,
            brightnessAdjusted: true,
            brightnessActionState: "overload_entered_pending_ramp",
            brightnessSupported: false,
            brightnessLastErrorCode: "brightness_unsupported",
            brightnessCooldownUntilUtcMs: 130000,
            brightnessBackoffUntilUtcMs: 140000,
            overloadStateDebug: new
            {
                mode = "stable_hold",
                previous_overload_active = false
            },
            deepWorkStateDebug: new
            {
                mode = "active_hold"
            });
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_ver").GetInt32());
        Assert.Equal(1, root.GetProperty("cold_start").GetInt32());
        Assert.Equal(1, root.GetProperty("partial").GetInt32());
        Assert.Equal("context_changed", root.GetProperty("sa_reset_reason").GetString());
        Assert.Equal(0.72d, root.GetProperty("task_continuity_score").GetDouble(), precision: 6);
        Assert.Equal(1810d, root.GetProperty("task_continuity_seconds").GetDouble(), precision: 6);
        Assert.Equal("keep", root.GetProperty("continuity_state").GetString());
        Assert.Equal(0.81d, root.GetProperty("continuity_confidence").GetDouble(), precision: 6);
        Assert.Equal("app-anchor", root.GetProperty("continuity_anchor_app_hash").GetString());
        Assert.Equal(0.72d, root.GetProperty("behavior_continuity_score").GetDouble(), precision: 6);
        Assert.Equal(0.90d, root.GetProperty("signal_quality_score").GetDouble(), precision: 6);
        Assert.Equal(0.91d, root.GetProperty("model_confidence").GetDouble(), precision: 6);
        Assert.Equal(CalibrationTruth.StageProvisional, root.GetProperty("calibration_stage").GetString());
        Assert.Equal(CalibrationTruth.TrustMedium, root.GetProperty("trust_tier").GetString());
        Assert.Equal(1, root.GetProperty("guidance_enabled").GetInt32());
        Assert.Equal(1, root.GetProperty("suggestions_enabled").GetInt32());
        Assert.Equal(1, root.GetProperty("policies_enabled").GetInt32());
        Assert.Equal(0, root.GetProperty("system_actions_enabled").GetInt32());
        Assert.True(root.GetProperty("overload_active").GetBoolean());
        Assert.True(root.GetProperty("low_quality").GetBoolean());
        Assert.Equal(15000, root.GetProperty("dwell_on_ms").GetInt64());
        Assert.Equal(5000, root.GetProperty("dwell_off_ms").GetInt64());
        Assert.Equal(10d, root.GetProperty("gap_s").GetDouble(), precision: 6);
        Assert.Equal("stable_hold", root.GetProperty("overload_state_debug").GetProperty("mode").GetString());
        Assert.True(root.GetProperty("deep_work_active").GetBoolean());
        Assert.Equal(10000, root.GetProperty("deep_work_dwell_on_ms").GetInt64());
        Assert.Equal(5000, root.GetProperty("deep_work_dwell_off_ms").GetInt64());
        Assert.Equal(5d, root.GetProperty("deep_work_gap_s").GetDouble(), precision: 6);
        Assert.Equal(45000, root.GetProperty("deep_work_started_at_utc_ms").GetInt64());
        Assert.Equal(10000, root.GetProperty("dw_on_dwell_ms").GetInt64());
        Assert.Equal(5000, root.GetProperty("dw_off_dwell_ms").GetInt64());
        Assert.Equal(1, root.GetProperty("dw_candidate").GetInt32());
        Assert.Equal(0, root.GetProperty("dw_exit").GetInt32());
        Assert.Equal(1, root.GetProperty("dw_low_quality").GetInt32());
        Assert.Equal("active_hold", root.GetProperty("deep_work_state_debug").GetProperty("mode").GetString());
        Assert.True(root.GetProperty("notifications_suppressed").GetBoolean());
        Assert.Equal("active_with_restore_error", root.GetProperty("notification_suppression_action_state").GetString());
        Assert.Equal(1, root.GetProperty("notification_suppression_restore_pending").GetInt32());
        Assert.Equal("notification_restore_failed", root.GetProperty("notification_suppression_error").GetString());
        Assert.Equal(100000, root.GetProperty("notification_enable_cooldown_until_utc_ms").GetInt64());
        Assert.Equal(90000, root.GetProperty("notification_min_on_until_utc_ms").GetInt64());
        Assert.Equal(120000, root.GetProperty("notification_backoff_until_utc_ms").GetInt64());
        Assert.True(root.GetProperty("brightness_adjusted").GetBoolean());
        Assert.Equal("overload_entered_pending_ramp", root.GetProperty("brightness_action_state").GetString());
        Assert.Equal(0, root.GetProperty("brightness_supported").GetInt32());
        Assert.Equal("brightness_unsupported", root.GetProperty("brightness_error").GetString());
        Assert.Equal(130000, root.GetProperty("brightness_cooldown_until_utc_ms").GetInt64());
        Assert.Equal(140000, root.GetProperty("brightness_backoff_until_utc_ms").GetInt64());
    }

    [Fact]
    public void BuildDeepWorkNotificationIdempotencyKey_UsesTransitionWindows()
    {
        long baseMs = 120_000_000;
        string onKeyA = SqliteBrainStateStore.BuildDeepWorkNotificationIdempotencyKey(
            "deep_work_notifications_suppressed",
            baseMs + 5_000);
        string onKeyB = SqliteBrainStateStore.BuildDeepWorkNotificationIdempotencyKey(
            "deep_work_notifications_suppressed",
            baseMs + 55_000);
        string offKey = SqliteBrainStateStore.BuildDeepWorkNotificationIdempotencyKey(
            "deep_work_notifications_restored",
            baseMs + 30_000);

        Assert.Equal(onKeyA, onKeyB);
        Assert.StartsWith("dw_notify_on:v1:", onKeyA);
        Assert.StartsWith("dw_notify_off:v1:", offKey);
        Assert.NotEqual(onKeyA, offKey);
    }

    [Fact]
    public void BuildOverloadBrightnessIdempotencyKey_UsesSessionWindows()
    {
        long baseMs = 120_000_000;
        string onKeyA = SqliteBrainStateStore.BuildOverloadBrightnessIdempotencyKey(
            "overload_brightness_reduced",
            baseMs + 5_000);
        string onKeyB = SqliteBrainStateStore.BuildOverloadBrightnessIdempotencyKey(
            "overload_brightness_reduced",
            baseMs + 55_000);
        string offKey = SqliteBrainStateStore.BuildOverloadBrightnessIdempotencyKey(
            "overload_brightness_restored",
            baseMs + 30_000);

        Assert.Equal(onKeyA, onKeyB);
        Assert.StartsWith("brightness_on:v1:", onKeyA);
        Assert.StartsWith("brightness_off:v1:", offKey);
        Assert.NotEqual(onKeyA, offKey);
    }
}
