using System;
using Porta.Agent.Common.Control;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Environment;

public static class DeterministicRenderMapper
{
    public static RenderParameters Map(ActuationCommand command)
    {
        return Map(ActuationState.FromCommand(command));
    }

    public static RenderParameters Map(ActuationState state)
    {
        AuthorityGatedState gated = AuthorityGate.Apply(state);
        ActuationState effective = gated.State;

        if (gated.UseSafeProfile)
        {
            return new RenderParameters(
                BlurPx: 1.5,
                Sharpness: 1.0,
                BrightnessRange: 1.0,
                MicroVignetteStrength: 0.08,
                ParticleDensity: 16.0,
                AnimationVelocity: 0.12,
                ParallaxAmount: 0.003,
                MotionAmplitude: 0.015,
                MotionEnergy: 0.1,
                KelvinShift: 0.0,
                TransitionMs: 400.0,
                EasingAggressiveness: 0.25,
                LoopCadenceHz: 0.25,
                AuthorityLevel: AuthorityLevel.Safe);
        }

        double stimulation = effective.StimulationIntensity;
        double sceneDynamics = effective.SceneDynamics;
        double visualShift = effective.VisualTemperatureShift;
        double pacing = effective.PacingModifier;
        double normalizedPacing = (pacing + 1.0) * 0.5;

        double blurPx = Lerp(0.0, 6.0, 1.0 - stimulation);
        double sharpness = Lerp(0.85, 1.25, stimulation);
        double brightnessRange = Lerp(0.92, 1.08, stimulation);
        double microVignetteStrength = Lerp(0.06, 0.22, stimulation);
        // Comfort-capped dynamics mapping.
        double particleDensity = Lerp(12.0, 180.0, sceneDynamics);
        double animationVelocity = Lerp(0.08, 1.10, sceneDynamics);
        double parallaxAmount = Lerp(0.002, 0.018, sceneDynamics);
        double motionAmplitude = Lerp(0.01, 0.12, sceneDynamics);
        double motionEnergy = Lerp(0.15, 0.75, sceneDynamics);
        double kelvinShift = visualShift * 1200.0;
        double transitionMs = Lerp(700.0, 180.0, normalizedPacing);
        double easingAggressiveness = Lerp(0.25, 0.90, normalizedPacing);
        double loopCadenceHz = Lerp(0.18, 1.30, normalizedPacing);

        if (effective.AuthorityLevel == AuthorityLevel.Reduced)
        {
            particleDensity = Math.Min(particleDensity, 120.0);
            animationVelocity = Math.Min(animationVelocity, 0.75);
            parallaxAmount = Math.Min(parallaxAmount, 0.012);
            motionAmplitude = Math.Min(motionAmplitude, 0.08);
            motionEnergy = Math.Min(motionEnergy, 0.5);
            transitionMs = Math.Max(transitionMs, 260.0);
            easingAggressiveness = Math.Min(easingAggressiveness, 0.65);
            loopCadenceHz = Math.Min(loopCadenceHz, 0.85);
            brightnessRange = Math.Clamp(brightnessRange, 0.96, 1.04);
            microVignetteStrength = Math.Min(microVignetteStrength, 0.16);
        }

        return new RenderParameters(
            BlurPx: MathEx.Quantize(blurPx, 6),
            Sharpness: MathEx.Quantize(sharpness, 6),
            BrightnessRange: MathEx.Quantize(brightnessRange, 6),
            MicroVignetteStrength: MathEx.Quantize(microVignetteStrength, 6),
            ParticleDensity: MathEx.Quantize(particleDensity, 6),
            AnimationVelocity: MathEx.Quantize(animationVelocity, 6),
            ParallaxAmount: MathEx.Quantize(parallaxAmount, 6),
            MotionAmplitude: MathEx.Quantize(motionAmplitude, 6),
            MotionEnergy: MathEx.Quantize(motionEnergy, 6),
            KelvinShift: MathEx.Quantize(kelvinShift, 6),
            TransitionMs: MathEx.Quantize(transitionMs, 6),
            EasingAggressiveness: MathEx.Quantize(easingAggressiveness, 6),
            LoopCadenceHz: MathEx.Quantize(loopCadenceHz, 6),
            AuthorityLevel: effective.AuthorityLevel);
    }

    private static double Lerp(double a, double b, double t)
    {
        return a + ((b - a) * MathEx.Clamp01(t));
    }
}
