using System;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.State;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Control;

public sealed class SafetyArbitrator
{
    // Explicit multi-axis conflict policy (stable behavioral contract):
    // 1) Safe-mode override (confidence gate) has absolute precedence.
    // 2) Authority reduction is resolved before any per-axis clamping.
    // 3) Primary axis order: stimulation_intensity -> scene_dynamics.
    // 4) Secondary axis order: visual_temperature_shift -> pacing_modifier.
    // 5) Cross-axis envelope: secondary axes cannot exceed safe intensity envelope.
    private const int PrioritySafeModeOverride = 0;
    private const int PriorityAuthorityResolution = 1;
    private const int PriorityPrimaryAxes = 2;
    private const int PrioritySecondaryAxes = 3;

    public readonly struct ArbitrationPolicyDescriptor
    {
        public int SafeModeOverridePriority => PrioritySafeModeOverride;
        public int AuthorityResolutionPriority => PriorityAuthorityResolution;
        public int PrimaryAxesPriority => PriorityPrimaryAxes;
        public int SecondaryAxesPriority => PrioritySecondaryAxes;
        public string PrimaryAxisOrder => "stimulation_intensity>scene_dynamics";
        public string SecondaryAxisOrder => "visual_temperature_shift>pacing_modifier";
        public string SafeModePrecedence => "confidence_breach_overrides_all_axes";
    }

    public static ArbitrationPolicyDescriptor Policy => new();

    public ActuationCommand Arbitrate(
        ControlFeatures features,
        ActuationCommand proposed,
        ControlState state,
        ControlParameters parameters)
    {
        double confidenceThreshold = MathEx.Clamp01(parameters.ConfidenceThreshold);
        double maxDeltaPerTick = Math.Clamp(parameters.MaxDeltaPerTick, 0.0, 1.0);

        if (features.SignalConfidence < confidenceThreshold)
        {
            // Priority 0: safe-mode override takes absolute precedence.
            state.LastActuationIntensity = 0;
            state.LastSafeSceneDynamics = 0;
            state.LastSafeVisualShift = 0;
            state.LastSafePacingModifier = 0;
            return ActuationCommand.Safe("low_confidence_safe_mode");
        }

        // Priority 1: resolve authority before axis arbitration.
        AuthorityLevel resolvedAuthority = proposed.AuthorityLevel;
        double authorityScale = resolvedAuthority switch
        {
            AuthorityLevel.Normal => 1.0,
            AuthorityLevel.Reduced => 0.5,
            _ => 0.0
        };

        // Priority 2: primary-axis arbitration.
        // 2a) Clamp intensity with authority and per-tick rate-limit.
        double clampedIntensity = MathEx.Quantize(MathEx.Clamp01(proposed.StimulationIntensity) * authorityScale);
        double clampedDynamics = MathEx.Quantize(MathEx.Clamp01(proposed.SceneDynamics) * authorityScale);
        double lastIntensity = MathEx.Clamp01(state.LastActuationIntensity);
        double lastDynamics = MathEx.Clamp01(state.LastSafeSceneDynamics);
        double delta = MathEx.Quantize(Math.Clamp(clampedIntensity - lastIntensity, -maxDeltaPerTick, maxDeltaPerTick));
        double rateLimitedIntensity = MathEx.Quantize(MathEx.Clamp01(lastIntensity + delta));
        // 2b) Scene dynamics is clamped after intensity, constrained by the new safe intensity.
        double maxDynamicsForIntensity = MathEx.Quantize(MathEx.Clamp01(0.35 + (0.65 * rateLimitedIntensity)));
        double desiredDynamics = MathEx.Quantize(Math.Min(clampedDynamics, maxDynamicsForIntensity));
        double dynamicsDelta = MathEx.Quantize(Math.Clamp(desiredDynamics - lastDynamics, -maxDeltaPerTick, maxDeltaPerTick));
        double rateLimitedDynamics = MathEx.Quantize(MathEx.Clamp01(lastDynamics + dynamicsDelta));

        // Priority 3: secondary-axis arbitration.
        // Cross-axis clamp order is fixed and deterministic:
        // visual_temperature_shift first, pacing_modifier second.
        // Both must remain inside the safe intensity envelope.
        double maxShift = MathEx.Quantize(Math.Max(rateLimitedIntensity, rateLimitedDynamics));
        double lastVisual = Math.Clamp(state.LastSafeVisualShift, -1.0, 1.0);
        double lastPacing = Math.Clamp(state.LastSafePacingModifier, -1.0, 1.0);
        double desiredVisual = MathEx.Quantize(Math.Clamp(proposed.VisualTemperatureShift, -maxShift, maxShift) * authorityScale);
        double desiredPacing = MathEx.Quantize(Math.Clamp(proposed.PacingModifier, -maxShift, maxShift) * authorityScale);
        double visual = MathEx.Quantize(Math.Clamp(desiredVisual, lastVisual - maxDeltaPerTick, lastVisual + maxDeltaPerTick));
        double pacing = MathEx.Quantize(Math.Clamp(desiredPacing, lastPacing - maxDeltaPerTick, lastPacing + maxDeltaPerTick));

        state.LastActuationIntensity = rateLimitedIntensity;
        state.LastSafeSceneDynamics = rateLimitedDynamics;
        state.LastSafeVisualShift = visual;
        state.LastSafePacingModifier = pacing;

        return new ActuationCommand
        {
            StimulationIntensity = rateLimitedIntensity,
            SceneDynamics = rateLimitedDynamics,
            VisualTemperatureShift = MathEx.Quantize(Math.Clamp(visual, -1.0, 1.0)),
            PacingModifier = MathEx.Quantize(Math.Clamp(pacing, -1.0, 1.0)),
            AuthorityLevel = resolvedAuthority,
            Note = $"safety_arbitrated:{proposed.Note}"
        };
    }
}
