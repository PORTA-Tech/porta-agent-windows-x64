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

    public static IReadOnlyList<ValidationResult> RunProductionSuite(long frequency)
    {
        var results = new List<ValidationResult>(RunSuite(frequency))
        {
            ValidateAuthorityProfileMatrix(),
            ValidateTransitionInterpolationMonotonic(),
            ValidateRenderCadenceStability(frequency),
            ValidateSceneDynamicsComfortCaps(),
            ValidateTemperatureMappingBounds()
        };
        return results;
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

    private static ValidationResult ValidateAuthorityProfileMatrix()
    {
        int failures = 0;
        double[] controls = [0.2, 0.5, 0.9, -0.6];
        AuthorityLevel[] authorities = [AuthorityLevel.Normal, AuthorityLevel.Reduced, AuthorityLevel.Safe];
        foreach (double control in controls)
        {
            foreach (AuthorityLevel authority in authorities)
            {
                RenderParameters mapped = authority == AuthorityLevel.Safe
                    ? DeterministicRenderMapper.Map(ActuationCommand.Safe("matrix"))
                    : DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(control, authority, "matrix"));

                if (authority == AuthorityLevel.Safe)
                {
                    if (mapped.KelvinShift != 0 || mapped.TransitionMs != 400.0 || mapped.MotionEnergy > 0.15)
                    {
                        failures++;
                    }
                }
                else if (authority == AuthorityLevel.Reduced)
                {
                    if (mapped.MotionEnergy > 0.5 || mapped.BrightnessRange > 1.04 || mapped.BrightnessRange < 0.96)
                    {
                        failures++;
                    }
                }
            }
        }

        return new ValidationResult
        {
            IsPass = failures == 0,
            Name = "authority_profile_matrix",
            Detail = $"controls={controls.Length};authorities={authorities.Length};failures={failures}"
        };
    }

    private static ValidationResult ValidateTransitionInterpolationMonotonic()
    {
        var interpolator = new VisualInterpolator();
        RenderParameters slow = DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(-1.0, AuthorityLevel.Normal, "slow"));
        RenderParameters fast = DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(1.0, AuthorityLevel.Normal, "fast"));
        interpolator.SetTarget(slow);
        for (int i = 0; i < 40; i++)
        {
            _ = interpolator.AdvanceFrame();
        }

        interpolator.SetTarget(fast);
        double previous = double.MaxValue;
        int violations = 0;
        for (int i = 0; i < 90; i++)
        {
            RenderParameters current = interpolator.AdvanceFrame();
            if (current.TransitionMs > previous + 1e-9)
            {
                violations++;
            }

            previous = current.TransitionMs;
        }

        return new ValidationResult
        {
            IsPass = violations == 0,
            Name = "transition_interpolation_monotonic",
            Detail = $"violations={violations};final_ms={previous:F6}"
        };
    }

    private static ValidationResult ValidateRenderCadenceStability(long frequency)
    {
        var interpolator = new VisualInterpolator();
        RenderParameters target = DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(0.8, AuthorityLevel.Normal, "cadence"));
        interpolator.SetTarget(target);

        long now = 0;
        long nominalStep = Math.Max(1, frequency / 60);
        double maxDelta = 0.0;
        double previous = 0.0;
        bool hasPrevious = false;

        for (int i = 0; i < 360; i++)
        {
            long jitter = i % 7 switch
            {
                0 => nominalStep / 3,
                1 => nominalStep / 4,
                2 => 0,
                _ => nominalStep / 6
            };
            now += nominalStep + jitter;
            RenderParameters frame = interpolator.AdvanceToTimestamp(now, frequency);
            if (hasPrevious)
            {
                maxDelta = Math.Max(maxDelta, Math.Abs(frame.MotionAmplitude - previous));
            }
            previous = frame.MotionAmplitude;
            hasPrevious = true;
        }

        bool pass = maxDelta <= 0.03;
        return new ValidationResult
        {
            IsPass = pass,
            Name = "render_cadence_stability",
            Detail = $"max_motion_delta={maxDelta:F6}"
        };
    }

    private static ValidationResult ValidateSceneDynamicsComfortCaps()
    {
        int failures = 0;
        double[] inputs = [0.2, 0.5, 1.0, -1.0];
        foreach (double input in inputs)
        {
            RenderParameters normal = DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(input, AuthorityLevel.Normal, "comfort_normal"));
            RenderParameters reduced = DeterministicRenderMapper.Map(ActuationCommand.FromControlSignal(input, AuthorityLevel.Reduced, "comfort_reduced"));

            if (normal.AnimationVelocity > 1.10 || normal.MotionAmplitude > 0.12 || normal.ParallaxAmount > 0.018)
            {
                failures++;
            }

            if (reduced.AnimationVelocity > 0.75 || reduced.MotionAmplitude > 0.08 || reduced.ParallaxAmount > 0.012)
            {
                failures++;
            }
        }

        return new ValidationResult
        {
            IsPass = failures == 0,
            Name = "scene_dynamics_comfort_caps",
            Detail = $"inputs={inputs.Length};failures={failures}"
        };
    }

    private static ValidationResult ValidateTemperatureMappingBounds()
    {
        int failures = 0;
        double[] shifts = [-1.0, -0.5, 0.0, 0.5, 1.0];
        foreach (double shift in shifts)
        {
            var cmd = new ActuationCommand
            {
                StimulationIntensity = 0.4,
                SceneDynamics = 0.4,
                VisualTemperatureShift = shift,
                PacingModifier = 0.0,
                AuthorityLevel = AuthorityLevel.Normal,
                Note = "temp"
            };
            RenderParameters mapped = DeterministicRenderMapper.Map(cmd);
            RenderSurfaceFrame frame = RenderSurfaceComposer.Compose(mapped, 1);
            if (Math.Abs(frame.Background.KelvinShift) > 1200.0 ||
                frame.Background.SaturationScale < 1.0 ||
                frame.Background.SaturationScale > 1.08)
            {
                failures++;
            }
        }

        return new ValidationResult
        {
            IsPass = failures == 0,
            Name = "temperature_mapping_bounds",
            Detail = $"shifts={shifts.Length};failures={failures}"
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
