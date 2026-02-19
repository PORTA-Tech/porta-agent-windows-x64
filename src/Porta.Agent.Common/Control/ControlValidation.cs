using System;
using System.Collections.Generic;
using System.Linq;
using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Common.Control;

public static class ControlValidation
{
    public sealed class ValidationResult
    {
        public bool IsPass { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    public static IReadOnlyList<ValidationResult> RunBasicSuite(long frequency)
    {
        var results = new List<ValidationResult>
        {
            ValidateConstantHighArousalConvergence(frequency),
            ValidateOscillatingNoiseDamping(frequency),
            ValidateStepChangeCriticallyDamped(frequency),
            ValidateSignalLossSafeFallback(frequency),
            ValidateReplayLogsDeterminism(frequency)
        };
        return results;
    }

    private static ValidationResult ValidateConstantHighArousalConvergence(long frequency)
    {
        var samples = BuildScenarioSamples(
            frequency,
            count: 1200,
            (i, t) => new DeterministicReplay.ReplaySample
            {
                Timestamp = t,
                ScrollDelta = 240,
                KeyDown = (i % 6 == 0),
                FocusChanged = false
            });
        IReadOnlyList<ActuationCommand> outputs = DeterministicReplay.Run(
            new DeterministicControlEngine(frequency),
            new ControlParameters { ActuationEnabled = true },
            samples);

        if (outputs.Count < 20)
        {
            return new ValidationResult
            {
                IsPass = false,
                Name = "constant_high_arousal_convergence",
                Detail = $"insufficient_outputs={outputs.Count}"
            };
        }

        int start = outputs.Count / 2;
        double final = outputs[^1].PacingModifier;
        double previousDeviation = double.MaxValue;
        int monotonicViolations = 0;
        double tailMin = double.MaxValue;
        double tailMax = double.MinValue;

        for (int i = start; i < outputs.Count; i++)
        {
            double current = outputs[i].PacingModifier;
            double deviation = Math.Abs(current - final);
            if (deviation > previousDeviation + 1e-9)
            {
                monotonicViolations++;
            }

            previousDeviation = deviation;
            tailMin = Math.Min(tailMin, current);
            tailMax = Math.Max(tailMax, current);
        }

        double tailSpan = tailMax - tailMin;
        bool pass = monotonicViolations <= 2 && tailSpan <= 0.02;
        return new ValidationResult
        {
            IsPass = pass,
            Name = "constant_high_arousal_convergence",
            Detail = $"monotonic_violations={monotonicViolations};tail_span={tailSpan:F6};outputs={outputs.Count}"
        };
    }

    private static ValidationResult ValidateOscillatingNoiseDamping(long frequency)
    {
        var samples = BuildScenarioSamples(
            frequency,
            count: 900,
            (i, t) => new DeterministicReplay.ReplaySample
            {
                Timestamp = t,
                ScrollDelta = (i % 2 == 0) ? 240 : -240,
                KeyDown = (i % 3 == 0),
                FocusChanged = (i % 97 == 0)
            });
        IReadOnlyList<ActuationCommand> outputs = DeterministicReplay.Run(
            new DeterministicControlEngine(frequency),
            new ControlParameters { ActuationEnabled = true },
            samples);

        int signFlips = 0;
        int previousSign = 0;
        double maxAbsPacing = 0.0;
        for (int i = 0; i < outputs.Count; i++)
        {
            ActuationCommand output = outputs[i];
            maxAbsPacing = Math.Max(maxAbsPacing, Math.Abs(output.PacingModifier));
            int sign = Math.Abs(output.PacingModifier) < 1e-9 ? 0 : Math.Sign(output.PacingModifier);
            if (previousSign != 0 && sign != 0 && sign != previousSign)
            {
                signFlips++;
            }

            if (sign != 0)
            {
                previousSign = sign;
            }
        }

        double flipRatio = outputs.Count == 0 ? 1.0 : signFlips / (double)outputs.Count;
        bool pass = flipRatio <= 0.25 && maxAbsPacing <= 1.0;
        return new ValidationResult
        {
            IsPass = pass,
            Name = "oscillating_noise_damping",
            Detail = $"sign_flips={signFlips};flip_ratio={flipRatio:F6};max_abs_pacing={maxAbsPacing:F6};outputs={outputs.Count}"
        };
    }

    private static ValidationResult ValidateStepChangeCriticallyDamped(long frequency)
    {
        const int sampleCount = 1200;
        int stepAt = sampleCount / 3;
        var samples = BuildScenarioSamples(frequency, sampleCount, (i, t) => new DeterministicReplay.ReplaySample
        {
            Timestamp = t,
            ScrollDelta = i < stepAt ? 0 : 240,
            KeyDown = i < stepAt ? false : (i % 5 == 0),
            FocusChanged = false
        });
        IReadOnlyList<ActuationCommand> outputs = DeterministicReplay.Run(new DeterministicControlEngine(frequency), new ControlParameters { ActuationEnabled = true }, samples);

        if (outputs.Count < 30)
        {
            return new ValidationResult
            {
                IsPass = false,
                Name = "step_change_critical_damping",
                Detail = $"insufficient_outputs={outputs.Count}"
            };
        }

        int stepOutputIndex = outputs.Count / 3;
        var postStep = outputs.Skip(stepOutputIndex).Select(o => o.PacingModifier).ToArray();
        double final = postStep.Skip(Math.Max(0, postStep.Length - 30)).Average();
        double finalAbs = Math.Max(Math.Abs(final), 1e-6);
        double maxAbsPostStep = postStep.Max(v => Math.Abs(v));
        double overshootRatio = maxAbsPostStep / finalAbs;
        int crossingCount = 0;
        int prevSign = Math.Sign(postStep[0] - final);
        for (int i = 1; i < postStep.Length; i++)
        {
            int sign = Math.Sign(postStep[i] - final);
            if (sign != 0 && prevSign != 0 && sign != prevSign)
            {
                crossingCount++;
            }
            if (sign != 0)
            {
                prevSign = sign;
            }
        }

        bool pass = overshootRatio <= 1.05 && crossingCount <= 1;
        return new ValidationResult
        {
            IsPass = pass,
            Name = "step_change_critical_damping",
            Detail = $"overshoot_ratio={overshootRatio:F6};crossings={crossingCount};outputs={outputs.Count}"
        };
    }

    private static ValidationResult ValidateSignalLossSafeFallback(long frequency)
    {
        const int sampleCount = 2400;
        int lossAt = sampleCount / 3;
        var samples = BuildScenarioSamples(frequency, sampleCount, (i, t) => new DeterministicReplay.ReplaySample
        {
            Timestamp = t,
            ScrollDelta = i < lossAt ? 240 : 0,
            KeyDown = i < lossAt && (i % 4 == 0),
            FocusChanged = i < lossAt && (i % 97 == 0)
        });
        IReadOnlyList<ActuationCommand> outputs = DeterministicReplay.Run(new DeterministicControlEngine(frequency), new ControlParameters { ActuationEnabled = true }, samples);

        int fallbackStart = Math.Max(0, outputs.Count - 60);
        int nonSafeCount = 0;
        for (int i = fallbackStart; i < outputs.Count; i++)
        {
            ActuationCommand output = outputs[i];
            if (output.AuthorityLevel != AuthorityLevel.Safe ||
                output.StimulationIntensity > 0.0 ||
                Math.Abs(output.PacingModifier) > 0.0)
            {
                nonSafeCount++;
            }
        }

        bool pass = nonSafeCount == 0;
        return new ValidationResult
        {
            IsPass = pass,
            Name = "signal_loss_safe_fallback",
            Detail = $"non_safe_outputs={nonSafeCount};tail_checked={(outputs.Count - fallbackStart)};outputs={outputs.Count}"
        };
    }

    private static ValidationResult ValidateReplayLogsDeterminism(long frequency)
    {
        var parameters = new ControlParameters { ActuationEnabled = true };
        var samples = new List<DeterministicReplay.ReplaySample>();
        long t = 0;
        for (int i = 0; i < 700; i++)
        {
            // Pseudo-log cadence with mixed periodicity.
            t += (i % 7 == 0) ? (frequency / 80) : (frequency / 120);
            samples.Add(new DeterministicReplay.ReplaySample
            {
                Timestamp = t,
                ScrollDelta = (i % 9 == 0) ? 240 : (i % 11 == 0 ? -120 : 0),
                KeyDown = (i % 5 == 0),
                FocusChanged = (i % 97 == 0)
            });
        }

        var run1 = DeterministicReplay.Run(new DeterministicControlEngine(frequency), parameters, samples);
        var run2 = DeterministicReplay.Run(new DeterministicControlEngine(frequency), parameters, samples);
        bool same = run1.Count == run2.Count;
        if (same)
        {
            for (int i = 0; i < run1.Count; i++)
            {
                if (run1[i].StimulationIntensity != run2[i].StimulationIntensity ||
                    run1[i].SceneDynamics != run2[i].SceneDynamics ||
                    run1[i].VisualTemperatureShift != run2[i].VisualTemperatureShift ||
                    run1[i].PacingModifier != run2[i].PacingModifier ||
                    run1[i].AuthorityLevel != run2[i].AuthorityLevel)
                {
                    same = false;
                    break;
                }
            }
        }

        return new ValidationResult
        {
            IsPass = same,
            Name = "replay_logs_determinism",
            Detail = $"outputs={run1.Count};identical={same}"
        };
    }

    private static List<DeterministicReplay.ReplaySample> BuildScenarioSamples(
        long frequency,
        int count,
        Func<int, long, DeterministicReplay.ReplaySample> factory)
    {
        int sampleStep = Math.Max(1, (int)(frequency / 100));
        long timestamp = 0;
        var samples = new List<DeterministicReplay.ReplaySample>(count);
        for (int i = 0; i < count; i++)
        {
            timestamp += sampleStep;
            samples.Add(factory(i, timestamp));
        }

        return samples;
    }
}
