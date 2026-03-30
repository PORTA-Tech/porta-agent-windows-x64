using System.Text.Json;
using Porta.Agent.Core;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiSnapshotAcceptanceTests
{
    [Fact]
    public void Snapshot_ReturnsLatestOverloadConfidenceAndSustainedAttention()
    {
        AgentBrainSnapshot snapshot = CreateSnapshot(
            overload: 0.11d,
            confidence: 0.22d,
            sustainedAttentionSec: 15d,
            stateFlagsJson: "{\"schema_ver\":1}",
            contributorsJson: "{\"schema_ver\":1}");

        AgentLatestStateDetails latest = new(
            TimestampUtc: DateTimeOffset.UtcNow,
            OverloadLikelihood: 0.84d,
            Confidence: 0.91d,
            SustainedAttentionDurationSec: 1810d,
            IdleFragmentationIndex: 0.33d,
            FocusAppEntropy: 0.44d,
            OverloadActive: true,
            StateFlagsJson: "{\"schema_ver\":1,\"overload_active\":true,\"deep_work_active\":true,\"task_continuity_score\":0.72,\"task_continuity_seconds\":1810,\"continuity_state\":\"keep\",\"continuity_confidence\":0.81,\"signal_quality_score\":0.90,\"model_confidence\":0.91}",
            ContributorsJson: "{\"schema_ver\":1,\"components\":{\"F\":{\"value\":0.2},\"I\":{\"value\":0.6},\"V\":{\"value\":0.3}}}",
            BaselineReady: true,
            BaselineReadyPct: 1d,
            AvgQuality2m: 0.88d,
            ColdStart: false,
            CurrentQualityScore: 0.9d,
            PartialData: false,
            MissingMajorChannels: [],
            BreakPendingAction: "stretch_break_suggestion",
            BreakPendingReasonCode: "rolling_focus_microbreak",
            BreakPendingEffectiveFocusSeconds: 1680d,
            BreakPendingEffectiveFocusWindowSeconds: 2100,
            BreakDeliveryBoundaryReached: false,
            BreakDeliveryBoundaryKind: "typing_quiet",
            BreakMicrobreakEffectiveFocusSeconds: 1680d,
            BreakShortBreakEffectiveFocusSeconds: 2280d);

        var dto = SnapshotMapper.Map(snapshot, latest);

        Assert.InRange(dto.overload_likelihood - 0.84d, -0.000001d, 0.000001d);
        Assert.InRange(dto.Confidence - 0.91d, -0.000001d, 0.000001d);
        Assert.InRange(dto.sustained_attention_duration_sec - 1810d, -0.000001d, 0.000001d);
        Assert.False(string.IsNullOrWhiteSpace(dto.snapshot_captured_at_utc));
        Assert.Equal(dto.timestamp_utc, dto.snapshot_captured_at_utc);
        Assert.Equal(dto.runtime_state_freshness == "fresh" ? "fresh" : "stale", dto.freshness_state);
        Assert.Equal(dto.runtime_state_freshness == "fresh" ? null : "snapshot_stale", dto.degraded_reason);
        Assert.Equal(dto.snapshot_captured_at_utc, dto.freshness.captured_at_utc);
        Assert.Equal(dto.freshness_state, dto.freshness.freshness_state);
        Assert.Equal(dto.degraded_reason, dto.freshness.degraded_reason);
        Assert.True(dto.overload_active);
        Assert.True(dto.deep_work_active);
        Assert.InRange((dto.F ?? 0d) - 0.2d, -0.000001d, 0.000001d);
        Assert.InRange((dto.I ?? 0d) - 0.6d, -0.000001d, 0.000001d);
        Assert.InRange((dto.V ?? 0d) - 0.3d, -0.000001d, 0.000001d);
        Assert.InRange(dto.behavior_continuity_score - 0.72d, -0.000001d, 0.000001d);
        Assert.InRange((dto.signal_quality_score ?? 0d) - 0.90d, -0.000001d, 0.000001d);
        Assert.InRange(dto.model_confidence - 0.91d, -0.000001d, 0.000001d);
        Assert.Equal("stretch_break_suggestion", dto.break_pending_action);
        Assert.Equal("rolling_focus_microbreak", dto.break_pending_reason_code);
        Assert.InRange((dto.break_pending_effective_focus_s ?? 0d) - 1680d, -0.000001d, 0.000001d);
        Assert.Equal(2100, dto.break_pending_effective_focus_window_s);
        Assert.False(dto.break_delivery_boundary_reached ?? true);
        Assert.Equal("typing_quiet", dto.break_delivery_boundary_kind);
        Assert.InRange((dto.break_microbreak_effective_focus_s ?? 0d) - 1680d, -0.000001d, 0.000001d);
        Assert.InRange((dto.break_short_break_effective_focus_s ?? 0d) - 2280d, -0.000001d, 0.000001d);
    }

    [Fact]
    public void Snapshot_AlwaysIncludesSchemaVerInContributorsAndStateFlags()
    {
        AgentBrainSnapshot snapshot = CreateSnapshot(
            overload: 0.1d,
            confidence: 0.2d,
            sustainedAttentionSec: 0d,
            stateFlagsJson: "{}",
            contributorsJson: "{}");

        var dto = SnapshotMapper.Map(snapshot, latestState: null);

        using JsonDocument stateFlags = JsonDocument.Parse(dto.state_flags_json!);
        using JsonDocument contributors = JsonDocument.Parse(dto.contributors_json!);
        Assert.Equal(1, stateFlags.RootElement.GetProperty("schema_ver").GetInt32());
        Assert.Equal(1, contributors.RootElement.GetProperty("schema_ver").GetInt32());
    }

    [Fact]
    public void Snapshot_ReturnsCooldownsRemainingInSeconds()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string stateFlags =
            $"{{\"schema_ver\":1,\"break_cooldown_until_utc_ms\":{nowMs + 45000},\"notification_enable_cooldown_until_utc_ms\":{nowMs + 80000},\"brightness_cooldown_until_utc_ms\":{nowMs + 30000},\"notification_min_on_until_utc_ms\":{nowMs + 20000}}}";

        AgentBrainSnapshot snapshot = CreateSnapshot(
            overload: 0.2d,
            confidence: 0.4d,
            sustainedAttentionSec: 25d,
            stateFlagsJson: stateFlags,
            contributorsJson: "{\"schema_ver\":1}");

        var dto = SnapshotMapper.Map(snapshot, latestState: null);

        Assert.NotNull(dto.cooldowns_remaining);
        Assert.InRange(dto.cooldowns_remaining!.break_suggestion_sec_remaining, 1, 45);
        Assert.InRange(dto.cooldowns_remaining.deep_work_notification_enable_sec_remaining, 1, 80);
        Assert.InRange(dto.cooldowns_remaining.brightness_action_sec_remaining, 1, 30);
        Assert.True(dto.cooldowns_remaining.AnyActive);
    }

    [Fact]
    public void Snapshot_ColdStartNoFinalizedData_ReturnsSafePayload()
    {
        AgentBrainSnapshot snapshot = CreateSnapshot(
            overload: 0d,
            confidence: 0d,
            sustainedAttentionSec: 0d,
            stateFlagsJson: "",
            contributorsJson: "");

        var dto = SnapshotMapper.Map(snapshot, latestState: null);

        Assert.NotNull(dto);
        Assert.False(dto.BaselineReady ?? true);
        Assert.True(dto.ColdStart ?? false);
        Assert.NotNull(dto.state_flags_json);
        Assert.NotNull(dto.contributors_json);
    }

    private static AgentBrainSnapshot CreateSnapshot(
        double overload,
        double confidence,
        double sustainedAttentionSec,
        string stateFlagsJson,
        string contributorsJson)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: 0.05d,
            ScrollVelocityIndex: 0.08d,
            FocusSwitchRate: 0.03d,
            SmoothedTypingBurstIntensity: 0.04d,
            SmoothedScrollVelocityIndex: 0.06d,
            SmoothedFocusSwitchRate: 0.02d,
            FocusStrength: 0.51d,
            OverloadLikelihood: overload,
            FatigueProbability: 0.1d,
            CognitiveStability: 0.8d,
            DominantStateValue: 0.51d,
            DominantStateName: "focus",
            Confidence: confidence,
            LastAction: "maintain_focus",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: [],
            SustainedAttentionDurationSec: sustainedAttentionSec,
            IdleFragmentationIndex: 0.01d,
            OverloadActive: false,
            StateFlagsJson: stateFlagsJson,
            ContributorsJson: contributorsJson);
    }
}
