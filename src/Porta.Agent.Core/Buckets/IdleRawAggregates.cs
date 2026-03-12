namespace Porta.Agent.Core.Buckets;

internal sealed class IdleRawAggregates
{
    private const double MicroIdleMinSeconds = 3d;
    private const double MicroIdleMaxSeconds = 15d;
    private const double ShortIdleMinSeconds = 2d;
    private const double ShortIdleMaxSeconds = 30d;
    private const double TrueBreakMinSeconds = 120d;

    private readonly List<IdleSamplePoint> _samples = [];
    private readonly List<ObservedIdleInterval> _observedIntervals = [];
    private readonly HashSet<IdleSampleKey> _seenSamples = [];
    private readonly HashSet<ObservedIdleIntervalKey> _seenObservedIntervals = [];
    private bool _hasTelemetrySummary;
    private int _summaryObservationCount;
    private double _summaryEndIdleSeconds;
    private double _summaryIdleSecondsWithinWindow;
    private int _summaryMicroIdleCount;
    private double _summaryMicroIdleSeconds;
    private double _summaryBreakSeconds;
    private bool _summaryShortIdleBoundaryObserved;

    public IReadOnlyList<IdleSamplePoint> Samples => _samples;
    public int ObservedIntervalCount => _observedIntervals.Count;
    public int ObservationCount => _summaryObservationCount > 0
        ? _summaryObservationCount
        : _samples.Count + _observedIntervals.Count;
    public double QualityIdleSecondsEstimate => ResolveQualityIdleSecondsEstimate();
    public double IdleSeconds { get; private set; }
    public double IdleSecondsWithinWindow { get; private set; }
    public int MicroIdleCount { get; private set; }
    public double MicroIdleSeconds { get; private set; }
    public double BreakSeconds { get; private set; }
    public bool ShortIdleBoundaryObserved { get; private set; }

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

    public void RecordTelemetrySummary(
        int observationCount,
        double endIdleSeconds,
        double idleSecondsWithinWindow,
        int microIdleCount,
        double microIdleSeconds,
        double breakSecondsWithinWindow,
        bool shortIdleBoundaryObserved)
    {
        _hasTelemetrySummary = true;
        _summaryObservationCount += Math.Max(0, observationCount);
        _summaryEndIdleSeconds += NormalizeNonNegative(endIdleSeconds);
        _summaryIdleSecondsWithinWindow += NormalizeNonNegative(idleSecondsWithinWindow);
        _summaryMicroIdleCount += Math.Max(0, microIdleCount);
        _summaryMicroIdleSeconds += NormalizeNonNegative(microIdleSeconds);
        _summaryBreakSeconds += NormalizeNonNegative(breakSecondsWithinWindow);
        _summaryShortIdleBoundaryObserved |= shortIdleBoundaryObserved;
    }

    public void RecomputeDerivedMetrics(long bucketStartUtcMs, long bucketEndUtcMs)
    {
        IdleSeconds = 0d;
        IdleSecondsWithinWindow = 0d;
        MicroIdleCount = 0;
        MicroIdleSeconds = 0d;
        BreakSeconds = 0d;
        ShortIdleBoundaryObserved = false;

        if (bucketEndUtcMs <= bucketStartUtcMs)
        {
            return;
        }

        if (_hasTelemetrySummary)
        {
            IdleSeconds = NormalizeNonNegative(_summaryEndIdleSeconds);
            IdleSecondsWithinWindow = NormalizeNonNegative(_summaryIdleSecondsWithinWindow);
            MicroIdleCount = Math.Max(0, _summaryMicroIdleCount);
            MicroIdleSeconds = Math.Min(
                NormalizeNonNegative(_summaryMicroIdleSeconds),
                IdleSecondsWithinWindow);
            BreakSeconds = Math.Min(
                NormalizeNonNegative(_summaryBreakSeconds),
                IdleSecondsWithinWindow);
            ShortIdleBoundaryObserved = _summaryShortIdleBoundaryObserved || MicroIdleSeconds >= ShortIdleMinSeconds;
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
                if (current.IdleSeconds > 0d)
                {
                    IdleSecondsWithinWindow += overlapSeconds;
                }

                bool isMicro = IsMicro(current.IdleSeconds);
                if (isMicro)
                {
                    MicroIdleSeconds += overlapSeconds;
                }

                if (IsShortIdle(current.IdleSeconds))
                {
                    ShortIdleBoundaryObserved = true;
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

        IdleSecondsWithinWindow = NormalizeNonNegative(IdleSecondsWithinWindow);
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

            IdleSecondsWithinWindow += observedSeconds;
            if (IsMicro(observedSeconds))
            {
                MicroIdleCount++;
                MicroIdleSeconds += observedSeconds;
            }

            if (IsShortIdle(observedSeconds))
            {
                ShortIdleBoundaryObserved = true;
            }

            if (observedSeconds >= TrueBreakMinSeconds)
            {
                BreakSeconds += observedSeconds;
            }
        }

        IdleSeconds = NormalizeNonNegative(IdleSecondsWithinWindow);
        IdleSecondsWithinWindow = NormalizeNonNegative(IdleSecondsWithinWindow);
        MicroIdleSeconds = NormalizeNonNegative(MicroIdleSeconds);
        BreakSeconds = NormalizeNonNegative(BreakSeconds);
    }

    private static bool IsMicro(double idleSeconds)
    {
        return idleSeconds >= MicroIdleMinSeconds && idleSeconds <= MicroIdleMaxSeconds;
    }

    private static bool IsShortIdle(double idleSeconds)
    {
        return idleSeconds >= ShortIdleMinSeconds && idleSeconds <= ShortIdleMaxSeconds;
    }

    private double ResolveQualityIdleSecondsEstimate()
    {
        if (_hasTelemetrySummary)
        {
            return NormalizeNonNegative(Math.Max(_summaryEndIdleSeconds, _summaryIdleSecondsWithinWindow));
        }

        double sampleIdle = _samples.Count == 0
            ? 0d
            : NormalizeNonNegative(_samples.Max(static sample => sample.IdleSeconds));
        double intervalIdle = _observedIntervals.Count == 0
            ? 0d
            : NormalizeNonNegative(_observedIntervals.Max(static interval => interval.IdleSeconds));
        return NormalizeNonNegative(Math.Max(sampleIdle, intervalIdle));
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
