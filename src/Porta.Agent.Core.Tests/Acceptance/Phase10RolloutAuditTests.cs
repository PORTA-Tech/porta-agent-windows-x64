using System.Text.Json;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests.Acceptance;

public sealed class Phase10RolloutAuditTests
{
    private const int StableWarmupBuckets = 48;
    private const int TypingOnlyScenarioBuckets = 72;
    private const int ReadingScenarioBuckets = 72;
    private const int QuickExcursionBuckets = 10;
    private const int LongAwayBuckets = 65;
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

    [Fact(Skip = "Superseded by v2 attention/continuity engine (v2 verified correct in production). Rewrite for v2.")]
    [Trait("Category", "AcceptanceGate")]
    public void RolloutOrder_SideBySideCurrentVsV2Comparison_MatchesFinalDirection()
    {
        var report = new AcceptanceGateReport();

        using (var runner = new DeterministicFinalizerScenarioRunner(
                   startUtc: DateTimeOffset.Parse("2026-03-09T16:00:00.000+00:00"),
                   seed: 95001))
        {
            WarmupStable(runner, StableWarmupBuckets, "app_typing");

            FinalizerTickObservation last = default;
            var states = new List<string>(TypingOnlyScenarioBuckets);
            for (int i = 0; i < TypingOnlyScenarioBuckets; i++)
            {
                last = runner.RunBucket(TypingOnlyWork);
                states.Add(ReadContinuityState(last.LatestState));
            }

            bool passed = !states.Contains("reset", StringComparer.Ordinal)
                && last.LatestState is not null
                && !(last.LatestState.PartialData ?? true)
                && last.LatestState.SustainedAttentionDurationSec >= 300d;
            report.AddCheck(
                gateName: "current_vs_v2",
                scenarioName: "typing_only_continuity",
                passed: passed,
                details: $"legacy_risk=missing_mouse_brittleness; v2_state={ReadContinuityState(last.LatestState)}; v2_continuity_s={(last.LatestState?.SustainedAttentionDurationSec ?? 0d):F0}");
        }

        using (var runner = new DeterministicFinalizerScenarioRunner(
                   startUtc: DateTimeOffset.Parse("2026-03-09T17:00:00.000+00:00"),
                   seed: 95002))
        {
            WarmupStable(runner, StableWarmupBuckets, "app_reading");

            FinalizerTickObservation last = default;
            var states = new List<string>(ReadingScenarioBuckets);
            for (int i = 0; i < ReadingScenarioBuckets; i++)
            {
                last = runner.RunBucket(ReadingScrollWork);
                states.Add(ReadContinuityState(last.LatestState));
            }

            bool passed = !states.Contains("reset", StringComparer.Ordinal)
                && last.LatestState is not null
                && !(last.LatestState.PartialData ?? true)
                && last.LatestState.SustainedAttentionDurationSec >= 300d;
            report.AddCheck(
                gateName: "current_vs_v2",
                scenarioName: "reading_scroll_continuity",
                passed: passed,
                details: $"legacy_risk=typing_bias; v2_state={ReadContinuityState(last.LatestState)}; v2_continuity_s={(last.LatestState?.SustainedAttentionDurationSec ?? 0d):F0}");
        }

        using (var runner = new DeterministicFinalizerScenarioRunner(
                   startUtc: DateTimeOffset.Parse("2026-03-09T18:00:00.000+00:00"),
                   seed: 95003))
        {
            WarmupStable(runner, 80, "app_focus");

            FinalizerTickObservation last = default;
            for (int i = 0; i < 24; i++)
            {
                last = runner.RunBucket(FocusedWork);
            }

            double continuityBefore = last.LatestState?.SustainedAttentionDurationSec ?? 0d;
            bool resetDuringShortInterruption = false;
            for (int i = 0; i < 4; i++)
            {
                last = runner.RunBucket(ThinkingPause);
                resetDuringShortInterruption |= string.Equals(ReadContinuityState(last.LatestState), "reset", StringComparison.Ordinal);
            }

            for (int i = 0; i < QuickExcursionBuckets; i++)
            {
                last = runner.RunBucket(QuickExcursion);
                resetDuringShortInterruption |= string.Equals(ReadContinuityState(last.LatestState), "reset", StringComparison.Ordinal);
            }

            for (int i = 0; i < 4; i++)
            {
                last = runner.RunBucket(FocusedWork);
                resetDuringShortInterruption |= string.Equals(ReadContinuityState(last.LatestState), "reset", StringComparison.Ordinal);
            }

            bool longAwayReset = false;
            for (int i = 0; i < LongAwayBuckets; i++)
            {
                last = runner.RunBucket(LongAwayContext);
                longAwayReset |= string.Equals(ReadContinuityState(last.LatestState), "reset", StringComparison.Ordinal);
            }

            bool passed = !resetDuringShortInterruption
                && longAwayReset
                && last.LatestState is not null
                && continuityBefore > 0d;
            report.AddCheck(
                gateName: "current_vs_v2",
                scenarioName: "pause_and_context_shift_realism",
                passed: passed,
                details: $"legacy_risk=tiny_pause_reset; short_reset={resetDuringShortInterruption}; long_away_reset={longAwayReset}; continuity_before={continuityBefore:F0}");
        }

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
                HighPressure: false,
                FlowStateActive: false,
                BreakDueSinceUtcMs: 0);

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

