using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Db;
using Porta.Agent.Core;
using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Tests;

// Workstream 1 — the personal break gate, rising-trend suppression, and acceptance
// backoff. The invariant: "elevated" is measured against the user's OWN distribution,
// dismissals stretch the next ask, and rest (taken or slept) clears the debt.
public sealed class BreakPersonalizationTests
{
    // ── Personal p80 gate in the policy ──

    [Fact]
    public void PressureAboveOldConstantButBelowPersonalP80_DoesNotNag()
    {
        // The Aymene case: post-1a normal work sits at ~0.55 all day. With a personal
        // p80 of 0.65 that is NOT elevated — no nudge, even though the old fixed 0.50
        // gate would have fired every cooldown expiry.
        BreakSuggestionDecision decision = TriggerShortBreak(
            pressureLevel: 0.55d,
            personalization: new BreakPersonalizationSnapshot(0.65d, PressureDeclining: false, "personal_p80"));

        Assert.False(decision.ShouldEmit);
        Assert.Equal("state_not_warranting_break", decision.ReasonCode);
        Assert.Equal("personal_p80", decision.NeedGateMode);
    }

    [Fact]
    public void PressureAbovePersonalP80_EmitsWithPersonalReason()
    {
        BreakSuggestionDecision decision = TriggerShortBreak(
            pressureLevel: 0.72d,
            personalization: new BreakPersonalizationSnapshot(0.65d, PressureDeclining: false, "personal_p80"));

        Assert.True(decision.ShouldEmit);
        Assert.Equal("short_break", decision.Action);
        Assert.Equal("personal_p80", decision.NeedGateMode);
        Assert.Equal("pressure_above_personal", decision.NeedGateReason);
    }

    [Fact]
    public void ColdStart_UsesAbsoluteConstantAndRecordsMode()
    {
        // Under 3 days of data the old behavior applies exactly — and the decision says so.
        BreakSuggestionDecision decision = TriggerShortBreak(
            pressureLevel: 0.55d,
            personalization: BreakPersonalizationSnapshot.ColdStart);

        Assert.True(decision.ShouldEmit);
        Assert.Equal("cold_start_absolute", decision.NeedGateMode);
        Assert.Equal("pressure_above_personal", decision.NeedGateReason);
    }

    [Fact]
    public void DecliningPressureAboveP80_ShortBreakSuppressed()
    {
        // 1.2: the user is settling in — pressure falling means leave them alone,
        // even when the level is above their p80.
        BreakSuggestionDecision decision = TriggerShortBreak(
            pressureLevel: 0.72d,
            personalization: new BreakPersonalizationSnapshot(0.65d, PressureDeclining: true, "personal_p80"));

        Assert.False(decision.ShouldEmit);
        Assert.Equal("pressure_declining", decision.ReasonCode);
    }

    [Fact]
    public void DecliningPressure_LongBreakUnaffected()
    {
        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            BaselineGatingResult.Warm,
            BuildStabilization(overloadActive: false),
            BuildOverloadInference(confidence: 0.80d, overloadLikelihood: 0.20d, pressureLevel: 0.30d),
            BuildSession(saSeconds: 4000d),
            BuildFocusWindow(
                shortBreakSeconds: 2400d,
                longBreakSeconds: PortaExecutionConstants.BreakPolicy.LongBreakEffectiveFocusSecondsLowPressure,
                boundaryKind: "short_idle"),
            resolveCooldown: () => new BreakPolicyCooldownSnapshot(true, 0, 0),
            personalization: new BreakPersonalizationSnapshot(0.65d, PressureDeclining: true, "personal_p80"));

