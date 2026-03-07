namespace Porta.Agent.Core.Buckets;

internal sealed class IdleRawAggregates
{
    private const double MicroIdleMinSeconds = 3d;
    private const double MicroIdleMaxSeconds = 15d;
    private const double TrueBreakMinSeconds = 120d;

    private readonly List<IdleSamplePoint> _samples = [];
    private readonly List<ObservedIdleInterval> _observedIntervals = [];
    private readonly HashSet<IdleSampleKey> _seenSamples = [];
    private readonly HashSet<ObservedIdleIntervalKey> _seenObservedIntervals = [];

    public IReadOnlyList<IdleSamplePoint> Samples => _samples;
    public int ObservedIntervalCount => _observedIntervals.Count;
    public double IdleSeconds { get; private set; }
    public int MicroIdleCount { get; private set; }
    public double MicroIdleSeconds { get; private set; }
    public double BreakSeconds { get; private set; }

    public void RecordSample(long tsUtcMs, double idleSeconds)
    {
        double normalizedIdle = NormalizeNonNegative(idleSeconds);
        long idleMillisRounded = (long)Math.Round(normalizedIdle * 1000d, MidpointRounding.AwayFromZero);
        var key = new IdleSampleKey(tsUtcMs, idleMillisRounded);
        if (!_seenSamples.Add(key))
        {
            return;
        }

        _samples.Add(new IdleSamplePoint(tsUtcMs, normalizedIdle));
    }

    public void RecordObservedInterval(long startUtcMs, long endUtcMs, double idleSeconds)
    {
        double normalizedIdle = NormalizeNonNegative(idleSeconds);
        long safeStartUtcMs = Math.Min(startUtcMs, endUtcMs);
        long safeEndUtcMs = Math.Max(startUtcMs, endUtcMs);
        if (normalizedIdle <= 0d || safeEndUtcMs <= safeStartUtcMs)
        {
            return;
        }

        long roundedIdleMs = (long)Math.Round(normalizedIdle * 1000d, MidpointRounding.AwayFromZero);
        var key = new ObservedIdleIntervalKey(safeStartUtcMs, safeEndUtcMs, roundedIdleMs);
        if (_seenObservedIntervals.Add(key))
        {
            _observedIntervals.Add(new ObservedIdleInterval(safeStartUtcMs, safeEndUtcMs, normalizedIdle));
        }
    }

    public void RecomputeDerivedMetrics(long bucketStartUtcMs, long bucketEndUtcMs)
    {
        IdleSeconds = 0d;
        MicroIdleCount = 0;
        MicroIdleSeconds = 0d;
        BreakSeconds = 0d;

        if (bucketEndUtcMs <= bucketStartUtcMs)
        {
            return;
        }

        if (_samples.Count >= 2)
        {
            IdleSamplePoint[] ordered = _samples.OrderBy(static sample => sample.TimestampUtcMs).ToArray();

            // Frozen method: tick-based attribution from idle samples.
            // Each sample's value classifies the span from previous sample timestamp to current sample timestamp.
            bool wasMicro = false;
            for (int i = 1; i < ordered.Length; i++)
            {
                IdleSamplePoint previous = ordered[i - 1];
                IdleSamplePoint current = ordered[i];
                if (current.TimestampUtcMs <= previous.TimestampUtcMs)
                {
                    continue;
                }

                long spanStart = previous.TimestampUtcMs;
                long spanEnd = current.TimestampUtcMs;
                long overlapStart = Math.Max(spanStart, bucketStartUtcMs);
                long overlapEnd = Math.Min(spanEnd, bucketEndUtcMs);
                if (overlapEnd <= overlapStart)
                {
                    wasMicro = IsMicro(current.IdleSeconds);
                    continue;
                }

                double overlapSeconds = (overlapEnd - overlapStart) / 1000d;
                if (current.IdleSeconds >= MicroIdleMinSeconds)
                {
                    IdleSeconds += overlapSeconds;
                }

                bool isMicro = IsMicro(current.IdleSeconds);
                if (isMicro)
                {
                    MicroIdleSeconds += overlapSeconds;
                }

                // Frozen count rule:
                // micro_idle_count counts micro-idle episodes whose first micro sample starts within bucket.
                if (isMicro && !wasMicro && current.TimestampUtcMs >= bucketStartUtcMs && current.TimestampUtcMs < bucketEndUtcMs)
                {
                    MicroIdleCount++;
                }

                if (current.IdleSeconds >= TrueBreakMinSeconds)
                {
                    BreakSeconds += overlapSeconds;
                }

                wasMicro = isMicro;
            }
        }

        IdleSeconds = NormalizeNonNegative(IdleSeconds);
        MicroIdleSeconds = NormalizeNonNegative(MicroIdleSeconds);
        BreakSeconds = NormalizeNonNegative(BreakSeconds);

        foreach (ObservedIdleInterval interval in _observedIntervals)
        {
            double intervalDurationSeconds = Math.Max(0d, (interval.EndUtcMs - interval.StartUtcMs) / 1000d);
            double observedSeconds = Math.Min(interval.IdleSeconds, intervalDurationSeconds);
            if (observedSeconds <= 0d)
            {
                continue;
            }

            IdleSeconds += observedSeconds;
            if (IsMicro(observedSeconds))
            {
                MicroIdleCount++;
                MicroIdleSeconds += observedSeconds;
            }

            if (observedSeconds >= TrueBreakMinSeconds)
            {
                BreakSeconds += observedSeconds;
            }
        }

        IdleSeconds = NormalizeNonNegative(IdleSeconds);
        MicroIdleSeconds = NormalizeNonNegative(MicroIdleSeconds);
        BreakSeconds = NormalizeNonNegative(BreakSeconds);
    }

    private static bool IsMicro(double idleSeconds)
    {
        return idleSeconds >= MicroIdleMinSeconds && idleSeconds <= MicroIdleMaxSeconds;
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

internal readonly record struct IdleSamplePoint(long TimestampUtcMs, double IdleSeconds);
internal readonly record struct IdleSampleKey(long TimestampUtcMs, long IdleMillisRounded);
internal readonly record struct ObservedIdleInterval(long StartUtcMs, long EndUtcMs, double IdleSeconds);
internal readonly record struct ObservedIdleIntervalKey(long StartUtcMs, long EndUtcMs, long IdleMillisRounded);