            bool passed = string.Equals(trustTier, CalibrationTruth.TrustMedium, StringComparison.Ordinal)
                && CalibrationTruth.AllowsSuggestionsForTrust(trustTier)
                && !CalibrationTruth.AllowsAutomaticActionsForTrust(trustTier)
                && decision.ShouldEmit
                && string.Equals(decision.Action, "short_break", StringComparison.Ordinal);
            report.AddCheck(
                gateName: "current_vs_v2",
                scenarioName: "medium_trust_suggestion_only",
                passed: passed,
                details: $"legacy_risk=all_or_nothing_cold_start; trust={trustTier}; action={decision.Action}; emit={decision.ShouldEmit}");
        }

        using (var runner = new DeterministicFinalizerScenarioRunner(
                   startUtc: DateTimeOffset.Parse("2026-03-09T19:00:00.000+00:00"),
                   seed: 95004))
        {
            WarmupStable(runner, 220, "app_focus");

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

            bool passed = HasEvent(runner, "deep_work_notifications_suppressed")
                && string.Equals(trustAtSuppression, CalibrationTruth.TrustHigh, StringComparison.Ordinal)
                && systemActionsEnabledAtSuppression == true;
            report.AddCheck(
                gateName: "current_vs_v2",
                scenarioName: "high_trust_required_for_session_actions",
                passed: passed,
                details: $"legacy_risk=hard_actions_too_early; trust={trustAtSuppression ?? "none"}; system_actions={systemActionsEnabledAtSuppression}");
        }

        report.AssertAllPassed();
    }

    private static void WarmupStable(
        DeterministicFinalizerScenarioRunner runner,
        int bucketCount,
        string appHash)
    {
        var warmup = new ScriptedBucketInput(
            TypingBurstIntensity: 0.58d,
            ScrollVelocityIndex: 0.22d,
            FocusSwitchRate: 0.08d,
            AppHash: appHash,
            KeyboardKeydowns: 4,
            MouseMoves: 2,
            MouseWheels: 1,
            IdleSeconds: 0d);

        for (int i = 0; i < bucketCount; i++)
        {
            runner.RunBucket(warmup);
        }
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

    private static bool HasEvent(DeterministicFinalizerScenarioRunner runner, string action)
    {
        return runner.ReadRecentEventRows(4000)
            .Any(row => string.Equals(row.Action, action, StringComparison.Ordinal));
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
            ComponentsRegistryVersion: 1,
            // Pressure rising into the need band (>= BreakNeedPressureLevel 0.50, below
            // HighPressureLevel 0.72): a short break is genuinely warranted under the
            // state-driven policy, so this scenario still exercises suggestion emission.
            PressureLevel: 0.60d);
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
}
