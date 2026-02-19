using System;

namespace Porta.Agent.Common.Processing;

public static class VelocityCalculator
{
    public static double ComputePerSecond(double normalizedDelta, long previousTimestamp, long currentTimestamp, long frequency)
    {
        if (frequency <= 0 || currentTimestamp <= previousTimestamp)
        {
            return 0;
        }

        double dt = (currentTimestamp - previousTimestamp) / (double)frequency;
        if (dt <= 0)
        {
            return 0;
        }

        return normalizedDelta / Math.Max(dt, 1e-6);
    }
}
