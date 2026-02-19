using System;
using Porta.Agent.Common.Control;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Environment;

public sealed class VisualInterpolator
{
    private readonly object _sync = new();
    private readonly double _lerpAlpha;
    private RenderParameters _current;
    private RenderParameters _target;
    private bool _initialized;
    private long _lastRenderTimestamp;

    public VisualInterpolator(double lerpAlpha = 0.12)
    {
        _lerpAlpha = Math.Clamp(lerpAlpha, 0.08, 0.15);
        _current = SafeProfile;
        _target = SafeProfile;
    }

    public void SetTarget(RenderParameters target)
    {
        lock (_sync)
        {
            _target = Quantize(target);
        }
    }

    public RenderParameters AdvanceFrame()
    {
        lock (_sync)
        {
            if (!_initialized)
            {
                _current = _target;
                _initialized = true;
                return _current;
            }

            _current = Interpolate(_current, _target, _lerpAlpha);
            return _current;
        }
    }

    public RenderParameters AdvanceToTimestamp(long renderTimestamp, long frequency)
    {
        if (frequency <= 0)
        {
            return AdvanceFrame();
        }

        lock (_sync)
        {
            if (!_initialized)
            {
                _current = _target;
                _initialized = true;
                _lastRenderTimestamp = renderTimestamp;
                return _current;
            }

            if (renderTimestamp <= _lastRenderTimestamp)
            {
                return _current;
            }

            double elapsedSeconds = (renderTimestamp - _lastRenderTimestamp) / (double)frequency;
            int frameCount = Math.Max(1, (int)Math.Floor(elapsedSeconds * 60.0));
            double effectiveAlpha = 1.0 - Math.Pow(1.0 - _lerpAlpha, frameCount);
            _current = Interpolate(_current, _target, effectiveAlpha);
            _lastRenderTimestamp = renderTimestamp;
            return _current;
        }
    }

    public (RenderParameters Current, RenderParameters Target, bool Initialized) Snapshot()
    {
        lock (_sync)
        {
            return (_current, _target, _initialized);
        }
    }

    private static RenderParameters Interpolate(in RenderParameters current, in RenderParameters target, double alpha)
    {
        double a = MathEx.Clamp01(alpha);
        return Quantize(new RenderParameters(
            BlurPx: current.BlurPx + ((target.BlurPx - current.BlurPx) * a),
            Sharpness: current.Sharpness + ((target.Sharpness - current.Sharpness) * a),
            BrightnessRange: current.BrightnessRange + ((target.BrightnessRange - current.BrightnessRange) * a),
            MicroVignetteStrength: current.MicroVignetteStrength + ((target.MicroVignetteStrength - current.MicroVignetteStrength) * a),
            ParticleDensity: current.ParticleDensity + ((target.ParticleDensity - current.ParticleDensity) * a),
            AnimationVelocity: current.AnimationVelocity + ((target.AnimationVelocity - current.AnimationVelocity) * a),
            ParallaxAmount: current.ParallaxAmount + ((target.ParallaxAmount - current.ParallaxAmount) * a),
            MotionAmplitude: current.MotionAmplitude + ((target.MotionAmplitude - current.MotionAmplitude) * a),
            MotionEnergy: current.MotionEnergy + ((target.MotionEnergy - current.MotionEnergy) * a),
            KelvinShift: current.KelvinShift + ((target.KelvinShift - current.KelvinShift) * a),
            TransitionMs: current.TransitionMs + ((target.TransitionMs - current.TransitionMs) * a),
            EasingAggressiveness: current.EasingAggressiveness + ((target.EasingAggressiveness - current.EasingAggressiveness) * a),
            LoopCadenceHz: current.LoopCadenceHz + ((target.LoopCadenceHz - current.LoopCadenceHz) * a),
            AuthorityLevel: target.AuthorityLevel));
    }

    private static RenderParameters Quantize(in RenderParameters value)
    {
        return new RenderParameters(
            BlurPx: MathEx.Quantize(value.BlurPx, 6),
            Sharpness: MathEx.Quantize(value.Sharpness, 6),
            BrightnessRange: MathEx.Quantize(value.BrightnessRange, 6),
            MicroVignetteStrength: MathEx.Quantize(value.MicroVignetteStrength, 6),
            ParticleDensity: MathEx.Quantize(value.ParticleDensity, 6),
            AnimationVelocity: MathEx.Quantize(value.AnimationVelocity, 6),
            ParallaxAmount: MathEx.Quantize(value.ParallaxAmount, 6),
            MotionAmplitude: MathEx.Quantize(value.MotionAmplitude, 6),
            MotionEnergy: MathEx.Quantize(value.MotionEnergy, 6),
            KelvinShift: MathEx.Quantize(value.KelvinShift, 6),
            TransitionMs: MathEx.Quantize(value.TransitionMs, 6),
            EasingAggressiveness: MathEx.Quantize(value.EasingAggressiveness, 6),
            LoopCadenceHz: MathEx.Quantize(value.LoopCadenceHz, 6),
            AuthorityLevel: value.AuthorityLevel);
    }

    private static readonly RenderParameters SafeProfile = new(
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
