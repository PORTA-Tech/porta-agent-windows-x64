using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Db;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Tests;

public sealed class BreakSuggestionPolicyTests
{
    [Fact]
    public void ShouldTriggerBreak_SuggestionsNotEnabled_FailsWithoutCooldownLookup()
    {
        int cooldownReads = 0;
        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            BaselineGatingResult.Cold,
            BuildStabilization(overloadActive: true),
            BuildOverloadInference(confidence: 0.9d),
            BuildSession(saSeconds: 7200d),
            resolveCooldown: () =>
            {
                cooldownReads++;
                return new BreakPolicyCooldownSnapshot(true, 0, 0);
            });

        Assert.False(decision.ShouldEmit);
        Assert.Equal("suggestions_not_enabled", decision.ReasonCode);
        Assert.Equal(0, cooldownReads);
        Assert.False(decision.CooldownChecked);
    }

    [Fact]
    public void ShouldTriggerBreak_LowConfidence_FailsWithoutCooldownLookup()
    {
        int cooldownReads = 0;
        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            BaselineGatingResult.Warm,
            BuildStabilization(overloadActive: true),
            BuildOverloadInference(confidence: 0.54d),
            BuildSession(saSeconds: 7200d),
            resolveCooldown: () =>
            {
                cooldownReads++;
                return new BreakPolicyCooldownSnapshot(true, 0, 0);
            });

        Assert.False(decision.ShouldEmit);
        Assert.Equal("confidence_below_min", decision.ReasonCode);
        Assert.Equal(0, cooldownReads);
        Assert.False(decision.CooldownChecked);
    }

    [Fact]
    public void ShouldTriggerBreak_AllPreconditionsPass_Emits()
    {
        int cooldownReads = 0;
        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            BaselineGatingResult.Warm,
            BuildStabilization(overloadActive: true),
            BuildOverloadInference(confidence: 0.60d),
            BuildSession(saSeconds: 3600d),
            resolveCooldown: () =>
            {
                cooldownReads++;
                return new BreakPolicyCooldownSnapshot(true, 0, 0);
            });

        Assert.True(decision.ShouldEmit);
        Assert.Equal("preconditions_met", decision.ReasonCode);
        Assert.Equal(1, cooldownReads);
        Assert.True(decision.CooldownChecked);
    }

    [Fact]
    public void ShouldTriggerBreak_CooldownActive_FailsAfterCooldownLookup()
    {
        BreakPolicyCooldownSnapshot cooldown = new(
            CooldownExpired: false,
            CooldownEndTsMs: Time.parseIsoUtcMs("2026-03-03T00:30:00.000Z"),
            CooldownRemainingMs: 600000);

        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            BaselineGatingResult.Warm,
            BuildStabilization(overloadActive: true),
            BuildOverloadInference(confidence: 0.80d),
            BuildSession(saSeconds: 7200d),
            resolveCooldown: () => cooldown);

        Assert.False(decision.ShouldEmit);
        Assert.Equal("cooldown_active", decision.ReasonCode);
        Assert.True(decision.CooldownChecked);
        Assert.False(decision.Cooldown.CooldownExpired);
    }

    [Fact]
    public void ParseBreakPolicyCooldownSnapshotJson_UsesStep13CFieldsAndExpiryRule()
    {
        long nowMs = Time.parseIsoUtcMs("2026-03-03T00:00:00.000Z");
        long cooldownUntilMs = Time.parseIsoUtcMs("2026-03-03T00:30:00.000Z");
        string json =
            $$"""
              {
                "schema_ver": 1,
                "break_cooldown_until_utc_ms": {{cooldownUntilMs}},
                "break_last_fired_utc_ms": {{nowMs}}
              }
              """;

        BreakPolicyCooldownSnapshot active = SqliteBrainStateStore.ParseBreakPolicyCooldownSnapshotJson(json, nowMs);
        Assert.False(active.CooldownExpired);
        Assert.Equal(cooldownUntilMs, active.CooldownEndTsMs);
        Assert.Equal(1_800_000, active.CooldownRemainingMs);

        BreakPolicyCooldownSnapshot expired = SqliteBrainStateStore.ParseBreakPolicyCooldownSnapshotJson(
            json,
            nowMs + 1_800_000);
        Assert.True(expired.CooldownExpired);
        Assert.Equal(0, expired.CooldownRemainingMs);
    }

    [Fact]
    public void BuildBreakEventIdempotencyKey_IsStableWithinCooldownWindow()
    {
        string k1 = SqliteBrainStateStore.BuildBreakEventIdempotencyKey(
            nowUtcMs: Time.parseIsoUtcMs("2026-03-03T00:05:00.000Z"),
            installIdHash: "abc123");
        string k2 = SqliteBrainStateStore.BuildBreakEventIdempotencyKey(
            nowUtcMs: Time.parseIsoUtcMs("2026-03-03T00:29:59.999Z"),
            installIdHash: "abc123");

        Assert.Equal(k1, k2);
        Assert.StartsWith("break_v1:", k1, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBreakEventIdempotencyKey_ChangesAcrossCooldownWindows()
    {
        string k1 = SqliteBrainStateStore.BuildBreakEventIdempotencyKey(
            nowUtcMs: Time.parseIsoUtcMs("2026-03-03T00:29:59.999Z"),
            installIdHash: "abc123");
        string k2 = SqliteBrainStateStore.BuildBreakEventIdempotencyKey(
            nowUtcMs: Time.parseIsoUtcMs("2026-03-03T00:30:00.000Z"),
            installIdHash: "abc123");

        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void BuildBreakSuggestionWhyJson_UsesSchemaVer1AndRequiredFacts()
    {
        InputBucketRow inputBucket = new(
            BucketSeq: 123456,
            TimestampUtc: "2026-03-03T00:00:00.000Z",
            StartUtc: "2026-03-03T00:00:00.000Z",
            TypingBurstIntensity: 0d,
            ScrollVelocityIndex: 0d,
            FocusSwitchRate: 0d,
            KeyboardIkiMean: 0d,
            KeyboardIkiCv: 0d,
            KeyboardBurstRate: 0d,
            KeyboardBackspaceRate: 0d,
            MouseSpeedMean: 0d,
            MouseJerkP95: 0d,
            MouseScrollSpeed: 0d,
            MouseMicroCorrectionRate: 0d,
            FocusAppEntropy: 0d,
            FocusSwitchCount: 0,
            DominantAppHash: "ignored_here",
            FocusActiveAppCount: 1d,
            IdleSeconds: 0d,
            IdleMicroIdleCount: 0d,
            IdleMicroIdleSeconds: 0d,
            TrueBreakSeconds: 0d,
            TrueBreakFlag: 0,
            QualityScore: 1d,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
        DerivedSessionMetricsResult session = new(
            SustainedAttentionDurationSec: 3725d,
            IdleFragmentationIndex: 0.1d,
            Partial: false,
            DominantAppHash: "abcd1234hash",
            SaIdlePauseSeconds: 0d,
            SaContextSwitchSeconds: 0d,
            SaFreezeReason: string.Empty,
            SaUntrusted: false);
        BaselineGatingResult gating = BaselineGatingResult.Warm;
        OverloadInferenceResult overload = BuildOverloadInference(confidence: 0.71d);
        OverloadStabilizationResult stabilization = BuildStabilization(overloadActive: true);
        BreakSuggestionDecision decision = new(
            ShouldEmit: true,
            ReasonCode: "preconditions_met",
            CooldownChecked: true,
            Cooldown: new BreakPolicyCooldownSnapshot(true, 0, 0));

        string json = SqliteBrainStateStore.BuildBreakSuggestionWhyJson(
            inputBucket,
            session,
            gating,
            overload,
            stabilization,
            decision);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_ver").GetInt32());
        Assert.Equal("break_suggestion_v1", root.GetProperty("policy").GetString());
        JsonElement reasons = root.GetProperty("reasons");
        Assert.True(reasons.GetProperty("baseline_ready").GetBoolean());
        Assert.True(reasons.GetProperty("overload_active").GetBoolean());
        Assert.Equal(0.71d, reasons.GetProperty("confidence").GetDouble(), precision: 6);
        Assert.Equal(3725d, reasons.GetProperty("sustained_attention_s").GetDouble(), precision: 6);
        Assert.True(reasons.GetProperty("cooldown_expired").GetBoolean());
        JsonElement thresholds = root.GetProperty("thresholds");
        Assert.Equal(0.55d, thresholds.GetProperty("confidence_min").GetDouble(), precision: 6);
        Assert.Equal(1800, thresholds.GetProperty("sustained_attention_min_s").GetInt32());
        Assert.Equal(1800, thresholds.GetProperty("cooldown_min_s").GetInt32());
        JsonElement context = root.GetProperty("context");
        Assert.Equal(123456, context.GetProperty("bucket_seq").GetInt64());
        Assert.Equal("abcd1234hash", context.GetProperty("dominant_app_hash").GetString());
    }

    [Fact]
    public void TryInsertBreakSuggestionPolicyEvent_MultipleEligibleBucketsSameWindow_InsertsOnlyOne()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Migrations.Migrate(connection);

        BaselineGatingResult gating = BaselineGatingResult.Warm;
        OverloadInferenceResult overload = BuildOverloadInference(confidence: 0.82d);
        OverloadStabilizationResult stabilization = BuildStabilization(overloadActive: true);
        DerivedSessionMetricsResult session = BuildSession(saSeconds: 3900d);
        BreakSuggestionDecision decision = new(
            ShouldEmit: true,
            ReasonCode: "preconditions_met",
            CooldownChecked: true,
            Cooldown: new BreakPolicyCooldownSnapshot(true, 0, 0));

        InputBucketRow bucketA = BuildMinimalInputBucket(bucketSeq: 1500);
        InputBucketRow bucketB = BuildMinimalInputBucket(bucketSeq: 1501);
        AgentBrainSnapshot snapshotA = BuildMinimalSnapshot(bucketA.BucketSeq, overload.OverloadLikelihood, overload.Confidence);
        AgentBrainSnapshot snapshotB = BuildMinimalSnapshot(bucketB.BucketSeq, overload.OverloadLikelihood, overload.Confidence);

        bool firstInserted;
        bool secondInserted;
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            firstInserted = SqliteBrainStateStore.TryInsertBreakSuggestionPolicyEvent(
                connection,
                tx,
                snapshotA,
                bucketA,
                gating,
                session,
                overload,
                stabilization,
                decision);
            secondInserted = SqliteBrainStateStore.TryInsertBreakSuggestionPolicyEvent(
                connection,
                tx,
                snapshotB,
                bucketB,
                gating,
                session,
                overload,
                stabilization,
                decision);
            tx.Commit();
        }

        Assert.True(firstInserted);
        Assert.False(secondInserted);
        Assert.Equal(1L, CountBreakEvents(connection));

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ts_utc, idempotency_key, why_json, state_flags_json FROM events WHERE action = 'break_suggestion' LIMIT 1;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(Time.toIsoUtcMsString(Time.bucketStartEnd(bucketA.BucketSeq).endUtcMs), reader.GetString(0));
        Assert.False(string.IsNullOrWhiteSpace(reader.GetString(1)));
        Assert.Contains("\"schema_ver\":1", reader.GetString(2), StringComparison.Ordinal);
        Assert.Contains("\"schema_ver\":1", reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public void TryInsertBreakSuggestionPolicyEvent_CooldownActive_DoesNotInsert()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Migrations.Migrate(connection);

        BaselineGatingResult gating = BaselineGatingResult.Warm;
        OverloadInferenceResult overload = BuildOverloadInference(confidence: 0.85d);
        OverloadStabilizationResult stabilization = BuildStabilization(overloadActive: true);
        DerivedSessionMetricsResult session = BuildSession(saSeconds: 4200d);
        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            gating,
            stabilization,
            overload,
            session,
            resolveCooldown: () => new BreakPolicyCooldownSnapshot(
                CooldownExpired: false,
                CooldownEndTsMs: Time.parseIsoUtcMs("2026-03-03T00:30:00.000Z"),
                CooldownRemainingMs: 60_000));

        bool inserted;
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            inserted = SqliteBrainStateStore.TryInsertBreakSuggestionPolicyEvent(
                connection,
                tx,
                BuildMinimalSnapshot(bucketSeq: 1600, overload: overload.OverloadLikelihood, confidence: overload.Confidence),
                BuildMinimalInputBucket(bucketSeq: 1600),
                gating,
                session,
                overload,
                stabilization,
                decision);
            tx.Commit();
        }

        Assert.False(decision.ShouldEmit);
        Assert.False(inserted);
        Assert.Equal(0L, CountBreakEvents(connection));
    }

    private static OverloadInferenceResult BuildOverloadInference(double confidence)
    {
        return new OverloadInferenceResult(
            OverloadLikelihood: 0.9d,
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
            ComponentsRegistryVersion: 1);
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
            OverloadLikelihood: 0.9d,
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
            SaUntrusted: false);
    }

    private static AgentBrainSnapshot BuildMinimalSnapshot(long bucketSeq, double overload, double confidence)
    {
        long startUtcMs = Time.bucketStartEnd(bucketSeq).startUtcMs;
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.FromUnixTimeMilliseconds(startUtcMs),
            TypingBurstIntensity: 0.1d,
            ScrollVelocityIndex: 0.1d,
            FocusSwitchRate: 0.1d,
            SmoothedTypingBurstIntensity: 0.1d,
            SmoothedScrollVelocityIndex: 0.1d,
            SmoothedFocusSwitchRate: 0.1d,
            FocusStrength: 0.2d,
            OverloadLikelihood: overload,
            FatigueProbability: 0.2d,
            CognitiveStability: 0.8d,
            DominantStateValue: 0.8d,
            DominantStateName: "overload",
            Confidence: confidence,
            LastAction: "none",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["focus_switch_rate_s"]);
    }

    private static InputBucketRow BuildMinimalInputBucket(long bucketSeq)
    {
        string startUtc = Time.toIsoUtcMsString(Time.bucketStartEnd(bucketSeq).startUtcMs);
        return new InputBucketRow(
            BucketSeq: bucketSeq,
            TimestampUtc: startUtc,
            StartUtc: startUtc,
            TypingBurstIntensity: 0d,
            ScrollVelocityIndex: 0d,
            FocusSwitchRate: 0d,
            KeyboardIkiMean: 0d,
            KeyboardIkiCv: 0d,
            KeyboardBurstRate: 0d,
            KeyboardBackspaceRate: 0d,
            MouseSpeedMean: 0d,
            MouseJerkP95: 0d,
            MouseScrollSpeed: 0d,
            MouseMicroCorrectionRate: 0d,
            FocusAppEntropy: 0d,
            FocusSwitchCount: 0,
            DominantAppHash: "apphash",
            FocusActiveAppCount: 1d,
            IdleSeconds: 0d,
            IdleMicroIdleCount: 0d,
            IdleMicroIdleSeconds: 0d,
            TrueBreakSeconds: 0d,
            TrueBreakFlag: 0,
            QualityScore: 1d,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
    }

    private static long CountBreakEvents(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE action = 'break_suggestion';";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }
}
