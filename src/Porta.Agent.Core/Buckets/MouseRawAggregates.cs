namespace Porta.Agent.Core.Buckets;

internal sealed class MouseRawAggregates
{
    private readonly List<MouseMoveDeltaSample> _moveSamples = [];
    private readonly List<MouseWheelDeltaSample> _wheelSamples = [];
    private readonly HashSet<MouseMoveDeltaSample> _seenMoveSamples = [];
    private readonly HashSet<MouseWheelDeltaSample> _seenWheelSamples = [];
    private int _summaryMoveSampleCount;
    private int _summaryWheelSampleCount;
    private int _summaryMicroCorrectionCount;
    private int _summaryScrollBurstCount;
    private double _summaryMoveWindowSeconds;
    private double _summaryWheelWindowSeconds;
    private double _summaryPathLengthPx;
    private double _summarySpeedWeightedSum;
    private double _summaryJerkWeightedSum;
    private double _summaryScrollSpeedWeightedSum;

    public int MoveSampleCount => _moveSamples.Count + _summaryMoveSampleCount;
    public int WheelSampleCount => _wheelSamples.Count + _summaryWheelSampleCount;
    public IReadOnlyList<MouseMoveDeltaSample> MoveSamples => _moveSamples;
    public IReadOnlyList<MouseWheelDeltaSample> WheelSamples => _wheelSamples;

    public double SpeedMeanPxPerSec { get; private set; }
    public double JerkP95 { get; private set; }
    public double MicroCorrectionRate { get; private set; }
    public double ScrollSpeed { get; private set; }
    public double ScrollBursts { get; private set; }

    public void RecordMove(long tsUtcMs, int dx, int dy)
    {
        var sample = new MouseMoveDeltaSample(tsUtcMs, dx, dy);
        if (!_seenMoveSamples.Add(sample))
        {
            return;
        }

        _moveSamples.Add(sample);
    }

    public void RecordAggregatedMove(long startUtcMs, long endUtcMs, int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        RecordMove(ResolveRepresentativeTimestamp(startUtcMs, endUtcMs), dx, dy);
    }

    public void RecordWheel(long tsUtcMs, int delta)
    {
        var sample = new MouseWheelDeltaSample(tsUtcMs, delta);
        if (!_seenWheelSamples.Add(sample))
        {
            return;
        }

        _wheelSamples.Add(sample);
    }

    public void RecordAggregatedWheel(long startUtcMs, long endUtcMs, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        RecordWheel(ResolveRepresentativeTimestamp(startUtcMs, endUtcMs), delta);
    }

    public void RecordTelemetrySummary(
        int moveSampleCount,
        double pathLengthPx,
        double speedMeanPxPerSec,
        double jerkP95,
        int microCorrectionCount,
        int wheelEventCount,
        int scrollBurstCount,
        double scrollSpeedMean,
        double windowSeconds)
    {
        int normalizedMoveSampleCount = Math.Max(0, moveSampleCount);
        int normalizedWheelEventCount = Math.Max(0, wheelEventCount);
        double normalizedWindowSeconds = NormalizeNonNegative(windowSeconds);

        _summaryMoveSampleCount += normalizedMoveSampleCount;
        _summaryWheelSampleCount += normalizedWheelEventCount;
        _summaryMicroCorrectionCount += Math.Max(0, microCorrectionCount);
        _summaryScrollBurstCount += Math.Max(0, scrollBurstCount);
        _summaryPathLengthPx += NormalizeNonNegative(pathLengthPx);
        _summaryMoveWindowSeconds += normalizedWindowSeconds;
        _summaryWheelWindowSeconds += normalizedWindowSeconds;
        _summarySpeedWeightedSum += NormalizeNonNegative(speedMeanPxPerSec) * Math.Max(1, normalizedMoveSampleCount);
        _summaryJerkWeightedSum += NormalizeNonNegative(jerkP95) * Math.Max(1, normalizedMoveSampleCount);
        _summaryScrollSpeedWeightedSum += NormalizeNonNegative(scrollSpeedMean) * Math.Max(1, normalizedWheelEventCount);
    }

    public void RecomputeDerivedMetrics()
    {
        RecomputeMoveDerivedMetrics();
        RecomputeWheelDerivedMetrics();
    }

    private void RecomputeMoveDerivedMetrics()
    {
        if (_moveSamples.Count < 2)
        {
            RecomputeMoveFromTelemetrySummary();
            return;
        }

        MouseMoveDeltaSample[] ordered = _moveSamples
            .OrderBy(static sample => sample.TimestampUtcMs)
            .ToArray();

        var speedSamples = new List<double>(ordered.Length - 1);
        var jerkSamples = new List<double>(ordered.Length - 2);
        double? previousSpeed = null;

        for (int i = 1; i < ordered.Length; i++)
        {
            double dtSeconds = (ordered[i].TimestampUtcMs - ordered[i - 1].TimestampUtcMs) / 1000d;
            if (dtSeconds <= 0d)
            {
                continue;
            }

            double distance = Math.Sqrt((ordered[i].Dx * (double)ordered[i].Dx) + (ordered[i].Dy * (double)ordered[i].Dy));
            double speed = distance / dtSeconds;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0d)
            {
                continue;
            }

            speedSamples.Add(speed);

            if (previousSpeed.HasValue)
            {
                double jerk = Math.Abs(speed - previousSpeed.Value) / dtSeconds;
                if (!double.IsNaN(jerk) && !double.IsInfinity(jerk) && jerk >= 0d)
                {
                    jerkSamples.Add(jerk);
                }
            }

            previousSpeed = speed;
        }

        SpeedMeanPxPerSec = speedSamples.Count == 0 ? 0d : speedSamples.Average();
        JerkP95 = PercentileNearestRank(jerkSamples, 0.95d);

