using System;

namespace Porta.Agent.Common.Utils;

public static class MathEx
{
    public static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    public static double Ema(double previous, double current, double alpha)
    {
        double a = Clamp01(alpha);
        return previous + (a * (current - previous));
    }

    public static double SafeDivide(double numerator, double denominator, double fallback = 0)
    {
        return Math.Abs(denominator) < 1e-9 ? fallback : numerator / denominator;
    }

    public static double Quantize(double value, int decimals = 12)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.0;
        }

        return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
    }
}
