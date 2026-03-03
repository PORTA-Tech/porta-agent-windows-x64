using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct BreakPolicyCooldownSnapshot(
    bool CooldownExpired,
    long CooldownEndTsMs,
    long CooldownRemainingMs);

internal readonly record struct BreakSuggestionDecision(
    bool ShouldEmit,
    string ReasonCode,
    bool CooldownChecked,
    BreakPolicyCooldownSnapshot Cooldown);

internal static class BreakSuggestionPolicy
{
    internal static BreakSuggestionDecision ShouldTriggerBreak(
        BaselineGatingResult gating,
        OverloadStabilizationResult overloadStabilization,
        OverloadInferenceResult overloadInference,
        DerivedSessionMetricsResult derivedSession,
        Func<BreakPolicyCooldownSnapshot> resolveCooldown)
    {
        // Step 13B preconditions in pinned order:
        // 1) baseline_ready
        // 2) overload_active
        // 3) confidence >= 0.60
        // 4) sustained_attention >= 3600s
        // 5) cooldown_expired
        // Cooldown lookup is deferred so failing early preconditions do not cause extra DB reads.
        if (!gating.BaselineReady)
        {
            return Fail("baseline_not_ready");
        }

        if (!overloadStabilization.OverloadActive)
        {
            return Fail("overload_inactive");
        }

        if (Clamp01(overloadInference.Confidence) < PortaExecutionConstants.BreakPolicy.ConfidenceMin)
        {
            return Fail("confidence_below_min");
        }

        if (Math.Max(0d, derivedSession.SustainedAttentionDurationSec) < PortaExecutionConstants.BreakPolicy.SustainedAttentionSeconds)
        {
            return Fail("sustained_attention_below_min");
        }

        BreakPolicyCooldownSnapshot cooldown = resolveCooldown();
        if (!cooldown.CooldownExpired)
        {
            return Fail("cooldown_active", cooldownChecked: true, cooldown);
        }

        return new BreakSuggestionDecision(
            ShouldEmit: true,
            ReasonCode: "preconditions_met",
            CooldownChecked: true,
            Cooldown: cooldown);
    }

    private static BreakSuggestionDecision Fail(
        string reasonCode,
        bool cooldownChecked = false,
        BreakPolicyCooldownSnapshot cooldown = default)
    {
        return new BreakSuggestionDecision(
            ShouldEmit: false,
            ReasonCode: reasonCode,
            CooldownChecked: cooldownChecked,
            Cooldown: cooldown);
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }
}
