using System;

namespace Porta.Agent.Common.Control;

public static class SafetyLimits
{
    public const int MaxAbsInjectionDelta = 240;
    public const int MaxInjectedEventsPerSecond = 300;
    public const int MaxActuationDelayMs = 25;

    public static int ClampInjectionDelta(int delta)
    {
        return Math.Clamp(delta, -MaxAbsInjectionDelta, MaxAbsInjectionDelta);
    }

    public static bool IsInjectableDelta(int delta)
    {
        return delta != 0;
    }
}
