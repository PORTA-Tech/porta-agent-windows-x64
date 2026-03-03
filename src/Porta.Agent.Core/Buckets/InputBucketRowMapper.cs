using System.Text.Json;

namespace Porta.Agent.Core.Buckets;

public readonly record struct InputBucketRow(
    long BucketSeq,
    string TimestampUtc,
    string StartUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    double KeyboardIkiMean,
    double KeyboardIkiCv,
    double KeyboardBurstRate,
    double KeyboardBackspaceRate,
    double MouseSpeedMean,
    double MouseJerkP95,
    double MouseScrollSpeed,
    double MouseMicroCorrectionRate,
    double FocusAppEntropy,
    double FocusActiveAppCount,
    double IdleSeconds,
    double IdleMicroIdleCount,
    double QualityScore,
    int Partial,
    string ContributorsJson);

internal static class InputBucketRowMapper
{
    internal static InputBucketRow BuildInputBucketRow(BucketBuffer bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);

        int sampleCount = Math.Max(1, bucket.SampleCount);
        double typing = Clamp01(bucket.TypingBurstIntensitySum / sampleCount);
        double scroll = Clamp01(bucket.ScrollVelocityIndexSum / sampleCount);
        double focus = Clamp01(bucket.FocusSwitchRateSum / sampleCount);
        string startUtc = Time.toIsoUtcMsString(bucket.StartUtcMs);
        IReadOnlyList<string> missingMajorChannels = ResolveMissingMajorChannels(bucket);

        string contributorsJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            keyboard = new
            {
                keys = bucket.Keyboard.KeydownCount,
                backspace = bucket.Keyboard.BackspaceCount
            },
            mouse = new
            {
                moves = bucket.Mouse.MoveSampleCount,
                scroll = bucket.Mouse.WheelSampleCount
            },
            focus = new
            {
                switches = bucket.Focus.FocusSwitchesCount
            },
            idle = new
            {
                idle_s = ClampNonNegativeFinite(bucket.Idle.IdleSeconds)
            },
            missing_major_channels = missingMajorChannels
        });

        return new InputBucketRow(
            BucketSeq: bucket.BucketSeq,
            TimestampUtc: startUtc,
            StartUtc: startUtc,
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: scroll,
            FocusSwitchRate: focus,
            KeyboardIkiMean: ClampNonNegativeFinite(bucket.Keyboard.IkiMeanMs),
            KeyboardIkiCv: ClampNonNegativeFinite(bucket.Keyboard.IkiCv),
            KeyboardBurstRate: Clamp01(bucket.Keyboard.BurstRate),
            KeyboardBackspaceRate: Clamp01(bucket.Keyboard.BackspaceRate),
            MouseSpeedMean: ClampNonNegativeFinite(bucket.Mouse.SpeedMeanPxPerSec),
            MouseJerkP95: ClampNonNegativeFinite(bucket.Mouse.JerkP95),
            MouseScrollSpeed: ClampNonNegativeFinite(bucket.Mouse.ScrollSpeed),
            MouseMicroCorrectionRate: Clamp01(bucket.Mouse.MicroCorrectionRate),
            FocusAppEntropy: ClampNonNegativeFinite(bucket.Focus.AppEntropy),
            FocusActiveAppCount: ClampNonNegativeFinite(bucket.Focus.ActiveAppCount),
            IdleSeconds: ClampNonNegativeFinite(bucket.Idle.IdleSeconds),
            IdleMicroIdleCount: ClampNonNegativeFinite(bucket.Idle.MicroIdleCount),
            QualityScore: Clamp01(bucket.QualityScore),
            Partial: bucket.Partial ? 1 : 0,
            ContributorsJson: contributorsJson);
    }

    private static IReadOnlyList<string> ResolveMissingMajorChannels(BucketBuffer bucket)
    {
        if (!bucket.StateFlagsStage.TryGetValue("major_channels_missing", out string? raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static double ClampNonNegativeFinite(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Max(0d, value);
    }
}
