using System.Text.Json;
using Porta.Agent.Common;
using Porta.Agent.Core.Tests.Harness;
using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests.Acceptance;

public sealed class Phase09ValidationHarnessTests
{
    private const int WarmupBuckets = 220;
    private const int TypingOnlyScenarioBuckets = 96;
    private const int ReadingOnlyScenarioBuckets = 96;
    private const int QuickExcursionBuckets = 10;
    private const int LongAwayBuckets = 65;
    private const int MediumTrustBreakSearchBuckets = 520;
    private const int HighTrustSuppressionSearchBuckets = 360;

    private static readonly ScriptedBucketInput FocusedWork = new(
        TypingBurstIntensity: 0.86d,
        ScrollVelocityIndex: 0.14d,
        FocusSwitchRate: 0.10d,
        AppHash: "app_focus",
        KeyboardKeydowns: 4,
        MouseMoves: 3,
        MouseWheels: 1,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput TypingOnlyWork = new(
        TypingBurstIntensity: 0.82d,
        ScrollVelocityIndex: 0.02d,
        FocusSwitchRate: 0.08d,
        AppHash: "app_typing",
        KeyboardKeydowns: 6,
        MouseMoves: 0,
        MouseWheels: 0,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput ReadingScrollWork = new(
        TypingBurstIntensity: 0.02d,
        ScrollVelocityIndex: 0.58d,
        FocusSwitchRate: 0.08d,
        AppHash: "app_reading",
        KeyboardKeydowns: 0,
        MouseMoves: 4,
        MouseWheels: 6,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput ScrollOnlyTypingApp = new(
        TypingBurstIntensity: 0.02d,
        ScrollVelocityIndex: 0.52d,
        FocusSwitchRate: 0.10d,
        AppHash: "app_typing",
        KeyboardKeydowns: 0,
        MouseMoves: 3,
        MouseWheels: 5,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput ThinkingPause = new(
        TypingBurstIntensity: 0.02d,
        ScrollVelocityIndex: 0.01d,
        FocusSwitchRate: 0.05d,
        AppHash: "app_focus",
        KeyboardKeydowns: 0,
        MouseMoves: 0,
        MouseWheels: 0,
        IdleSeconds: 5d);

    private static readonly ScriptedBucketInput QuickExcursion = new(
        TypingBurstIntensity: 0.22d,
        ScrollVelocityIndex: 0.10d,
        FocusSwitchRate: 0.08d,
        AppHash: "app_mail",
        KeyboardKeydowns: 1,
        MouseMoves: 1,
        MouseWheels: 0,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput LongAwayContext = new(
        TypingBurstIntensity: 0.18d,
        ScrollVelocityIndex: 0.18d,
        FocusSwitchRate: 0.08d,
        AppHash: "app_other",
        KeyboardKeydowns: 1,
        MouseMoves: 1,
        MouseWheels: 0,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput BreakBoundaryPause = new(
        TypingBurstIntensity: 0.04d,
        ScrollVelocityIndex: 0.01d,
        FocusSwitchRate: 0.05d,
        AppHash: "app_typing",
        KeyboardKeydowns: 0,
        MouseMoves: 0,
        MouseWheels: 0,
        IdleSeconds: 4d);

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Replay_TypingOnlyForSeveralMinutes_MaintainsContinuityWithoutReset()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-09T08:00:00.000+00:00"),
            seed: 94001);
        WarmupBaseline(runner, 48, "app_typing");

        var states = new List<string>(TypingOnlyScenarioBuckets);
        FinalizerTickObservation last = default;
        for (int i = 0; i < TypingOnlyScenarioBuckets; i++)
        {
            last = runner.RunBucket(TypingOnlyWork);
            states.Add(ReadContinuityState(last.LatestState));
        }

        Assert.DoesNotContain("reset", states);
        Assert.NotNull(last.LatestState);
        Assert.False(last.LatestState!.PartialData ?? true);
        Assert.True(
            last.LatestState.SustainedAttentionDurationSec >= 300d,
            $"Expected typing-only continuity to accumulate for several minutes; got {last.LatestState.SustainedAttentionDurationSec:F1}s.");
        Assert.True(
            ReadContinuityScore(last.LatestState) >= 0.75d,
            $"Expected strong continuity score during typing-only work; got {ReadContinuityScore(last.LatestState):F2}.");
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Replay_ReadingAndScrollingWithLittleTyping_MaintainsContinuityWithoutReset()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-09T09:00:00.000+00:00"),
            seed: 94002);
        WarmupBaseline(runner, 48, "app_reading");

        var states = new List<string>(ReadingOnlyScenarioBuckets);
        FinalizerTickObservation last = default;
        for (int i = 0; i < ReadingOnlyScenarioBuckets; i++)
        {
            last = runner.RunBucket(ReadingScrollWork);
            states.Add(ReadContinuityState(last.LatestState));
        }

        Assert.DoesNotContain("reset", states);
        Assert.NotNull(last.LatestState);
        Assert.False(last.LatestState!.PartialData ?? true);
        Assert.True(
            last.LatestState.SustainedAttentionDurationSec >= 300d,
            $"Expected reading/scrolling continuity to accumulate for several minutes; got {last.LatestState.SustainedAttentionDurationSec:F1}s.");
        Assert.True(
            ReadContinuityScore(last.LatestState) >= 0.70d,
            $"Expected continuity score to stay healthy during reading/scrolling; got {ReadContinuityScore(last.LatestState):F2}.");
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Replay_ThinkingPauseAndQuickReturn_DoNotResetContinuity_ButLongAwayContextDoes()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-09T10:00:00.000+00:00"),
            seed: 94003);
        WarmupBaseline(runner, 80, "app_focus");

        FinalizerTickObservation last = default;
        for (int i = 0; i < 24; i++)
        {
            last = runner.RunBucket(FocusedWork);
        }

        double continuityBeforePause = last.LatestState?.SustainedAttentionDurationSec ?? 0d;
        var shortInterruptionStates = new List<string>();

        for (int i = 0; i < 4; i++)
        {
            last = runner.RunBucket(ThinkingPause);
            shortInterruptionStates.Add(ReadContinuityState(last.LatestState));
        }

        for (int i = 0; i < QuickExcursionBuckets; i++)
        {
            last = runner.RunBucket(QuickExcursion);
            shortInterruptionStates.Add(ReadContinuityState(last.LatestState));
        }

        for (int i = 0; i < 4; i++)
        {
            last = runner.RunBucket(FocusedWork);
            shortInterruptionStates.Add(ReadContinuityState(last.LatestState));
        }

        Assert.DoesNotContain("reset", shortInterruptionStates);
        Assert.NotNull(last.LatestState);
        Assert.True(
            last.LatestState!.SustainedAttentionDurationSec >= continuityBeforePause * 0.60d,
            $"Continuity decayed too aggressively during short pause/quick return. before={continuityBeforePause:F1}s after={last.LatestState.SustainedAttentionDurationSec:F1}s");

        bool sawReset = false;
        for (int i = 0; i < LongAwayBuckets; i++)
        {
            last = runner.RunBucket(LongAwayContext);
            sawReset |= string.Equals(ReadContinuityState(last.LatestState), "reset", StringComparison.Ordinal);
        }

        last = runner.RunBucket(FocusedWork);

        Assert.True(sawReset, "Expected continuity to reset after being away from the working sphere for more than 3 minutes.");
        Assert.NotNull(last.LatestState);
        Assert.True(
            last.LatestState!.SustainedAttentionDurationSec <= continuityBeforePause * 0.25d,
            $"Expected continuity to be materially reset after long away-context. before={continuityBeforePause:F1}s after={last.LatestState.SustainedAttentionDurationSec:F1}s");
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Replay_LowTrustColdStart_DoesNotProduceAutomaticSessionActions()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-09T11:00:00.000+00:00"),
            seed: 94004);

        FinalizerTickObservation last = default;
        for (int i = 0; i < 18; i++)
        {
            last = runner.RunBucket(TypingOnlyWork);
        }

        LocalApiProjectionSnapshot projection = runner.ReadLocalApiProjection();

        Assert.NotNull(last.LatestState);
        Assert.Equal(CalibrationTruth.TrustLow, CalibrationTruth.NormalizeTrustTier(last.LatestState!.TrustTier));
        Assert.Equal(CalibrationTruth.StageCold, CalibrationTruth.NormalizeStage(last.LatestState.CalibrationStage));
        Assert.False(last.LatestState.SystemActionsEnabled ?? true);
        Assert.False(projection.Status.NotificationsSuppressed);
        Assert.False(HasEvent(runner, "deep_work_notifications_suppressed"));
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Acceptance_MediumTrust_AllowsRollingFocusSuggestionWithoutAutomaticSessionChanges()
    {
        BaselineGatingResult gating = BaselineGatingResult.Provisional(0.68d);
        OverloadInferenceResult overloadInference = BuildAcceptanceOverloadInference(confidence: 0.62d, quality: 0.72d);
        DerivedSessionMetricsResult session = BuildAcceptanceSession(taskContinuitySeconds: 1750d);
        BreakFocusWindowSnapshot focusWindow = new(
            CurrentBucketEffectiveFocusSeconds: Time.bucketSeconds,
            ShortBreakWindowEffectiveFocusSeconds: PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowPressure,
            LongBreakWindowEffectiveFocusSeconds: 2100d,
            DeliveryBoundaryReached: true,
            DeliveryBoundaryKind: "short_idle",
            HighPressure: false);

        string trustTier = CalibrationTruth.ResolveTrustTier(
            gating.CalibrationStage,
            overloadInference.Confidence,
            overloadInference.Quality,
            session.Partial);

        BreakSuggestionDecision decision = BreakSuggestionPolicy.ShouldTriggerBreak(
            gating,
            BuildAcceptanceOverloadStabilization(overloadActive: false),
            overloadInference,
            session,
            focusWindow,
            resolveCooldown: () => new BreakPolicyCooldownSnapshot(true, 0, 0));

        Assert.Equal(CalibrationTruth.TrustMedium, trustTier);
        Assert.True(CalibrationTruth.AllowsSuggestionsForTrust(trustTier));
        Assert.False(CalibrationTruth.AllowsAutomaticActionsForTrust(trustTier));
        Assert.True(decision.ShouldEmit);
        Assert.Equal("short_break", decision.Action);
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Replay_HighTrustIsRequiredForAutomaticSessionChanges()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-09T13:00:00.000+00:00"),
            seed: 94006);
        WarmupBaseline(runner);

        string? trustAtSuppression = null;
        bool? systemActionsEnabledAtSuppression = null;

        for (int i = 0; i < HighTrustSuppressionSearchBuckets; i++)
        {
            FinalizerTickObservation observation = runner.RunBucket(FocusedWork);
            if (HasEvent(runner, "deep_work_notifications_suppressed"))
            {
                trustAtSuppression = CalibrationTruth.NormalizeTrustTier(observation.LatestState?.TrustTier);
                systemActionsEnabledAtSuppression = observation.LatestState?.SystemActionsEnabled;
                break;
            }
        }

        LocalApiProjectionSnapshot projection = runner.ReadLocalApiProjection();

        Assert.True(HasEvent(runner, "deep_work_notifications_suppressed"), "Expected deep work notification suppression to apply only after the scenario reached high trust.");
        Assert.Equal(CalibrationTruth.TrustHigh, trustAtSuppression);
        Assert.True(systemActionsEnabledAtSuppression);
        Assert.True(projection.Status.NotificationsSuppressed);
    }

    private static ScriptedBucketInput BuildWarmupBucket(int bucketIndex, string appHash = "app_focus")
    {
        double phaseA = (bucketIndex % 17) / 16d;
        double phaseB = ((bucketIndex * 3) % 17) / 16d;
        return new ScriptedBucketInput(
            TypingBurstIntensity: 0.30d + (0.40d * phaseA),
            ScrollVelocityIndex: 0.15d + (0.35d * phaseA),
            FocusSwitchRate: 0.15d + (0.35d * phaseB),
            AppHash: appHash,
            KeyboardKeydowns: 4,
            MouseMoves: 3,
            MouseWheels: 1,
            IdleSeconds: 0d);
    }

    private static void WarmupBaseline(
        DeterministicFinalizerScenarioRunner runner,
        int bucketCount = WarmupBuckets,
        string appHash = "app_focus")
    {
        for (int i = 0; i < bucketCount; i++)
        {
            runner.RunBucket(BuildWarmupBucket(i, appHash));
        }
    }

    private static ScriptedBucketInput BuildMediumTrustBreakBucket(int bucketIndex)
    {
        if ((bucketIndex % 54) == 53)
        {
            return BreakBoundaryPause;
        }

        if ((bucketIndex % 120) is >= 112 and <= 115)
        {
            return QuickExcursion;
        }

        return new ScriptedBucketInput(
            TypingBurstIntensity: FocusedWork.TypingBurstIntensity,
            ScrollVelocityIndex: FocusedWork.ScrollVelocityIndex,
            FocusSwitchRate: FocusedWork.FocusSwitchRate,
            AppHash: "app_typing",
            KeyboardKeydowns: FocusedWork.KeyboardKeydowns,
            MouseMoves: FocusedWork.MouseMoves,
            MouseWheels: FocusedWork.MouseWheels,
            IdleSeconds: FocusedWork.IdleSeconds);
    }

    private static string ReadContinuityState(AgentLatestStateDetails? latestState)
    {
        if (string.IsNullOrWhiteSpace(latestState?.StateFlagsJson))
        {
            return "reset";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(latestState.StateFlagsJson);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("continuity_state", out JsonElement continuity)
                && continuity.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(continuity.GetString()))
            {
                return continuity.GetString()!.Trim().ToLowerInvariant();
            }
        }
        catch
        {
        }

        return "reset";
    }

