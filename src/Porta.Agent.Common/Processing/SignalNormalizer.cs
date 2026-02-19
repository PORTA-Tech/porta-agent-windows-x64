using System;

namespace Porta.Agent.Common.Processing;

public static class SignalNormalizer
{
    public const double WheelStep = 120.0;

    public static double NormalizeWheelDelta(int delta)
    {
        // Typical wheel ticks arrive in multiples of 120.
        return Math.Clamp(delta / WheelStep, -4.0, 4.0);
    }
}
