using System;
using Porta.Agent.Common.State;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Processing;

public sealed class FeatureExtractor
{
    public double IntegralClamp { get; init; } = 2.0;

    public ControlFeatures Extract(
        AggregatedState current,
        AggregatedState? previous,
        double targetMin,
        double targetMax,
        double previousIntegral,
        long frequency)
    {
        double lower = Math.Min(targetMin, targetMax);
        double upper = Math.Max(targetMin, targetMax);
        double currentArousal = current.CompositeArousalEstimate;

        // Target envelope deadband: no control error inside [lower, upper].
        double error = 0;
        if (currentArousal < lower)
        {
            error = lower - currentArousal;
        }
        else if (currentArousal > upper)
        {
            error = upper - currentArousal;
        }

        double dt = previous == null ? 0 : MathEx.Quantize(Math.Max(1e-6, Time.ToSeconds(current.Timestamp - previous.Timestamp, frequency)));
        double velocity = previous == null ? 0 : MathEx.Quantize((currentArousal - previous.CompositeArousalEstimate) / dt);
        double integral = MathEx.Quantize(Math.Clamp(previousIntegral + (error * dt), -IntegralClamp, IntegralClamp));

        double scrollNorm = MathEx.Clamp01(Math.Abs(current.ScrollRate));
        double keyboardNorm = MathEx.Clamp01(current.KeyboardRate / 8.0);
        double activity = MathEx.Quantize(0.5 * (scrollNorm + keyboardNorm));
        double coherence = MathEx.Quantize(1.0 - Math.Abs(scrollNorm - keyboardNorm));
        double engagement = MathEx.Quantize(MathEx.Clamp01(activity * coherence));

        // Variance spikes are the primary instability signal; focus instability is a secondary signal.
        double varianceSpike = MathEx.Quantize(MathEx.Clamp01(current.InteractionVariance / 4.0));
        double focusInstability = MathEx.Quantize(1.0 - MathEx.Clamp01(current.FocusStability));
        double instability = MathEx.Quantize(MathEx.Clamp01((0.8 * varianceSpike) + (0.2 * focusInstability)));

        return new ControlFeatures
        {
            Timestamp = current.Timestamp,
            ArousalError = MathEx.Quantize(error),
            ArousalVelocity = velocity,
            ArousalIntegral = integral,
            EngagementIndex = engagement,
            InstabilityIndex = instability,
            SignalConfidence = current.SignalConfidence
        };
    }
}
