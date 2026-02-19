using System;
using System.Collections.Generic;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.Control;

namespace Porta.Agent.Common.Environment;

public static class EnvironmentValidation
{
    public sealed class ValidationResult
    {
        public bool IsPass { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    public static IReadOnlyList<ValidationResult> RunSuite(long frequency)
    {
        return new List<ValidationResult>
        {
            ValidateHighArousalConvergesSmoothly(),
            ValidateOscillatingSignalRemainsBounded(),
            ValidateSignalLossSafeModeVisible(),
            ValidateReplaySurfaceDeterminism(frequency),
            ValidateAuthorityTransitionsVisible()
        };
    }

    private static ValidationResult ValidateHighArousalConvergesSmoothly()
    {
        var interpolator = new VisualInterpolator();
        var high = DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(1.0, AuthorityLevel.Normal, "test_high"));
        interpolator.SetTarget(high);

        double previousDistance = double.MaxValue;
        int violations = 0;
        for (int i = 0; i < 120; i++)
        {
            RenderParameters current = interpolator.AdvanceFrame();
            double distance = Math.Abs(high.Sharpness - current.Sharpness);
            if (distance > previousDistance + 1e-9)
            {
                violations++;
            }

            previousDistance = distance;
        }

        bool pass = violations == 0 && previousDistance <= 0.001;
        return new ValidationResult
        {
            IsPass = pass,
            Name = "surface_convergence_smooth",
            Detail = $"violations={violations};final_distance={previousDistance:F6}"
        };
    }

    private static ValidationResult ValidateOscillatingSignalRemainsBounded()
    {
        var interpolator = new VisualInterpolator();
        double maxFrameDelta = 0.0;
        double previous = 0.0;
        bool hasPrevious = false;

        for (int i = 0; i < 300; i++)
        {
            var cmd = ActuationCommand.FromControlSignal(
                normalizedControlSignal: (i % 2 == 0) ? 1.0 : -1.0,
                authorityLevel: AuthorityLevel.Normal,
                note: "test_oscillating");
            interpolator.SetTarget(DeterministicRenderMapper.Map(cmd));
            RenderParameters frame = interpolator.AdvanceFrame();
            double current = frame.MotionAmplitude;
            if (hasPrevious)
            {
                maxFrameDelta = Math.Max(maxFrameDelta, Math.Abs(current - previous));
            }

            previous = current;
            hasPrevious = true;
        }

        bool pass = maxFrameDelta <= 0.05;
        return new ValidationResult
        {
            IsPass = pass,
            Name = "oscillation_bounded_no_pulse_spike",
            Detail = $"max_frame_delta={maxFrameDelta:F6}"
        };
    }

    private static ValidationResult ValidateSignalLossSafeModeVisible()
    {
        RenderParameters safe = DeterministicRenderMapper.Map(ActuationCommand.Safe("signal_loss"));
        RenderSurfaceFrame frame = RenderSurfaceComposer.Compose(safe, renderTimestamp: 1);

        bool pass =
            frame.Overlay.AuthorityLevel == AuthorityLevel.Safe &&
            frame.Overlay.BadgeText == "SAFE" &&
            frame.Background.KelvinShift == 0.0 &&
            frame.Motion.MotionVelocity <= 0.2 &&
            frame.Overlay.TransitionMs == 400.0;

        return new ValidationResult
        {
            IsPass = pass,
            Name = "signal_loss_safe_mode_visible",
            Detail = $"badge={frame.Overlay.BadgeText};kelvin={frame.Background.KelvinShift:F1};motion={frame.Motion.MotionVelocity:F3};transition_ms={frame.Overlay.TransitionMs:F1}"
        };
    }

    private static ValidationResult ValidateReplaySurfaceDeterminism(long frequency)
    {
        var parameters = new ControlParameters { ActuationEnabled = true };
        var samples = new List<DeterministicReplay.ReplaySample>();
        long t = 0;
        for (int i = 0; i < 600; i++)
        {
            t += (i % 5 == 0) ? (frequency / 90) : (frequency / 110);
            samples.Add(new DeterministicReplay.ReplaySample
            {
                Timestamp = t,
                ScrollDelta = (i % 8 == 0) ? 240 : ((i % 13 == 0) ? -120 : 0),
                KeyDown = (i % 4 == 0),
                FocusChanged = (i % 101 == 0)
            });
        }

        var run1 = BuildFrames(frequency, DeterministicReplay.Run(new DeterministicControlEngine(frequency), parameters, samples));
        var run2 = BuildFrames(frequency, DeterministicReplay.Run(new DeterministicControlEngine(frequency), parameters, samples));
        bool identical = run1.Count == run2.Count;
        if (identical)
        {
            for (int i = 0; i < run1.Count; i++)
            {
                if (!run1[i].Equals(run2[i]))
                {
                    identical = false;
                    break;
                }
            }
        }

        return new ValidationResult
        {
            IsPass = identical,
            Name = "replay_surface_determinism",
            Detail = $"frames={run1.Count};identical={identical}"
        };
    }

    private static ValidationResult ValidateAuthorityTransitionsVisible()
    {
        RenderSurfaceFrame normal = RenderSurfaceComposer.Compose(
            DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(0.5, AuthorityLevel.Normal, "normal")),
            1);
        RenderSurfaceFrame reduced = RenderSurfaceComposer.Compose(
            DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(0.5, AuthorityLevel.Reduced, "reduced")),
            2);
        RenderSurfaceFrame safe = RenderSurfaceComposer.Compose(
            DeterministicRenderMapper.Map(ActuationCommand.Safe("safe")),
            3);

        bool pass =
            normal.Overlay.BadgeText == "NORMAL" &&
            reduced.Overlay.BadgeText == "REDUCED" &&
            safe.Overlay.BadgeText == "SAFE" &&
            reduced.Motion.MotionVelocity <= normal.Motion.MotionVelocity &&
            reduced.Background.BrightnessFactor <= normal.Background.BrightnessFactor &&
            safe.Motion.MotionVelocity <= reduced.Motion.MotionVelocity;

        return new ValidationResult
        {
            IsPass = pass,
            Name = "authority_transition_profiles_visible",
            Detail = $"normal_badge={normal.Overlay.BadgeText};reduced_badge={reduced.Overlay.BadgeText};safe_badge={safe.Overlay.BadgeText}"
        };
    }

    private static List<RenderSurfaceFrame> BuildFrames(long frequency, IReadOnlyList<ActuationCommand> outputs)
    {
        var interpolator = new VisualInterpolator();
        var frames = new List<RenderSurfaceFrame>(outputs.Count * 3);
        long now = 0;
        long frameStep = Math.Max(1, frequency / 60);
        for (int i = 0; i < outputs.Count; i++)
        {
            RenderParameters target = DeterministicRenderMapper.Map(outputs[i]);
            interpolator.SetTarget(target);
            for (int j = 0; j < 3; j++)
            {
                now += frameStep;
                RenderParameters current = interpolator.AdvanceToTimestamp(now, frequency);
                frames.Add(RenderSurfaceComposer.Compose(current, now));
            }
        }

        return frames;
    }
}
