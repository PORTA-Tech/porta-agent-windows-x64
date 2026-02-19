using Porta.Agent.Common.Control;

namespace Porta.Agent.Common.Environment;

public readonly record struct RenderParameters(
    double BlurPx,
    double Sharpness,
    double BrightnessRange,
    double MicroVignetteStrength,
    double ParticleDensity,
    double AnimationVelocity,
    double ParallaxAmount,
    double MotionAmplitude,
    double MotionEnergy,
    double KelvinShift,
    double TransitionMs,
    double EasingAggressiveness,
    double LoopCadenceHz,
    AuthorityLevel AuthorityLevel);
