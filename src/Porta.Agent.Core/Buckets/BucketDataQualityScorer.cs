namespace Porta.Agent.Core.Buckets;

internal static class BucketDataQualityScorer
{
    private const double KeyboardMissingPenalty = 0.10;
    private const double MouseMissingPenalty = 0.12;
    private const double FocusMissingPenalty = 0.24;
    private const double IdleMissingPenalty = 0.18;
    private const double InteractionMissingBasePenalty = 0.18;
    private const double InteractionMissingMaxPenalty = 0.38;
    private const double InteractionMissingShortQuietSeconds = 2.0;
    private const double InteractionMissingLongQuietSeconds = 45.0;
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
        bool idleAvailable = bucket.Idle.ObservationCount > 0
            || bucket.Idle.Samples.Count > 0
            || bucket.Idle.ObservedIntervalCount > 0;

        var missing = new List<string>(4);
        double score = 1d;

        bool interactionMissing = !keyboardAvailable && !mouseAvailable;

        if (interactionMissing)
        {
            missing.Add("keyboard");
            missing.Add("mouse");
            score -= ResolveInteractionMissingPenalty(bucket, idleAvailable);
        }
        else
        {
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
        bool partial = interactionMissing || !focusAvailable || !idleAvailable;
        return new BucketDataQualityScore(clamped, partial, missing);
    }

    private static double ResolveInteractionMissingPenalty(BucketBuffer bucket, bool idleAvailable)
    {
        double quietSeconds = idleAvailable
            ? NormalizeNonNegative(bucket.Idle.QualityIdleSecondsEstimate)
            : Time.bucketSeconds;
        double progress = 0d;
        if (InteractionMissingLongQuietSeconds > InteractionMissingShortQuietSeconds)
        {
            progress = Math.Clamp(
                (quietSeconds - InteractionMissingShortQuietSeconds)
                / (InteractionMissingLongQuietSeconds - InteractionMissingShortQuietSeconds),
                0d,
                1d);
        }

        return InteractionMissingBasePenalty
            + ((InteractionMissingMaxPenalty - InteractionMissingBasePenalty) * progress);
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static double NormalizeNonNegative(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
        {
            return 0d;
        }

        return value;
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
