using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct BreakPolicyCooldownSnapshot(
    bool CooldownExpired,
    long CooldownEndTsMs,
    long CooldownRemainingMs);

internal readonly record struct BreakFocusWindowSnapshot(
    double CurrentBucketEffectiveFocusSeconds,
    double MicrobreakWindowEffectiveFocusSeconds,
    double ShortBreakWindowEffectiveFocusSeconds,
    bool DeliveryBoundaryReached,
    string DeliveryBoundaryKind,
    bool HighOverloadPressure);

internal readonly record struct BreakSuggestionDecision(
    bool ShouldEmit,
    string Action,
    string ReasonCode,
    bool CooldownChecked,
    BreakPolicyCooldownSnapshot Cooldown,
    double EffectiveFocusSeconds,
    int EffectiveFocusWindowSeconds,
    string DeliveryBoundaryKind,
    bool HighOverloadPressure);

internal static class BreakSuggestionPolicy
{
    internal static BreakSuggestionDecision ShouldTriggerBreak(
        BaselineGatingResult gating,
        OverloadStabilizationResult overloadStabilization,
        OverloadInferenceResult overloadInference,
        DerivedSessionMetricsResult derivedSession,
        BreakFocusWindowSnapshot focusWindow,
        Func<BreakPolicyCooldownSnapshot> resolveCooldown)
    {
        if (!gating.PoliciesEnabled)
        {
            return Fail("suggestions_not_enabled");
        }

        if (Clamp01(overloadInference.Confidence) < PortaExecutionConstants.BreakPolicy.ConfidenceMin)
        {
            return Fail("confidence_below_min");
        }

        bool highOverloadPressure = focusWindow.HighOverloadPressure
            || overloadStabilization.OverloadActive
            || overloadInference.OverloadLikelihood >= PortaExecutionConstants.BreakPolicy.HighOverloadLikelihood;
        int microbreakThresholdSeconds = highOverloadPressure
            ? PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsHighOverload
            : PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsLowOverload;
        int shortBreakThresholdSeconds = highOverloadPressure
            ? PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsHighOverload
            : PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowOverload;

        string action;
        int windowSeconds;
        double effectiveFocusSeconds;
        if (Math.Max(0d, focusWindow.ShortBreakWindowEffectiveFocusSeconds) >= shortBreakThresholdSeconds)
        {
            action = "break_suggestion";
            windowSeconds = PortaExecutionConstants.BreakPolicy.ShortBreakWindowSeconds;
            effectiveFocusSeconds = Math.Max(0d, focusWindow.ShortBreakWindowEffectiveFocusSeconds);
        }
        else if (Math.Max(0d, focusWindow.MicrobreakWindowEffectiveFocusSeconds) >= microbreakThresholdSeconds)
        {
            action = "stretch_break_suggestion";
            windowSeconds = PortaExecutionConstants.BreakPolicy.MicrobreakWindowSeconds;
            effectiveFocusSeconds = Math.Max(0d, focusWindow.MicrobreakWindowEffectiveFocusSeconds);
        }
        else
        {
            return Fail(
                "effective_focus_below_min",
                effectiveFocusSeconds: Math.Max(
                    focusWindow.MicrobreakWindowEffectiveFocusSeconds,
                    focusWindow.ShortBreakWindowEffectiveFocusSeconds),
                effectiveFocusWindowSeconds: 0,
                highOverloadPressure: highOverloadPressure);
        }

        if (!focusWindow.DeliveryBoundaryReached)
        {
            return Fail(
                "awaiting_delivery_boundary",
                action: action,
                effectiveFocusSeconds: effectiveFocusSeconds,
                effectiveFocusWindowSeconds: windowSeconds,
                highOverloadPressure: highOverloadPressure);
        }

        BreakPolicyCooldownSnapshot cooldown = resolveCooldown();
        if (!cooldown.CooldownExpired)
        {
            return Fail(
                "cooldown_active",
                cooldownChecked: true,
                cooldown: cooldown,
                action: action,
                effectiveFocusSeconds: effectiveFocusSeconds,
                effectiveFocusWindowSeconds: windowSeconds,
                deliveryBoundaryKind: focusWindow.DeliveryBoundaryKind,
                highOverloadPressure: highOverloadPressure);
        }

        return new BreakSuggestionDecision(
            ShouldEmit: true,
            Action: action,
            ReasonCode: "preconditions_met",
            CooldownChecked: true,
            Cooldown: cooldown,
            EffectiveFocusSeconds: effectiveFocusSeconds,
            EffectiveFocusWindowSeconds: windowSeconds,
            DeliveryBoundaryKind: NormalizeBoundaryKind(focusWindow.DeliveryBoundaryKind),
            HighOverloadPressure: highOverloadPressure);
    }

    private static BreakSuggestionDecision Fail(
        string reasonCode,
        bool cooldownChecked = false,
        BreakPolicyCooldownSnapshot cooldown = default,
        string action = "",
        double effectiveFocusSeconds = 0d,
        int effectiveFocusWindowSeconds = 0,
        string deliveryBoundaryKind = "",
        bool highOverloadPressure = false)
    {
        return new BreakSuggestionDecision(
            ShouldEmit: false,
            Action: action,
            ReasonCode: reasonCode,
            CooldownChecked: cooldownChecked,
            Cooldown: cooldown,
            EffectiveFocusSeconds: Math.Max(0d, effectiveFocusSeconds),
            EffectiveFocusWindowSeconds: Math.Max(0, effectiveFocusWindowSeconds),
            DeliveryBoundaryKind: NormalizeBoundaryKind(deliveryBoundaryKind),
            HighOverloadPressure: highOverloadPressure);
    }

    private static string NormalizeBoundaryKind(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
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