    private static double ReadContinuityScore(AgentLatestStateDetails? latestState)
    {
        if (string.IsNullOrWhiteSpace(latestState?.StateFlagsJson))
        {
            return 0d;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(latestState.StateFlagsJson);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("task_continuity_score", out JsonElement score)
                && score.ValueKind == JsonValueKind.Number
                && score.TryGetDouble(out double parsed))
            {
                return Math.Clamp(parsed, 0d, 1d);
            }
        }
        catch
        {
        }

        return 0d;
    }

    private static bool HasEvent(DeterministicFinalizerScenarioRunner runner, string action)
    {
        return runner.ReadRecentEventRows(4000)
            .Any(row => string.Equals(row.Action, action, StringComparison.Ordinal));
    }

    private static int CountBreakFamilyEvents(DeterministicFinalizerScenarioRunner runner)
    {
        return runner.ReadRecentEventRows(4000)
            .Count(row => IsBreakFamilyAction(row.Action));
    }

    private static bool IsBreakFamilyAction(string action)
    {
        return PortaBreakSemantics.IsBreakSuggestionAction(action);
    }

    private static string? ReadLatestBreakAction(DeterministicFinalizerScenarioRunner runner)
    {
        return runner.ReadRecentEventRows(4000)
            .FirstOrDefault(row => IsBreakFamilyAction(row.Action))
            .Action;
    }

    private static OverloadInferenceResult BuildAcceptanceOverloadInference(double confidence, double quality)
    {
        return new OverloadInferenceResult(
            OverloadLikelihood: 0.46d,
            Confidence: confidence,
            FComponent: 0.42d,
            IComponent: 0.55d,
            VComponent: 0.38d,
            FTopPrimitives: [],
            ITopPrimitives: [],
            VTopPrimitives: [],
            Quality: quality,
            Agreement: 0.52d,
            PrimitiveCoverage: 1d,
            MissingPrimitiveCount: 0,
            UsedSnapshotFallback: false,
            Source: "acceptance_harness",
            ComponentsRegistryVersion: 1);
    }

    private static OverloadStabilizationResult BuildAcceptanceOverloadStabilization(bool overloadActive)
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
            OverloadLikelihood: overloadActive ? 0.78d : 0.46d,
            Confidence: 0.62d,
            QualityScore: 0.72d);
    }

    private static DerivedSessionMetricsResult BuildAcceptanceSession(double taskContinuitySeconds)
    {
        return new DerivedSessionMetricsResult(
            SustainedAttentionDurationSec: taskContinuitySeconds,
            IdleFragmentationIndex: 0.12d,
            Partial: false,
            DominantAppHash: "app_typing",
            SaIdlePauseSeconds: 0d,
            SaContextSwitchSeconds: 0d,
            SaFreezeReason: string.Empty,
            SaUntrusted: false,
            TaskContinuityScore: 0.86d,
            TaskContinuitySeconds: taskContinuitySeconds,
            ContinuityState: "keep",
            ContinuityConfidence: 0.74d);
    }

    private static void PinOverloadActiveRuntime(DeterministicFinalizerScenarioRunner runner)
    {
        long nowMs = runner.Clock.UtcNow.ToUnixTimeMilliseconds();
        long bucketSeq = Time.bucketSeqFromEventTs(nowMs);
        string overloadStateJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.OverloadStabilization.ContractVersion,
            overload_active = true,
            on_dwell_ms = PortaExecutionConstants.OverloadStabilization.DwellOnSeconds * 1000,
            off_dwell_ms = 0,
            last_good_ts_ms = nowMs,
            last_bucket_seq = bucketSeq,
            last_transition_ts_ms = nowMs
        });
        string overloadActiveJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.OverloadStabilization.ContractVersion,
            value = true,
            overload_active = true,
            bucket_seq = bucketSeq,
            updated_utc = runner.Clock.UtcNow.ToString("O")
        });

        runner.UpsertRuntimeStateValue(
            PortaExecutionConstants.OverloadStabilization.RuntimeStateKey,
            overloadStateJson,
            runner.Clock.UtcNow);
        runner.UpsertRuntimeStateValue(
            PortaExecutionConstants.OverloadStabilization.ActiveRuntimeStateKey,
            overloadActiveJson,
            runner.Clock.UtcNow);
    }
}
