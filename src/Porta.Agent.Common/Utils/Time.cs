using System;

namespace Porta.Agent.Common.Utils;

public static class Time
{
    public static double ToSeconds(long deltaTicks, long frequency)
    {
        if (frequency <= 0)
        {
            return 0;
        }

        return deltaTicks / (double)frequency;
    }

    public static long SecondsToTicks(double seconds, long frequency)
    {
        if (seconds <= 0 || frequency <= 0)
        {
            return 0;
        }

        return (long)Math.Round(seconds * frequency, MidpointRounding.AwayFromZero);
    }
}
