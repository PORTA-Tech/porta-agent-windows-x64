using Porta.Agent.Common;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct BreakPolicyCooldownSnapshot(
    bool CooldownExpired,
    long CooldownEndTsMs,
    long CooldownRemainingMs);

internal readonly record struct BreakFocusWindowSnapshot(
    double CurrentBucketEffectiveFocusSeconds,
    double ShortBreakWindowEffectiveFocusSeconds,
    double LongBreakWindowEffectiveFocusSeconds,
    bool DeliveryBoundaryReached,
    string DeliveryBoundaryKind,
    bool HighPressure,
    bool FlowStateActive,
    long BreakDueSinceUtcMs);

internal readonly record struct BreakSuggestionDecision(
    bool ShouldEmit,
    string Action,
    string BreakKind,
    string ReasonCode,
    bool CooldownChecked,
    BreakPolicyCooldownSnapshot Cooldown,
    double EffectiveFocusSeconds,
    int EffectiveFocusWindowSeconds,
    string DeliveryBoundaryKind,
    bool HighPressure);

internal static class BreakSuggestionPolicy
{
    internal static BreakSuggestionDecision ShouldTriggerBreak(
        BaselineGatingResult gating,
        OverloadStabilizationResult overloadStabilization,
        OverloadInferenceResult overloadInference,
        DerivedSessionMetricsResult derivedSession,
        BreakFocusWindowSnapshot focusWindow,
        Func<BreakPolicyCooldownSnapshot> resolveCooldown,
        RuntimeBreakPolicySettings? policy = null,
        bool meetingInProgress = false)
    {
        // Meeting shield (lite): never interrupt with a break nudge while a real-time meeting
        // app (Zoom/Teams/Webex/Google Meet) is foreground. Interrupting a call reads as the
        // product getting in the way. Coarse by design — no audio/mic signal is captured.
        if (meetingInProgress)
        {
            return Fail("meeting_in_progress");
        }

        RuntimeBreakPolicySettings settings = policy ?? new RuntimeBreakPolicySettings(
            PortaExecutionConstants.BreakPolicy.ShortBreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowPressure,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsHighPressure,
            PortaExecutionConstants.BreakPolicy.LongBreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.LongBreakEffectiveFocusSecondsLowPressure,
            PortaExecutionConstants.BreakPolicy.LongBreakEffectiveFocusSecondsHighPressure,
            PortaExecutionConstants.BreakPolicy.CooldownSeconds,
            PortaExecutionConstants.BreakPolicy.ConfidenceMin,
            PortaExecutionConstants.BreakPolicy.HighPressureLevel,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMinSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMaxSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryTypingQuietMax,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryKeyboardBurstQuietMax,
            PortaExecutionConstants.BreakPolicy.FlowStateWindowSeconds,
            PortaExecutionConstants.BreakPolicy.HardTimeoutSeconds);
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

        bool highPressure = focusWindow.HighPressure
            || overloadStabilization.OverloadActive
            || overloadInference.PressureLevel >= settings.HighPressureLevel;
        int shortBreakThresholdSeconds = highPressure
            ? settings.ShortBreakEffectiveFocusSecondsHighPressure
            : settings.ShortBreakEffectiveFocusSecondsLowPressure;
        int longBreakThresholdSeconds = highPressure
            ? settings.LongBreakEffectiveFocusSecondsHighPressure
            : settings.LongBreakEffectiveFocusSecondsLowPressure;

        // State-driven gate (Launch 2.3 — "follow the state, don't nag the clock").
        // A SHORT break is only warranted when the state shows a genuine, elevated need:
        // overload is active, or pressure / overload likelihood has risen into the need band.
        // A fresh, energized user who has simply been focused a while is NOT interrupted —
        // ignored nudges only train the user to dismiss every nudge. A LONG break stays
        // justified by genuinely prolonged focus on its own (the "you've been at it a long
        // time, that's normal" case), so deep, sustained work still eventually earns a rest.
        bool breakNeedState = overloadStabilization.OverloadActive
            || overloadInference.PressureLevel >= PortaExecutionConstants.BreakPolicy.BreakNeedPressureLevel
            || overloadInference.OverloadLikelihood >= PortaExecutionConstants.BreakPolicy.BreakNeedOverloadLikelihood;

        string action;
        string breakKind;
        int windowSeconds;
        double effectiveFocusSeconds;
        // Canonical action IDs are now long_break and short_break.
        // stretch_break_suggestion and break_suggestion are legacy transport IDs kept
        // only for normalising historical events; new events never emit them.
        if (Math.Max(0d, focusWindow.LongBreakWindowEffectiveFocusSeconds) >= longBreakThresholdSeconds)
        {
            action = PortaBreakSemantics.LongBreakKind;
            breakKind = PortaBreakSemantics.LongBreakKind;
            windowSeconds = settings.LongBreakWindowSeconds;
            effectiveFocusSeconds = Math.Max(0d, focusWindow.LongBreakWindowEffectiveFocusSeconds);
        }
        else if (Math.Max(0d, focusWindow.ShortBreakWindowEffectiveFocusSeconds) >= shortBreakThresholdSeconds)
        {
            if (!breakNeedState)
            {
                // Focus duration alone is met, but the user shows no sign of needing a break
                // yet — leave them in flow. A long break will still be offered later if the
                // focus run becomes genuinely prolonged.
                return Fail(
                    "state_not_warranting_break",
                    effectiveFocusSeconds: Math.Max(0d, focusWindow.ShortBreakWindowEffectiveFocusSeconds),
                    effectiveFocusWindowSeconds: 0,
                    highPressure: highPressure);
            }

            action = PortaBreakSemantics.ShortBreakKind;
            breakKind = PortaBreakSemantics.ShortBreakKind;
            windowSeconds = settings.ShortBreakWindowSeconds;
            effectiveFocusSeconds = Math.Max(0d, focusWindow.ShortBreakWindowEffectiveFocusSeconds);
        }
        else
        {
            return Fail(
                "effective_focus_below_min",
                effectiveFocusSeconds: Math.Max(
                    focusWindow.ShortBreakWindowEffectiveFocusSeconds,
                    focusWindow.LongBreakWindowEffectiveFocusSeconds),
                effectiveFocusWindowSeconds: 0,
                highPressure: highPressure);
        }

        bool hardTimeoutExpired = focusWindow.BreakDueSinceUtcMs > 0
            && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - focusWindow.BreakDueSinceUtcMs)
                >= (long)(settings.HardTimeoutSeconds * 1000L);