        MicroCorrectionRate = ComputeMicroCorrectionRateFromSamples(ordered);
    }

    private void RecomputeWheelDerivedMetrics()
    {
        if (_wheelSamples.Count < 2)
        {
            RecomputeWheelFromTelemetrySummary();
            return;
        }

        MouseWheelDeltaSample[] ordered = _wheelSamples
            .OrderBy(static sample => sample.TimestampUtcMs)
            .ToArray();

        var scrollSpeedSamples = new List<double>(ordered.Length - 1);
        for (int i = 1; i < ordered.Length; i++)
        {
            double dtSeconds = (ordered[i].TimestampUtcMs - ordered[i - 1].TimestampUtcMs) / 1000d;
            if (dtSeconds <= 0d)
            {
                continue;
            }

            double speed = Math.Abs(ordered[i].Delta) / dtSeconds;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0d)
            {
                continue;
            }

            scrollSpeedSamples.Add(speed);
        }

        ScrollSpeed = scrollSpeedSamples.Count == 0 ? 0d : scrollSpeedSamples.Average();

        ScrollBursts = ComputeScrollBurstRateFromSamples(ordered);
    }

    private static double PercentileNearestRank(IReadOnlyCollection<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return 0d;
        }

        // Frozen percentile method: nearest-rank.
        double p = Math.Clamp(percentile, 0d, 1d);
        double[] ordered = samples.OrderBy(static value => value).ToArray();
        int rank = (int)Math.Ceiling(p * ordered.Length);
        int index = Math.Clamp(rank - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static long ResolveRepresentativeTimestamp(long startUtcMs, long endUtcMs)
    {
        long safeStartUtcMs = Math.Min(startUtcMs, endUtcMs);
        long safeEndUtcMs = Math.Max(safeStartUtcMs + 1, endUtcMs);
        long midpoint = safeStartUtcMs + ((safeEndUtcMs - safeStartUtcMs) / 2);
        return Math.Clamp(midpoint, safeStartUtcMs, safeEndUtcMs - 1);
    }

    private void RecomputeMoveFromTelemetrySummary()
    {
        if (_summaryMoveSampleCount <= 0)
        {
            SpeedMeanPxPerSec = 0d;
            JerkP95 = 0d;
            MicroCorrectionRate = 0d;
            return;
        }

        SpeedMeanPxPerSec = _summarySpeedWeightedSum / Math.Max(1, _summaryMoveSampleCount);
        JerkP95 = _summaryJerkWeightedSum / Math.Max(1, _summaryMoveSampleCount);
        MicroCorrectionRate = _summaryMoveWindowSeconds <= 0d
            ? 0d
            : Clamp01(_summaryMicroCorrectionCount / _summaryMoveWindowSeconds);
    }

    private void RecomputeWheelFromTelemetrySummary()
    {
        if (_summaryWheelSampleCount <= 0)
        {
            ScrollSpeed = 0d;
            ScrollBursts = 0d;
            return;
        }

        ScrollSpeed = _summaryScrollSpeedWeightedSum / Math.Max(1, _summaryWheelSampleCount);
        ScrollBursts = _summaryWheelWindowSeconds <= 0d
            ? 0d
            : Clamp01(_summaryScrollBurstCount / _summaryWheelWindowSeconds);
    }

    private static double ComputeMicroCorrectionRateFromSamples(IReadOnlyList<MouseMoveDeltaSample> orderedSamples)
    {
        if (orderedSamples.Count < 2)
        {
            return 0d;
        }

        int microCorrectionCount = 0;
        for (int i = 1; i < orderedSamples.Count; i++)
        {
            MouseMoveDeltaSample previous = orderedSamples[i - 1];
            MouseMoveDeltaSample current = orderedSamples[i];
            long gapMs = current.TimestampUtcMs - previous.TimestampUtcMs;
            if (gapMs <= 0 || gapMs > 250L)
            {
                continue;
            }

            if (previous.MagnitudePx > 12d || current.MagnitudePx > 12d)
            {
                continue;
            }

            double dot = (previous.Dx * (double)current.Dx) + (previous.Dy * (double)current.Dy);
            if (dot < 0d)
            {
                microCorrectionCount++;
            }
        }

        double windowSeconds = Math.Max(0.001d, (orderedSamples[^1].TimestampUtcMs - orderedSamples[0].TimestampUtcMs) / 1000d);
        return Clamp01(microCorrectionCount / windowSeconds);
    }

    private static double ComputeScrollBurstRateFromSamples(IReadOnlyList<MouseWheelDeltaSample> orderedSamples)
    {
        if (orderedSamples.Count < 2)
        {
            return 0d;
        }

        int burstCount = 0;
        int clusterLength = 1;
        for (int i = 1; i < orderedSamples.Count; i++)
        {
            long gapMs = orderedSamples[i].TimestampUtcMs - orderedSamples[i - 1].TimestampUtcMs;
            if (gapMs <= 300L)
            {
                clusterLength++;
            }
            else
            {
                if (clusterLength >= 2)
                {
                    burstCount++;
                }

                clusterLength = 1;
            }
        }

        if (clusterLength >= 2)
        {
            burstCount++;
        }

        double windowSeconds = Math.Max(0.001d, (orderedSamples[^1].TimestampUtcMs - orderedSamples[0].TimestampUtcMs) / 1000d);
        return Clamp01(burstCount / windowSeconds);
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

internal readonly record struct MouseMoveDeltaSample(long TimestampUtcMs, int Dx, int Dy)
{
    public double MagnitudePx => Math.Sqrt((Dx * (double)Dx) + (Dy * (double)Dy));
}
internal readonly record struct MouseWheelDeltaSample(long TimestampUtcMs, int Delta);
