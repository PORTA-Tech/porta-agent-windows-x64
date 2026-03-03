using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

public sealed class RobustNormalizeTests
{
    [Fact]
    public void Normalize_WinsorizesWithBounds_AndSetsFlags()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "keyboard_iki_mean",
            rawValue: 13d,
            baseline: new RobustBaselineStats(Mean: 10d, Std: 2d, SampleCount: 120),
            rollingBounds: new RobustRollingBounds(P5: 9d, P95: 11d));

        Assert.Equal(11d, result.WinsorizedValue, precision: 6);
        Assert.Equal(0.5d, result.Z, precision: 6);
        Assert.True(result.WasWinsorized);
        Assert.True(result.BaselineReadyForMetric);
        Assert.Equal(0, result.NonFinite);
    }

    [Fact]
    public void Normalize_UsesStdFloor_AndClampsZ()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "focus_switch_rate",
            rawValue: 100d,
            baseline: new RobustBaselineStats(Mean: 0d, Std: 0d, SampleCount: 120),
            rollingBounds: null);

        Assert.Equal(100d, result.WinsorizedValue, precision: 6);
        Assert.Equal(PortaExecutionConstants.RobustNormalization.ZClampMax, result.Z, precision: 6);
        Assert.False(result.WasWinsorized);
        Assert.True(result.BaselineReadyForMetric);
        Assert.Equal(0, result.NonFinite);
    }

    [Fact]
    public void Normalize_UsesStdFloor_WhenStdIsNearZero()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "focus_switch_rate",
            rawValue: 1d,
            baseline: new RobustBaselineStats(Mean: 0d, Std: 1e-12d, SampleCount: 120),
            rollingBounds: null,
            zClampRange: new RobustZClampRange(-1000d, 1000d));

        // focus_switch_rate std_floor is pinned at 0.05 => z = 1 / 0.05 = 20
        Assert.Equal(20d, result.Z, precision: 6);
        Assert.True(result.BaselineReadyForMetric);
        Assert.Equal(0, result.NonFinite);
    }

    [Fact]
    public void Normalize_BaselineReadyForMetric_FollowsMetricReadyMinSamples()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "focus_switch_rate",
            rawValue: 1d,
            baseline: new RobustBaselineStats(
                Mean: 1d,
                Std: 1d,
                SampleCount: PortaExecutionConstants.RobustNormalization.MetricReadyMinSamples - 1),
            rollingBounds: null);

        Assert.False(result.BaselineReadyForMetric);
        Assert.Equal(0d, result.Z, precision: 6);
        Assert.Equal(0, result.NonFinite);
    }

    [Fact]
    public void Normalize_UsesPerMetricStdFloor()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "focus_app_entropy",
            rawValue: 0.25d,
            baseline: new RobustBaselineStats(Mean: 0d, Std: 0d, SampleCount: 120),
            rollingBounds: null,
            zClampRange: new RobustZClampRange(-100d, 100d));

        // focus_app_entropy std_floor is pinned at 0.1 => z = 0.25 / 0.1 = 2.5
        Assert.Equal(2.5d, result.Z, precision: 6);
        Assert.True(result.BaselineReadyForMetric);
        Assert.Equal(0, result.NonFinite);
    }

    [Fact]
    public void Normalize_NonFiniteInput_SetsZToZero_AndFlagsNonFinite()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "focus_switch_rate",
            rawValue: double.NaN,
            baseline: new RobustBaselineStats(Mean: 1d, Std: 1d, SampleCount: 120),
            rollingBounds: null);

        Assert.Equal(0d, result.Z, precision: 6);
        Assert.Equal(1, result.NonFinite);
    }

    [Fact]
    public void Normalize_NonFiniteMean_SetsZToZero_AndFlagsNonFinite()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "focus_switch_rate",
            rawValue: 1d,
            baseline: new RobustBaselineStats(Mean: double.PositiveInfinity, Std: 1d, SampleCount: 120),
            rollingBounds: null);

        Assert.Equal(0d, result.Z, precision: 6);
        Assert.Equal(1, result.NonFinite);
    }

    [Fact]
    public void Normalize_NonFiniteStd_SetsZToZero_AndFlagsNonFinite()
    {
        RobustNormalizeResult result = RobustNormalize.Normalize(
            metricKey: "focus_switch_rate",
            rawValue: 1d,
            baseline: new RobustBaselineStats(Mean: 0d, Std: double.NaN, SampleCount: 120),
            rollingBounds: null);

        Assert.Equal(0d, result.Z, precision: 6);
        Assert.Equal(1, result.NonFinite);
    }
}
