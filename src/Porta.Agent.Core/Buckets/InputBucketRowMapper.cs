using System.Text.Json;

namespace Porta.Agent.Core.Buckets;

public readonly record struct InputBucketRow(
    long BucketSeq,
    string TimestampUtc,
    string StartUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
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
            }
        });

        return new InputBucketRow(
            BucketSeq: bucket.BucketSeq,
            TimestampUtc: startUtc,
            StartUtc: startUtc,
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: scroll,
            FocusSwitchRate: focus,
            QualityScore: Clamp01(bucket.QualityScore),
            Partial: bucket.Partial ? 1 : 0,
            ContributorsJson: contributorsJson);
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
