using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal enum BaselineTransformKind
{
    Identity = 0,
    Log1p = 1
}

internal readonly record struct BaselineMetricDefinition(
    string MetricKey,
    string SourceField,
    double SpikeGuardMin,
    double SpikeGuardMax,
    BaselineTransformKind Transform,
    Func<InputBucketRow, double> ReadRawValue)
{
    internal bool TryGetTransformedValue(InputBucketRow row, out double value)
    {
        double raw = ReadRawValue(row);
        if (double.IsNaN(raw) || double.IsInfinity(raw))
        {
            value = 0d;
            return false;
        }

        if (raw < SpikeGuardMin || raw > SpikeGuardMax)
        {
            value = 0d;
            return false;
        }

        value = Transform switch
        {
            BaselineTransformKind.Identity => raw,
            BaselineTransformKind.Log1p => Math.Log(1d + Math.Max(0d, raw)),
            _ => raw
        };

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = 0d;
            return false;
        }

        return true;
    }
}

internal static class BaselineMetricRegistry
{
    internal const int RegistryVersion = 1;

    internal static readonly IReadOnlyList<BaselineMetricDefinition> Metrics =
    [
        new("keyboard_iki_mean", "keyboard_iki_mean", 0d, 10000d, BaselineTransformKind.Log1p, static row => row.KeyboardIkiMean),
        new("keyboard_iki_cv", "keyboard_iki_cv", 0d, 10d, BaselineTransformKind.Identity, static row => row.KeyboardIkiCv),
        new("keyboard_burst_rate", "keyboard_burst_rate", 0d, 1d, BaselineTransformKind.Identity, static row => row.KeyboardBurstRate),
        new("keyboard_backspace_rate", "keyboard_backspace_rate", 0d, 1d, BaselineTransformKind.Identity, static row => row.KeyboardBackspaceRate),
        new("mouse_speed_mean", "mouse_speed_mean", 0d, 50000d, BaselineTransformKind.Log1p, static row => row.MouseSpeedMean),
        new("mouse_jerk_p95", "mouse_jerk_p95", 0d, 500000d, BaselineTransformKind.Log1p, static row => row.MouseJerkP95),
        new("mouse_scroll_speed", "mouse_scroll_speed", 0d, 50000d, BaselineTransformKind.Log1p, static row => row.MouseScrollSpeed),
        new("mouse_micro_correction_rate", "mouse_micro_correction_rate", 0d, 1d, BaselineTransformKind.Identity, static row => row.MouseMicroCorrectionRate),
        new("focus_switch_rate", "focus_switch_rate", 0d, 100d, BaselineTransformKind.Identity, static row => row.FocusSwitchRate),
        new("focus_app_entropy", "focus_app_entropy", 0d, 8d, BaselineTransformKind.Identity, static row => row.FocusAppEntropy),
        new("focus_active_app_count", "focus_active_app_count", 0d, 100d, BaselineTransformKind.Identity, static row => row.FocusActiveAppCount),
        new("idle_seconds", "idle_seconds", 0d, 300d, BaselineTransformKind.Log1p, static row => row.IdleSeconds),
        new("idle_micro_idle_count", "idle_micro_idle_count", 0d, 100d, BaselineTransformKind.Identity, static row => row.IdleMicroIdleCount)
    ];
}