        Assert.True(decision.ShouldEmit);
        Assert.Equal("long_break", decision.Action);
        Assert.Equal("long_focus", decision.NeedGateReason);
    }

    [Fact]
    public void OverloadActive_EmitsWithOverloadReason()
    {
        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            BaselineGatingResult.Warm,
            BuildStabilization(overloadActive: true),
            BuildOverloadInference(confidence: 0.80d, overloadLikelihood: 0.30d, pressureLevel: 0.30d),
            BuildSession(saSeconds: 1600d),
            BuildFocusWindow(
                shortBreakSeconds: PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowPressure,
                longBreakSeconds: 2400d,
                boundaryKind: "short_idle"),
            resolveCooldown: () => new BreakPolicyCooldownSnapshot(true, 0, 0),
            personalization: new BreakPersonalizationSnapshot(0.65d, PressureDeclining: false, "personal_p80"));

        Assert.True(decision.ShouldEmit);
        Assert.Equal("overload_active", decision.NeedGateReason);
    }

    // ── Baseline math ──

    [Fact]
    public void PersonalPressureBaseline_ComputesNearestRankP80()
    {
        // 10 values 0.1..1.0 → nearest-rank p80 = 8th value = 0.8.
        var readings = new List<double> { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };
        PersonalPressureBaselineResult result = PersonalPressureBaseline.Compute(readings, daysOfData: 7);
        Assert.Equal(0.8d, result.P80, precision: 6);
        Assert.Equal(10, result.SampleCount);
    }

    [Fact]
    public void PersonalPressureBaseline_NotReadyUnderThreeDaysOrThinSamples()
    {
        var plenty = new List<double>();
        for (int i = 0; i < PortaExecutionConstants.BreakPolicy.PersonalBaselineMinSamples; i++)
        {
            plenty.Add(0.5d);
        }

        Assert.False(PersonalPressureBaseline.Compute(plenty, daysOfData: 2).Ready);
        Assert.True(PersonalPressureBaseline.Compute(plenty, daysOfData: 3).Ready);
        Assert.False(PersonalPressureBaseline.Compute(plenty.Take(50).ToList(), daysOfData: 7).Ready);
    }

    [Fact]
    public void PersonalPressureBaseline_IgnoresNonFiniteReadings()
    {
        var readings = new List<double> { 0.5, double.NaN, double.PositiveInfinity, 0.7 };
        PersonalPressureBaselineResult result = PersonalPressureBaseline.Compute(readings, daysOfData: 7);
        Assert.Equal(2, result.SampleCount);
    }

    // ── Backoff math ──

    [Theory]
    [InlineData(0, 1800)]
    [InlineData(1, 3600)]
    [InlineData(2, 7200)]
    [InlineData(3, 14400)]
    [InlineData(4, 14400)] // cap 4h
    [InlineData(10, 14400)]
    public void BackoffCooldown_DoublesToCap(int streak, int expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            BreakBackoffPolicy.EffectiveCooldownSeconds(streak, PortaExecutionConstants.BreakPolicy.CooldownSeconds));
    }

    [Fact]
    public void BackoffDay_RollsAtFiveLocal()
    {
        var offset = TimeSpan.FromHours(2);
        Assert.Equal(
            BreakBackoffPolicy.ResolveBackoffDay(new DateTimeOffset(2026, 07, 07, 23, 50, 0, offset)),
            BreakBackoffPolicy.ResolveBackoffDay(new DateTimeOffset(2026, 07, 08, 4, 59, 0, offset)));
        Assert.NotEqual(
            BreakBackoffPolicy.ResolveBackoffDay(new DateTimeOffset(2026, 07, 08, 4, 59, 0, offset)),
            BreakBackoffPolicy.ResolveBackoffDay(new DateTimeOffset(2026, 07, 08, 5, 1, 0, offset)));
    }

    // ── Backoff state in the store: outcome wiring + resets ──

    [Fact]
    public void DismissedBreak_IncrementsStreakAndExtendsCooldown()
    {
        RunWithDb((connection, tx) =>
        {
            DateTimeOffset ackAt = DateTimeOffset.UtcNow;
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(
                connection, tx, "short_break", "dismissed", ackAt, 1800);

            // Streak is now 1 → next ask is 1h out, anchored at the dismissal.
            int streak = SqliteBrainStateStore.UpdateBreakBackoffForBucket(
                connection, tx, ackAt.ToUnixTimeMilliseconds(), BuildSession(saSeconds: 600d));
            Assert.Equal(1, streak);

            BreakPolicyCooldownSnapshot cooldown = SqliteBrainStateStore.ParseBreakPolicyCooldownSnapshotJson(
                ReadRuntimeState(connection, tx, PortaExecutionConstants.BreakPolicy.RuntimeStateKey),
                ackAt.ToUnixTimeMilliseconds());
            Assert.False(cooldown.CooldownExpired);
            Assert.Equal(ackAt.ToUnixTimeMilliseconds() + 3_600_000, cooldown.CooldownEndTsMs);

            // Second dismissal → streak 2, cooldown 2h from that ack.
            DateTimeOffset secondAck = ackAt.AddHours(1);
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(
                connection, tx, "short_break", "skipped", secondAck, 1800);
            Assert.Equal(2, SqliteBrainStateStore.UpdateBreakBackoffForBucket(
                connection, tx, secondAck.ToUnixTimeMilliseconds(), BuildSession(saSeconds: 600d)));
            cooldown = SqliteBrainStateStore.ParseBreakPolicyCooldownSnapshotJson(
                ReadRuntimeState(connection, tx, PortaExecutionConstants.BreakPolicy.RuntimeStateKey),
                secondAck.ToUnixTimeMilliseconds());
            Assert.Equal(secondAck.ToUnixTimeMilliseconds() + 7_200_000, cooldown.CooldownEndTsMs);
        });
    }

    [Fact]
    public void AcceptedBreak_ResetsStreak()
    {
        RunWithDb((connection, tx) =>
        {
            DateTimeOffset ackAt = DateTimeOffset.UtcNow;
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(connection, tx, "short_break", "dismissed", ackAt, 1800);
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(connection, tx, "short_break", "dismissed", ackAt, 1800);
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(connection, tx, "long_break", "accepted", ackAt, 1800);

            Assert.Equal(0, SqliteBrainStateStore.UpdateBreakBackoffForBucket(
                connection, tx, ackAt.ToUnixTimeMilliseconds(), BuildSession(saSeconds: 600d)));
        });
    }

    [Fact]
    public void SnoozedBreak_IsNeutral()
    {
        RunWithDb((connection, tx) =>
        {
            DateTimeOffset ackAt = DateTimeOffset.UtcNow;
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(connection, tx, "short_break", "dismissed", ackAt, 1800);
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(connection, tx, "short_break", "snoozed", ackAt, 1800);

            // Snooze neither increments nor resets — "later" is engagement, not refusal.
            Assert.Equal(1, SqliteBrainStateStore.UpdateBreakBackoffForBucket(
                connection, tx, ackAt.ToUnixTimeMilliseconds(), BuildSession(saSeconds: 600d)));
        });
    }

    [Fact]
    public void NonBreakAction_NeverTouchesTheStreak()
    {
        RunWithDb((connection, tx) =>
        {
            DateTimeOffset ackAt = DateTimeOffset.UtcNow;
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(
                connection, tx, "focus_lock_in_suggestion", "dismissed", ackAt, 1800);
            Assert.Equal(0, SqliteBrainStateStore.UpdateBreakBackoffForBucket(
                connection, tx, ackAt.ToUnixTimeMilliseconds(), BuildSession(saSeconds: 600d)));
        });
    }

    [Fact]
    public void NaturalLongIdle_ResetsStreak()
    {
        RunWithDb((connection, tx) =>
        {
            DateTimeOffset ackAt = DateTimeOffset.UtcNow;
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(connection, tx, "short_break", "dismissed", ackAt, 1800);

            DerivedSessionMetricsResult rested = BuildSession(saSeconds: 0d) with
            {
                SaFreezeReason = "long_break",
                SaIdlePauseSeconds = PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds
            };
            Assert.Equal(0, SqliteBrainStateStore.UpdateBreakBackoffForBucket(
                connection, tx, ackAt.ToUnixTimeMilliseconds(), rested));
        });
    }

    [Fact]
    public void NewMorning_ResetsStreak()
    {
        RunWithDb((connection, tx) =>
        {
            DateTimeOffset ackAt = DateTimeOffset.UtcNow;
            SqliteBrainStateStore.ApplyBreakBackoffAckSideEffects(connection, tx, "short_break", "dismissed", ackAt, 1800);

            // First bucket a day later lands in a new backoff day → streak resets.
            long nextDayMs = ackAt.AddDays(1).ToUnixTimeMilliseconds();
            Assert.Equal(0, SqliteBrainStateStore.UpdateBreakBackoffForBucket(
                connection, tx, nextDayMs, BuildSession(saSeconds: 600d)));
        });
    }

    // ── Reason copy (1.4) ──

    [Fact]
    public void ReasonText_EachTriggerProducesItsSpecificCopy()
    {
        Assert.Equal(
            "Pressure has been above your usual level for a while — worth 5 minutes away.",
            Porta.Agent.Common.BreakReasonText.For("pressure_above_personal", "short_break", 0d, 0d));
        Assert.Equal(
            "Signs of overload for the last 12 min — a short reset helps.",
            Porta.Agent.Common.BreakReasonText.For("overload_active", "short_break", 12.2d, 0d));
        Assert.Equal(
            "Signs of overload right now — a short reset helps.",
            Porta.Agent.Common.BreakReasonText.For("overload_active", "short_break", 0d, 0d));
        Assert.Equal(
            "Signs of rising overload — a short reset helps.",
            Porta.Agent.Common.BreakReasonText.For("overload_likelihood", "short_break", 0d, 0d));
        Assert.Equal(
            "You've been at it 1.5h without a real pause.",
            Porta.Agent.Common.BreakReasonText.For("long_focus", "long_break", 0d, 1.4d));
        Assert.Equal(
            "You've been at it 2h without a real pause.",
            Porta.Agent.Common.BreakReasonText.For("long_focus", "long_break", 0d, 2.05d));
    }

    [Fact]
    public void ReasonText_NeverEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(
            Porta.Agent.Common.BreakReasonText.For(string.Empty, "short_break", 0d, 0d)));
        Assert.False(string.IsNullOrWhiteSpace(
            Porta.Agent.Common.BreakReasonText.For("unknown_reason", "short_break", 0d, 0d)));
    }

    [Fact]
    public void ComposeBreakPromptMessage_UsesWhyJsonReason()
    {
        AgentControlEvent ev = BuildBreakEvent(
            whyJson: """{"schema_ver":1,"reasons":{"need_gate_reason":"pressure_above_personal","overload_on_dwell_ms":0,"effective_focus_s":1500}}""");
        string message = SqliteBrainStateStore.ComposeBreakPromptMessage(ev, "short_break");
        Assert.StartsWith("Pressure has been above your usual level", message, StringComparison.Ordinal);
        Assert.Contains("Take a short break now?", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeBreakPromptMessage_MalformedWhyJson_StillHonestCopy()
    {
        AgentControlEvent ev = BuildBreakEvent(whyJson: "{not json");
        string message = SqliteBrainStateStore.ComposeBreakPromptMessage(ev, "long_break");
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("Ready for a longer break now?", message, StringComparison.Ordinal);
    }

    // ── Helpers ──

    private static BreakSuggestionDecision TriggerShortBreak(
        double pressureLevel,
        BreakPersonalizationSnapshot personalization)
    {
        return BreakSuggestionPolicy.ShouldTriggerBreak(
            BaselineGatingResult.Warm,
            BuildStabilization(overloadActive: false),
            BuildOverloadInference(confidence: 0.80d, overloadLikelihood: 0.20d, pressureLevel: pressureLevel),
            BuildSession(saSeconds: 1600d),
            BuildFocusWindow(
                shortBreakSeconds: PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowPressure,
                longBreakSeconds: 2400d,
                boundaryKind: "short_idle"),
            resolveCooldown: () => new BreakPolicyCooldownSnapshot(true, 0, 0),
            personalization: personalization);
    }

    private static void RunWithDb(Action<SqliteConnection, SqliteTransaction> body)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Migrations.Migrate(connection);
        using SqliteTransaction tx = connection.BeginTransaction();
        body(connection, tx);
        tx.Commit();
    }

    private static string? ReadRuntimeState(SqliteConnection connection, SqliteTransaction tx, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static AgentControlEvent BuildBreakEvent(string whyJson)
    {
        return new AgentControlEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            Action: "short_break",
            TriggerState: "focus",
            TriggerValue: 0.5d,
            Confidence: 0.8d,
            DurationSeconds: 180,
            EscalationLevel: "Suggest",
            Reversible: false,
            Reason: "preconditions_met",
            Contributors: ["focus_switch_rate_s"],
            InterventionId: Guid.NewGuid().ToString("N"),
            PolicyId: "break-policy:test",
            Domain: "fatigue",
            PlannedEndTimestampUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: "Short break suggested.",
            HowToUndo: "Dismiss suggestion toast.",
            ReversalApplied: false,
            CooldownEndTimestampUtc: DateTimeOffset.UtcNow.AddMinutes(30),
            LifecycleState: "activated",
            WhyJsonOverride: whyJson,
            IdempotencyKey: Guid.NewGuid().ToString("N"));
    }

    private static OverloadInferenceResult BuildOverloadInference(
        double confidence,
        double overloadLikelihood,
        double pressureLevel)
    {
        return new OverloadInferenceResult(
            OverloadLikelihood: overloadLikelihood,
            Confidence: confidence,
            FComponent: 0.8d,
            IComponent: 0.8d,
            VComponent: 0.8d,
            FTopPrimitives: [],
            ITopPrimitives: [],
            VTopPrimitives: [],
            Quality: 1d,
            Agreement: 1d,
            PrimitiveCoverage: 1d,
            MissingPrimitiveCount: 0,
            UsedSnapshotFallback: false,
            Source: "bucket_finalizer",
            ComponentsRegistryVersion: 1,
            PressureLevel: pressureLevel);
    }

    private static OverloadStabilizationResult BuildStabilization(bool overloadActive)
    {
        return new OverloadStabilizationResult(
            OverloadActive: overloadActive,
            RuntimeState: new OverloadStabilizationRuntimeState(
                OverloadActive: overloadActive,
                OnDwellMs: 0,
                OffDwellMs: 0,
                LastGoodTsMs: Time.bucketStartEnd(1).endUtcMs,
                LastBucketSeq: 1,
                LastTransitionTsMs: Time.bucketStartEnd(1).endUtcMs),
            DebugMode: "stable_hold",
            Transition: string.Empty,
            Transitioned: false,
            PreviousOverloadActive: overloadActive,
            GapExceeded: false,
            LowQualityPaused: false,
            Eligible: true,
            OnSignal: overloadActive,
            OffSignal: !overloadActive,
            BetweenThresholds: false,
            GapMs: 0,
            OverloadLikelihood: overloadActive ? 0.9d : 0.2d,
            Confidence: 0.9d,
            QualityScore: 1d);
    }

    private static DerivedSessionMetricsResult BuildSession(double saSeconds)
    {
        return new DerivedSessionMetricsResult(
            SustainedAttentionDurationSec: saSeconds,
            IdleFragmentationIndex: 0.1d,
            Partial: false,
            DominantAppHash: "app",
            SaIdlePauseSeconds: 0d,
            SaContextSwitchSeconds: 0d,
            SaFreezeReason: string.Empty,
            SaUntrusted: false,
            TaskContinuityScore: 0.8d,
            TaskContinuitySeconds: Math.Max(1d, saSeconds),
            ContinuityState: "keep",
            ContinuityConfidence: 0.82d);
    }

    private static BreakFocusWindowSnapshot BuildFocusWindow(
        double shortBreakSeconds,
        double longBreakSeconds,
        string boundaryKind)
    {
        return new BreakFocusWindowSnapshot(
            CurrentBucketEffectiveFocusSeconds: Time.bucketSeconds,
            ShortBreakWindowEffectiveFocusSeconds: shortBreakSeconds,
            LongBreakWindowEffectiveFocusSeconds: longBreakSeconds,
            DeliveryBoundaryReached: !string.IsNullOrWhiteSpace(boundaryKind),
            DeliveryBoundaryKind: boundaryKind,
            HighPressure: false,
            FlowStateActive: false,
            BreakDueSinceUtcMs: 0);
    }
}
