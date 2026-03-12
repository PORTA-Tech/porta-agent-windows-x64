using System.Security.Cryptography;
using System.Text;

namespace Porta.Agent.Runtime.Layer2;

internal sealed class Layer2NormalizationEngine
{
    private readonly Layer2NormalizationConfig _config;
    private readonly Dictionary<string, Layer2MetricState> _stateByMetric = new(StringComparer.Ordinal);
    private readonly double _alphaShort;
    private readonly double _alphaLong;
    private readonly double _alphaBaseline;

    public Layer2NormalizationEngine(Layer2NormalizationConfig config, double samplePeriodSeconds)
    {
        _config = config;
        double shortSamples = Math.Max(1.0, config.ShortWindowSeconds / Math.Max(1.0, samplePeriodSeconds));
        double longSamples = Math.Max(shortSamples, config.LongWindowSeconds / Math.Max(1.0, samplePeriodSeconds));
        double baselineSamples = Math.Max(longSamples, config.BaselineWindowSeconds / Math.Max(1.0, samplePeriodSeconds));
        _alphaShort = 2.0 / (shortSamples + 1.0);
        _alphaLong = 2.0 / (longSamples + 1.0);
        _alphaBaseline = 2.0 / (baselineSamples + 1.0);
    }

    public Layer2PrimitiveResult Transform(Layer2SignalSample sample)
    {
        if (string.IsNullOrWhiteSpace(sample.MetricName) || string.IsNullOrWhiteSpace(sample.Unit))
        {
            return Layer2PrimitiveResult.Error("missing_required_input", sample.TimestampUtc);
        }

        if (double.IsNaN(sample.Value) || double.IsInfinity(sample.Value))
        {
            return Layer2PrimitiveResult.Error("numeric_instability_input_nan_or_inf", sample.TimestampUtc);
        }

        double clampedValue = Math.Clamp(sample.Value, -_config.AbsurdValueCap, _config.AbsurdValueCap);
        if (double.IsNaN(clampedValue) || double.IsInfinity(clampedValue))
        {
            return Layer2PrimitiveResult.Error("numeric_instability_after_clamp", sample.TimestampUtc);
        }

        if (!_stateByMetric.TryGetValue(sample.MetricName, out Layer2MetricState? state))
        {
            state = new Layer2MetricState();
            _stateByMetric[sample.MetricName] = state;
        }

        long n = checked(state.SampleCount + 1);
        double baselineMean = state.BaselineInitialized ? state.BaselineMean : clampedValue;
        double baselineVariance = state.BaselineInitialized ? Math.Max(0.0, state.BaselineVariance) : 0.0;

        if (!state.EwmaInitialized)
        {
            state.EwmaShort = clampedValue;
            state.EwmaLong = clampedValue;
            state.EwmaInitialized = true;
        }
        else
        {
            state.EwmaShort = (_alphaShort * clampedValue) + ((1.0 - _alphaShort) * state.EwmaShort);
            state.EwmaLong = (_alphaLong * clampedValue) + ((1.0 - _alphaLong) * state.EwmaLong);
        }

        double? std = null;
        if (state.BaselineInitialized)
        {
            double sigma = Math.Sqrt(baselineVariance);
            if (!double.IsNaN(sigma) && !double.IsInfinity(sigma))
            {
                std = sigma;
            }
        }

        double z = 0;
        if (state.SampleCount >= _config.MinSamplesForStd && std.HasValue && std.Value >= _config.SigmaEpsilon)
        {
            z = (clampedValue - baselineMean) / std.Value;
            z = Math.Clamp(z, -_config.ZScoreCap, _config.ZScoreCap);
        }

        double confidence = Math.Min(1.0, n / (double)_config.ConfidenceThresholdSamples);
        if (n < _config.MinSamplesForStd)
        {
            confidence = Math.Min(confidence, 0.25);
            std = null;
            z = 0;
        }

        if (!state.BaselineInitialized)
        {
            state.BaselineMean = clampedValue;
            state.BaselineVariance = 0.0;
            state.EffectiveSampleCount = 1.0;
            state.BaselineInitialized = true;
        }
        else
        {
            double delta = clampedValue - state.BaselineMean;
            state.BaselineMean += _alphaBaseline * delta;
            state.BaselineVariance = Math.Max(
                0.0,
                (1.0 - _alphaBaseline) * (state.BaselineVariance + (_alphaBaseline * delta * delta)));
            state.EffectiveSampleCount = 1.0 + ((1.0 - _alphaBaseline) * state.EffectiveSampleCount);
        }

        state.SampleCount = n;
        state.LastUpdateUtc = sample.TimestampUtc;

        if (double.IsNaN(z) || double.IsInfinity(z) || double.IsNaN(state.EwmaShort) || double.IsInfinity(state.EwmaShort))
        {
            return Layer2PrimitiveResult.Error("numeric_instability_output_nan_or_inf", sample.TimestampUtc);
        }

        string primitiveId = BuildPrimitiveId(sample.MachineId, sample.MetricName, sample.TimestampUtc);
        return new Layer2PrimitiveResult(
            Success: true,
            PrimitiveId: primitiveId,
            CorrelationId: sample.CorrelationId,
            Name: sample.MetricName,
            Value: clampedValue,
            Unit: sample.Unit,
            BaselineMean: baselineMean,
            BaselineStd: std,
            ZScore: z,
            Ewma: state.EwmaShort,
            WindowSizeSeconds: _config.ShortWindowSeconds,
            Confidence: Math.Clamp(confidence, 0, 1),
            ComputedAtUtc: sample.TimestampUtc,
            ErrorReason: string.Empty);
    }

    private static string BuildPrimitiveId(string machineId, string metricName, DateTimeOffset ts)
    {
        string seed = $"{machineId}|{metricName}|{ts.ToUnixTimeMilliseconds()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }
}
