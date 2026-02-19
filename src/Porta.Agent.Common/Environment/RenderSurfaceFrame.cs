using Porta.Agent.Common.Control;

namespace Porta.Agent.Common.Environment;

public readonly record struct BackgroundGradientLayer(
    ColorRgb TopColor,
    ColorRgb BottomColor,
    double BrightnessFactor,
    ColorRgb HighlightTint,
    ColorRgb ShadowTint,
    double SaturationScale,
    double KelvinShift);

public readonly record struct MotionFieldLayer(
    int ParticleDensity,
    double MotionVelocity,
    double NoiseAmplitude,
    double LoopCadenceHz);

public readonly record struct DepthLayer(
    double ParallaxAmount,
    double VignetteStrength,
    double Clarity);

public readonly record struct OverlayStateLayer(
    string BadgeText,
    AuthorityLevel AuthorityLevel,
    bool ReducedAnimation,
    double EasingAggressiveness,
    double TransitionMs);

public readonly record struct RenderSurfaceFrame(
    long RenderTimestamp,
    BackgroundGradientLayer Background,
    MotionFieldLayer Motion,
    DepthLayer Depth,
    OverlayStateLayer Overlay);
