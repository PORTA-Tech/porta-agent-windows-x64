using Porta.Agent.Runtime.Layer2;

namespace Porta.Agent.Runtime.Tests;

public sealed class Layer2NormalizationEngineTests
{
    [Fact]
    public void Transform_UsesConfidenceGateUntilMinimumSamples()
    {
        var engine = new Layer2NormalizationEngine(
            new Layer2NormalizationConfig
            {
                ShortWindowSeconds = 30,
                LongWindowSeconds = 60,
                BaselineWindowSeconds = 120,
                MinSamplesForStd = 5,
                ConfidenceThresholdSamples = 10
            },
            samplePeriodSeconds: 10);
        DateTimeOffset start = DateTimeOffset.Parse("2026-03-12T08:00:00Z");
        Layer2PrimitiveResult result = default;
        double[] samples = [8d, 10d, 12d, 9d, 11d];

        for (int i = 0; i < samples.Length; i++)
        {
            result = engine.Transform(BuildSample(samples[i], start.AddSeconds(i * 10)));

            if (i < 4)
            {
                Assert.True(result.Success);
                Assert.Null(result.BaselineStd);
                Assert.Equal(0d, result.ZScore, precision: 6);
                Assert.InRange(result.Confidence, 0d, 0.25d);
            }
        }

        Assert.True(result.Success);
        Assert.NotNull(result.BaselineStd);
        Assert.True(result.BaselineStd > 0d);
        Assert.True(result.Confidence > 0.25d);
    }

    [Fact]
    public void Transform_RollingBaselineAdaptsToRecentShift()
    {
        var engine = new Layer2NormalizationEngine(
            new Layer2NormalizationConfig
            {
                ShortWindowSeconds = 30,
                LongWindowSeconds = 120,
                BaselineWindowSeconds = 120,
                MinSamplesForStd = 5,
                ConfidenceThresholdSamples = 20
            },
            samplePeriodSeconds: 10);
        DateTimeOffset start = DateTimeOffset.Parse("2026-03-12T09:00:00Z");
        double[] warmup = [8d, 10d, 12d, 9d, 11d, 8d, 10d, 12d, 9d, 11d];

        for (int i = 0; i < warmup.Length; i++)
        {
            engine.Transform(BuildSample(warmup[i], start.AddSeconds(i * 10)));
        }

        Layer2PrimitiveResult spike = engine.Transform(BuildSample(100d, start.AddSeconds(100)));

        Layer2PrimitiveResult adapted = default;
        double[] shifted = [98d, 100d, 102d, 99d, 101d, 98d, 100d, 102d, 99d, 101d, 100d, 100d];
        for (int i = 0; i < shifted.Length; i++)
        {
            adapted = engine.Transform(BuildSample(shifted[i], start.AddSeconds(110 + (i * 10))));
        }

        Assert.True(spike.Success);
        Assert.True(adapted.Success);
        Assert.True(spike.ZScore > adapted.ZScore);
        Assert.True(adapted.BaselineMean > 60d);
        Assert.True(adapted.BaselineMean < 100d);
    }

    private static Layer2SignalSample BuildSample(double value, DateTimeOffset atUtc)
    {
        return new Layer2SignalSample(
            MachineId: "machine-a",
            CorrelationId: $"corr-{atUtc.ToUnixTimeSeconds()}",
            MetricName: "system.cpu_utilization",
            Value: value,
            Unit: "percent",
            WindowDurationSeconds: 10d,
            CollectionMethod: "test_fixture",
            PrivacyClass: "restricted",
            TimestampUtc: atUtc);
    }
}
