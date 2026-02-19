using Porta.Agent.Common.State;

namespace Porta.Agent.Common.Processing;

public static class ScrollAnalyzer
{
    public static ScrollState Analyze(int rawDelta, long timestamp, long previousTimestamp, long frequency)
    {
        double normalized = SignalNormalizer.NormalizeWheelDelta(rawDelta);
        double velocity = VelocityCalculator.ComputePerSecond(normalized, previousTimestamp, timestamp, frequency);

        return new ScrollState
        {
            RawDelta = rawDelta,
            NormalizedDelta = normalized,
            VelocityPerSecond = velocity,
            Timestamp = timestamp
        };
    }
}
