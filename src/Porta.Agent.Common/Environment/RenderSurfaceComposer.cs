using System;
using Porta.Agent.Common.Control;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Environment;

public static class RenderSurfaceComposer
{
    private static readonly ColorRgb CoolTop = new(142, 181, 245);
    private static readonly ColorRgb CoolBottom = new(106, 147, 224);
    private static readonly ColorRgb NeutralTop = new(186, 196, 214);
    private static readonly ColorRgb NeutralBottom = new(136, 148, 172);
    private static readonly ColorRgb WarmTop = new(245, 197, 148);
    private static readonly ColorRgb WarmBottom = new(229, 160, 110);
    private static readonly ColorRgb NeutralHighlight = new(235, 236, 240);
    private static readonly ColorRgb NeutralShadow = new(84, 92, 106);
    private static readonly ColorRgb WarmHighlight = new(248, 226, 198);
    private static readonly ColorRgb WarmShadow = new(116, 88, 72);
    private static readonly ColorRgb CoolHighlight = new(214, 230, 250);
    private static readonly ColorRgb CoolShadow = new(70, 84, 114);

    public static RenderSurfaceFrame Compose(RenderParameters parameters, long renderTimestamp)
    {
        double normalizedTemp = Math.Clamp(parameters.KelvinShift / 1200.0, -1.0, 1.0);
        double brightness = Math.Clamp(parameters.BrightnessRange, 0.85, 1.15);
        double kelvinShift = MathEx.Quantize(parameters.KelvinShift, 6);
        double saturationScale = MathEx.Quantize(1.0 + (0.08 * Math.Abs(normalizedTemp)), 6);

        ColorRgb top = InterpolateTemperature(normalizedTemp, top: true).Scale(brightness);
        ColorRgb bottom = InterpolateTemperature(normalizedTemp, top: false).Scale(brightness * 0.95);
        ColorRgb highlightTint = InterpolateTint(normalizedTemp, highlight: true);
        ColorRgb shadowTint = InterpolateTint(normalizedTemp, highlight: false);

        int particleDensity = (int)Math.Round(Math.Clamp(parameters.ParticleDensity, 8.0, 180.0), MidpointRounding.AwayFromZero);
        double motionVelocity = MathEx.Quantize(Math.Clamp(parameters.AnimationVelocity, 0.05, 1.10), 6);
        double noiseAmplitude = MathEx.Quantize(Math.Clamp(parameters.MotionAmplitude, 0.005, 0.12), 6);
        double loopCadenceHz = MathEx.Quantize(Math.Clamp(parameters.LoopCadenceHz, 0.10, 1.30), 6);

        double parallaxAmount = MathEx.Quantize(Math.Clamp(parameters.ParallaxAmount, 0.001, 0.018), 6);
        double vignetteStrength = MathEx.Quantize(Math.Clamp(parameters.MicroVignetteStrength, 0.04, 0.28), 6);
        double clarity = MathEx.Quantize(Math.Clamp(parameters.Sharpness, 0.85, 1.25), 6);
        double easingAggressiveness = MathEx.Quantize(Math.Clamp(parameters.EasingAggressiveness, 0.20, 0.90), 6);
        double transitionMs = MathEx.Quantize(Math.Clamp(parameters.TransitionMs, 180.0, 700.0), 6);

        (string badge, bool reducedAnimation) = parameters.AuthorityLevel switch
        {
            AuthorityLevel.Normal => ("NORMAL", false),
            AuthorityLevel.Reduced => ("REDUCED", true),
            _ => ("SAFE", true)
        };

        return new RenderSurfaceFrame(
            RenderTimestamp: renderTimestamp,
            Background: new BackgroundGradientLayer(
                TopColor: top,
                BottomColor: bottom,
                BrightnessFactor: MathEx.Quantize(brightness, 6),
                HighlightTint: highlightTint,
                ShadowTint: shadowTint,
                SaturationScale: saturationScale,
                KelvinShift: kelvinShift),
            Motion: new MotionFieldLayer(
                ParticleDensity: particleDensity,
                MotionVelocity: motionVelocity,
                NoiseAmplitude: noiseAmplitude,
                LoopCadenceHz: loopCadenceHz),
            Depth: new DepthLayer(
                ParallaxAmount: parallaxAmount,
                VignetteStrength: vignetteStrength,
                Clarity: clarity),
            Overlay: new OverlayStateLayer(
                BadgeText: badge,
                AuthorityLevel: parameters.AuthorityLevel,
                ReducedAnimation: reducedAnimation,
                EasingAggressiveness: easingAggressiveness,
                TransitionMs: transitionMs));
    }

    private static ColorRgb InterpolateTemperature(double normalizedTemp, bool top)
    {
        if (normalizedTemp >= 0.0)
        {
            return ColorRgb.Lerp(
                top ? NeutralTop : NeutralBottom,
                top ? WarmTop : WarmBottom,
                normalizedTemp);
        }

        return ColorRgb.Lerp(
            top ? NeutralTop : NeutralBottom,
            top ? CoolTop : CoolBottom,
            -normalizedTemp);
    }
    
    private static ColorRgb InterpolateTint(double normalizedTemp, bool highlight)
    {
        if (normalizedTemp >= 0.0)
        {
            return ColorRgb.Lerp(
                highlight ? NeutralHighlight : NeutralShadow,
                highlight ? WarmHighlight : WarmShadow,
                normalizedTemp);
        }

        return ColorRgb.Lerp(
            highlight ? NeutralHighlight : NeutralShadow,
            highlight ? CoolHighlight : CoolShadow,
            -normalizedTemp);
    }
}
