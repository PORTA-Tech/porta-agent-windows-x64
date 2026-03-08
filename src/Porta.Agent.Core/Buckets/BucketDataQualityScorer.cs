namespace Porta.Agent.Core.Buckets;

internal static class BucketDataQualityScorer
{
    // Pinned scoring contract for v1.
    private const double KeyboardMissingPenalty = 0.25;
    private const double MouseMissingPenalty = 0.20;
    private const double FocusMissingPenalty = 0.20;
    private const double IdleMissingPenalty = 0.15;
    private const double MaxDropPenalty = 0.25;
    private const double DropPenaltySlope = 0.50;

    internal static BucketDataQualityScore Compute(
        BucketBuffer bucket,
        BucketDropCountersDelta dropDelta)
    {
        ArgumentNullException.ThrowIfNull(bucket);

        bool keyboardAvailable = bucket.Keyboard.KeydownCount > 0;
        bool mouseAvailable = bucket.Mouse.MoveSampleCount > 0
            || bucket.Mouse.WheelSampleCount > 0;
        bool focusAvailable = bucket.Focus.FocusEvents.Count > 0
            || bucket.Focus.ObservedIntervalCount > 0;
        bool idleAvailable = bucket.Idle.Samples.Count > 0
            || bucket.Idle.ObservedIntervalCount > 0;

        var missing = new List<string>(4);
        double score = 1d;

        if (!keyboardAvailable)
        {
            missing.Add("keyboard");
            score -= KeyboardMissingPenalty;
        }

        if (!mouseAvailable)
        {
            missing.Add("mouse");
            score -= MouseMissingPenalty;
        }

        if (!focusAvailable)
        {
            missing.Add("focus");
            score -= FocusMissingPenalty;
        }

        if (!idleAvailable)
        {
            missing.Add("idle");
            score -= IdleMissingPenalty;
        }

        long totalDrops = Math.Max(0, dropDelta.TotalDrops);
        int denominator = Math.Max(1, bucket.SampleCount + checked((int)Math.Min(int.MaxValue, totalDrops)));
        double dropRate = totalDrops / (double)denominator;
        double dropPenalty = Math.Min(MaxDropPenalty, dropRate * DropPenaltySlope);
        score -= dropPenalty;

        double clamped = Clamp01(score);
        // Normal active use often alternates between keyboard-only and mouse-only windows.
        // Treat those as valid interaction buckets; only mark partial when both interaction
        // channels are absent or a non-interaction channel is missing.
        bool interactionMissing = !keyboardAvailable && !mouseAvailable;
        bool partial = interactionMissing || !focusAvailable || !idleAvailable;
        return new BucketDataQualityScore(clamped, partial, missing);
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }
}

internal readonly record struct BucketDropCountersDelta(
    long FutureSkewDrops,
    long TooOldDrops,
    long LateFinalDrops)
{
    public long TotalDrops => checked(FutureSkewDrops + TooOldDrops + LateFinalDrops);
}

internal readonly record struct BucketDropCountersSnapshot(
    long FutureSkewDrops,
    long TooOldDrops,
    long LateFinalDrops);

internal readonly record struct BucketDataQualityScore(
    double QualityScore,
    bool Partial,
    IReadOnlyList<string> MissingMajorChannels);