        if (!focusWindow.DeliveryBoundaryReached && !hardTimeoutExpired)
        {
            return Fail(
                "awaiting_delivery_boundary",
                action: action,
                breakKind: breakKind,
                effectiveFocusSeconds: effectiveFocusSeconds,
                effectiveFocusWindowSeconds: windowSeconds,
                highPressure: highPressure);
        }

        string resolvedBoundaryKind = focusWindow.DeliveryBoundaryReached
            ? focusWindow.DeliveryBoundaryKind
            : "hard_timeout";

        BreakPolicyCooldownSnapshot cooldown = resolveCooldown();
        if (!cooldown.CooldownExpired)
        {
            return Fail(
                "cooldown_active",
                cooldownChecked: true,
                cooldown: cooldown,
                action: action,
                breakKind: breakKind,
                effectiveFocusSeconds: effectiveFocusSeconds,
                effectiveFocusWindowSeconds: windowSeconds,
                deliveryBoundaryKind: resolvedBoundaryKind,
                highPressure: highPressure);
        }

        return new BreakSuggestionDecision(
            ShouldEmit: true,
            Action: action,
            BreakKind: breakKind,
            ReasonCode: "preconditions_met",
            CooldownChecked: true,
            Cooldown: cooldown,
            EffectiveFocusSeconds: effectiveFocusSeconds,
            EffectiveFocusWindowSeconds: windowSeconds,
            DeliveryBoundaryKind: NormalizeBoundaryKind(resolvedBoundaryKind),
            HighPressure: highPressure);
    }

    private static BreakSuggestionDecision Fail(
        string reasonCode,
        bool cooldownChecked = false,
        BreakPolicyCooldownSnapshot cooldown = default,
        string action = "",
        string breakKind = "",
        double effectiveFocusSeconds = 0d,
        int effectiveFocusWindowSeconds = 0,
        string deliveryBoundaryKind = "",
        bool highPressure = false)
    {
        return new BreakSuggestionDecision(
            ShouldEmit: false,
            Action: action,
            BreakKind: breakKind,
            ReasonCode: reasonCode,
            CooldownChecked: cooldownChecked,
            Cooldown: cooldown,
            EffectiveFocusSeconds: Math.Max(0d, effectiveFocusSeconds),
            EffectiveFocusWindowSeconds: Math.Max(0, effectiveFocusWindowSeconds),
            DeliveryBoundaryKind: NormalizeBoundaryKind(deliveryBoundaryKind),
            HighPressure: highPressure);
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
