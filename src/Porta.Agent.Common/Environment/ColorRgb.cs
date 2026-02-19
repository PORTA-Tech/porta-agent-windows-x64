using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Environment;

public readonly record struct ColorRgb(byte R, byte G, byte B)
{
    public static ColorRgb Lerp(in ColorRgb a, in ColorRgb b, double t)
    {
        double alpha = MathEx.Clamp01(t);
        return new ColorRgb(
            R: ToByte(a.R + ((b.R - a.R) * alpha)),
            G: ToByte(a.G + ((b.G - a.G) * alpha)),
            B: ToByte(a.B + ((b.B - a.B) * alpha)));
    }

    public ColorRgb Scale(double factor)
    {
        return new ColorRgb(
            R: ToByte(R * factor),
            G: ToByte(G * factor),
            B: ToByte(B * factor));
    }

    private static byte ToByte(double value)
    {
        int clamped = (int)System.Math.Round(System.Math.Clamp(value, 0.0, 255.0), MidpointRounding.AwayFromZero);
        return (byte)clamped;
    }
}
