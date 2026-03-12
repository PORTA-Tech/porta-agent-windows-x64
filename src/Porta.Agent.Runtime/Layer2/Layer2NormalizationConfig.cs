namespace Porta.Agent.Runtime.Layer2;

internal sealed class Layer2NormalizationConfig
{
    public int ShortWindowSeconds { get; init; } = 30;
    public int LongWindowSeconds { get; init; } = 900;
    public int BaselineWindowSeconds { get; init; } = 1800;
    public int MinSamplesForStd { get; init; } = 30;
    public int ConfidenceThresholdSamples { get; init; } = 100;
    public double ZScoreCap { get; init; } = 10.0;
    public double SigmaEpsilon { get; init; } = 1e-9;
    public double AbsurdValueCap { get; init; } = 1_000_000_000.0;

    public static Layer2NormalizationConfig FromEnvironment()
    {
        return new Layer2NormalizationConfig
        {
            ShortWindowSeconds = ReadInt("PORTA_LAYER2_SHORT_WINDOW_SECONDS", 30, 10, 300),
            LongWindowSeconds = ReadInt("PORTA_LAYER2_LONG_WINDOW_SECONDS", 900, 300, 1800),
            BaselineWindowSeconds = ReadInt("PORTA_LAYER2_BASELINE_WINDOW_SECONDS", 1800, 300, 7200),
            MinSamplesForStd = ReadInt("PORTA_LAYER2_MIN_SAMPLES_FOR_STD", 30, 5, 1000),
            ConfidenceThresholdSamples = ReadInt("PORTA_LAYER2_CONFIDENCE_THRESHOLD", 100, 10, 5000),
            ZScoreCap = ReadDouble("PORTA_LAYER2_ZSCORE_CAP", 10.0, 1.0, 100.0),
            SigmaEpsilon = ReadDouble("PORTA_LAYER2_SIGMA_EPSILON", 1e-9, 1e-12, 1e-3),
            AbsurdValueCap = ReadDouble("PORTA_LAYER2_ABSURD_VALUE_CAP", 1_000_000_000.0, 1_000.0, 1_000_000_000_000.0)
        };
    }

    private static int ReadInt(string key, int fallback, int min, int max)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (!int.TryParse(raw, out int value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static double ReadDouble(string key, double fallback, double min, double max)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (!double.TryParse(raw, out double value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }
}
