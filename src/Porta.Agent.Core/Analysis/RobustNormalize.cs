using System.Collections.ObjectModel;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct RobustBaselineStats(
    double Mean,
    double Std,
    int SampleCount);

internal readonly record struct RobustRollingBounds(
    double P5,
    double P95)
{
    internal bool IsValid =>
        double.IsFinite(P5)
        && double.IsFinite(P95)
        && P5 <= P95;
}

internal readonly record struct RobustZClampRange(
    double Min,
    double Max)
{
    internal static readonly RobustZClampRange Default = new(
        PortaExecutionConstants.RobustNormalization.ZClampMin,
        PortaExecutionConstants.RobustNormalization.ZClampMax);
}

internal readonly record struct RobustNormalizeResult(
    double WinsorizedValue,
    double Z,
    bool WasWinsorized,
    bool BaselineReadyForMetric,
    int NonFinite);

internal static class RobustNormalize
{
    private static readonly ReadOnlyDictionary<string, double> StdFloorByMetric =
        new(
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["keyboard_iki_mean"] = 1.0d,
                ["keyboard_iki_cv"] = 0.05d,
                ["keyboard_burst_rate"] = 0.05d,
                ["keyboard_backspace_rate"] = 0.05d,
                ["mouse_speed_mean"] = 1.0d,
                ["mouse_jerk_p95"] = 1.0d,
                ["mouse_scroll_speed"] = 1.0d,
                ["mouse_micro_correction_rate"] = 0.05d,
                ["focus_switch_rate"] = 0.05d,
                ["focus_app_entropy"] = 0.1d,
                ["focus_active_app_count"] = 1.0d,
                ["idle_seconds"] = 1.0d,
                ["idle_micro_idle_count"] = 1.0d
            });

    internal static RobustNormalizeResult Normalize(
        string metricKey,
        double rawValue,
        RobustBaselineStats baseline,
        RobustRollingBounds? rollingBounds,
        RobustZClampRange? zClampRange = null)
    {
        bool nonFiniteDetected = HasAnyNonFiniteInput(rawValue, baseline, rollingBounds, zClampRange);
        if (nonFiniteDetected)
        {
            LogNonFinite(metricKey, rawValue, baseline, rollingBounds, zClampRange);
        }

        double sanitizedMean = SanitizeFinite(baseline.Mean);
        double sanitizedStd = SanitizeFiniteNonNegative(baseline.Std);
        double sanitizedRaw = SanitizeFinite(rawValue);
        (double winsorized, bool wasWinsorized) = Winsorize(sanitizedRaw, rollingBounds);
        bool metricReady = baseline.SampleCount >= PortaExecutionConstants.RobustNormalization.MetricReadyMinSamples;
        if (nonFiniteDetected)
        {
            return new RobustNormalizeResult(
                WinsorizedValue: winsorized,
                Z: 0d,
                WasWinsorized: wasWinsorized,
                BaselineReadyForMetric: metricReady,
                NonFinite: 1);
        }

        if (!metricReady)
        {
            return new RobustNormalizeResult(
                WinsorizedValue: winsorized,
                Z: 0d,
                WasWinsorized: wasWinsorized,
                BaselineReadyForMetric: false,
                NonFinite: 0);
        }

        double stdFloor = ResolveStdFloor(metricKey);
        double sigmaEff = Math.Max(sanitizedStd, stdFloor);
        double zRaw = (winsorized - sanitizedMean) / sigmaEff;
        if (!double.IsFinite(zRaw))
        {
            LogNonFinite(metricKey, rawValue, baseline, rollingBounds, zClampRange);
            return new RobustNormalizeResult(
                WinsorizedValue: winsorized,
                Z: 0d,
                WasWinsorized: wasWinsorized,
                BaselineReadyForMetric: true,
                NonFinite: 1);
        }

        RobustZClampRange clamp = SanitizeClamp(zClampRange ?? RobustZClampRange.Default);
        double z = Math.Clamp(zRaw, clamp.Min, clamp.Max);

        return new RobustNormalizeResult(
            WinsorizedValue: winsorized,
            Z: z,
            WasWinsorized: wasWinsorized,
            BaselineReadyForMetric: true,
            NonFinite: 0);
    }

    private static (double winsorized, bool wasWinsorized) Winsorize(double value, RobustRollingBounds? bounds)
    {
        // Step 8.2 contract:
        // x_w = min(max(raw, p5), p95)
        // fallback when bounds unavailable/invalid is identity (raw unchanged).
        if (!bounds.HasValue || !bounds.Value.IsValid)
        {
            return (value, false);
        }

        RobustRollingBounds valid = bounds.Value;
        double winsorized = Math.Clamp(value, valid.P5, valid.P95);
        bool wasWinsorized = winsorized != value;
        return (winsorized, wasWinsorized);
    }

    private static double ResolveStdFloor(string metricKey)
    {
        if (!string.IsNullOrWhiteSpace(metricKey) && StdFloorByMetric.TryGetValue(metricKey, out double floor))
        {
            return floor;
        }

        return PortaExecutionConstants.RobustNormalization.DefaultStdFloor;
    }

    private static RobustZClampRange SanitizeClamp(RobustZClampRange clamp)
    {
        double min = SanitizeFinite(clamp.Min);
        double max = SanitizeFinite(clamp.Max);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        return new RobustZClampRange(min, max);
    }

    private static bool HasAnyNonFiniteInput(
        double rawValue,
        RobustBaselineStats baseline,
        RobustRollingBounds? rollingBounds,
        RobustZClampRange? zClampRange)
    {
        if (!double.IsFinite(rawValue) || !double.IsFinite(baseline.Mean) || !double.IsFinite(baseline.Std))
        {
            return true;
        }

        if (rollingBounds.HasValue)
        {
            RobustRollingBounds bounds = rollingBounds.Value;
            if (!double.IsFinite(bounds.P5) || !double.IsFinite(bounds.P95))
            {
                return true;
            }
        }

        if (zClampRange.HasValue)
        {
            RobustZClampRange clamp = zClampRange.Value;
            if (!double.IsFinite(clamp.Min) || !double.IsFinite(clamp.Max))
            {
                return true;
            }
        }

        return false;
    }

    private static void LogNonFinite(
        string metricKey,
        double rawValue,
        RobustBaselineStats baseline,
        RobustRollingBounds? rollingBounds,
        RobustZClampRange? zClampRange)
    {
        string boundsFragment = rollingBounds.HasValue
            ? $" p5={rollingBounds.Value.P5} p95={rollingBounds.Value.P95}"
            : " p5=none p95=none";
        string clampFragment = zClampRange.HasValue
            ? $" z_min={zClampRange.Value.Min} z_max={zClampRange.Value.Max}"
            : " z_min=default z_max=default";
        Console.WriteLine(
            $"robust_normalize_non_finite metric_key={metricKey} raw={rawValue} mean={baseline.Mean} std={baseline.Std}{boundsFragment}{clampFragment}");
    }

    private static double SanitizeFinite(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return value;
    }

    private static double SanitizeFiniteNonNegative(double value)
    {
        double sanitized = SanitizeFinite(value);
        return sanitized < 0d ? 0d : sanitized;
    }
}
