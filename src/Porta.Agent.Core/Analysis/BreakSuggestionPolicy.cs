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
        Func<BreakPolicyCooldownSnapshot> resolveCooldown,
        RuntimeBreakPolicySettings? policy = null)
    {
        RuntimeBreakPolicySettings settings = policy ?? new RuntimeBreakPolicySettings(
            PortaExecutionConstants.BreakPolicy.MicrobreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsLowOverload,
            PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsHighOverload,
            PortaExecutionConstants.BreakPolicy.ShortBreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowOverload,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsHighOverload,
            PortaExecutionConstants.BreakPolicy.CooldownSeconds,
            PortaExecutionConstants.BreakPolicy.ConfidenceMin,
            PortaExecutionConstants.BreakPolicy.HighOverloadLikelihood,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMinSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMaxSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryTypingQuietMax,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryKeyboardBurstQuietMax);
        if (!gating.SuggestionsEnabled)
        {
            return Fail("suggestions_not_enabled");
        }

        if (Clamp01(overloadInference.Confidence) < settings.ConfidenceMin)
        {
            return Fail("confidence_below_min");
        }

        string trustTier = CalibrationTruth.ResolveTrustTier(
            gating.CalibrationStage,
            overloadInference.Confidence,
            overloadInference.Quality,
            derivedSession.Partial);
        if (!CalibrationTruth.AllowsSuggestionsForTrust(trustTier))
        {
            return Fail("trust_below_suggestion_min");
        }

        bool continuityNotActive = Math.Max(0d, derivedSession.TaskContinuitySeconds) <= 0d
            && (derivedSession.SaIdlePauseSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds
                || string.Equals(derivedSession.SaFreezeReason, "long_break", StringComparison.Ordinal)
                || string.Equals(derivedSession.ContinuityState, "reset", StringComparison.Ordinal));
        if (continuityNotActive)
        {
            return Fail("continuity_not_active");
        }

        bool highOverloadPressure = focusWindow.HighOverloadPressure
            || overloadStabilization.OverloadActive
            || overloadInference.OverloadLikelihood >= settings.HighOverloadLikelihood;
        int microbreakThresholdSeconds = highOverloadPressure
            ? settings.MicrobreakEffectiveFocusSecondsHighOverload
            : settings.MicrobreakEffectiveFocusSecondsLowOverload;
        int shortBreakThresholdSeconds = highOverloadPressure
            ? settings.ShortBreakEffectiveFocusSecondsHighOverload
            : settings.ShortBreakEffectiveFocusSecondsLowOverload;

        string action;
        int windowSeconds;
        double effectiveFocusSeconds;
        // Step 2 migration map:
        // break_suggestion and stretch_break_suggestion are legacy transport IDs.
        // Product-facing naming is moving to long break and short break.
        if (Math.Max(0d, focusWindow.ShortBreakWindowEffectiveFocusSeconds) >= shortBreakThresholdSeconds)
        {
            action = "break_suggestion";
            windowSeconds = settings.ShortBreakWindowSeconds;
            effectiveFocusSeconds = Math.Max(0d, focusWindow.ShortBreakWindowEffectiveFocusSeconds);
        }
        else if (Math.Max(0d, focusWindow.MicrobreakWindowEffectiveFocusSeconds) >= microbreakThresholdSeconds)
        {
            action = "stretch_break_suggestion";
            windowSeconds = settings.MicrobreakWindowSeconds;
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
