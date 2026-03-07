namespace Porta.Agent.Core.Buckets;

internal sealed class FocusRawAggregates
{
    private const long MinFocusIntervalMs = 800;
    private const long FlapCollapseMaxMs = 2000;
    private const long SameAppStormDedupeWindowMs = 250;
    private readonly List<FocusEventSample> _focusEvents = [];
    private readonly List<FocusInterval> _observedIntervals = [];
    private readonly Dictionary<string, double> _dwellByAppSeconds = new(StringComparer.Ordinal);
    private readonly HashSet<FocusEventSample> _seenSamples = [];
    private readonly HashSet<FocusInterval> _seenObservedIntervals = [];
    private FocusEventSample? _lastAcceptedSample;

    public IReadOnlyList<FocusEventSample> FocusEvents => _focusEvents;
    public int ObservedIntervalCount => _observedIntervals.Count;
    public IReadOnlyDictionary<string, double> DwellByAppSeconds => _dwellByAppSeconds;
    public int FocusSwitchesCount { get; private set; }
    public int ActiveAppCount { get; private set; }
    public double AppEntropy { get; private set; }
    public string DominantAppHash { get; private set; } = string.Empty;

    public void RecordFocusEvent(long tsUtcMs, string appHash)
    {
        string normalized = string.IsNullOrWhiteSpace(appHash) ? "unknown" : appHash.Trim().ToLowerInvariant();
        var sample = new FocusEventSample(tsUtcMs, normalized);
        if (!_seenSamples.Add(sample))
        {
            return;
        }

        if (_lastAcceptedSample.HasValue
            && string.Equals(_lastAcceptedSample.Value.AppHash, sample.AppHash, StringComparison.Ordinal))
        {
            long deltaMs = sample.TimestampUtcMs - _lastAcceptedSample.Value.TimestampUtcMs;
            if (deltaMs >= 0 && deltaMs <= SameAppStormDedupeWindowMs)
            {
                return;
            }
        }

        _focusEvents.Add(sample);
        _lastAcceptedSample = sample;
    }

    public void RecordObservedInterval(long startUtcMs, long endUtcMs, string appHash)
    {
        string normalized = string.IsNullOrWhiteSpace(appHash) ? "unknown" : appHash.Trim().ToLowerInvariant();
        long safeStartUtcMs = Math.Min(startUtcMs, endUtcMs);
        long safeEndUtcMs = Math.Max(startUtcMs, endUtcMs);
        if (safeEndUtcMs <= safeStartUtcMs)
        {
            return;
        }

        var interval = new FocusInterval(normalized, safeStartUtcMs, safeEndUtcMs);
        if (_seenObservedIntervals.Add(interval))
        {
            _observedIntervals.Add(interval);
        }
    }

    public void RecomputeDerivedMetrics(long bucketStartUtcMs, long bucketEndUtcMs)
    {
        FocusSwitchesCount = 0;
        ActiveAppCount = 0;
        AppEntropy = 0d;
        DominantAppHash = string.Empty;
        _dwellByAppSeconds.Clear();

        if (bucketEndUtcMs <= bucketStartUtcMs || (_focusEvents.Count == 0 && _observedIntervals.Count == 0))
        {
            return;
        }

        FocusEventSample[] ordered = _focusEvents
            .OrderBy(static e => e.TimestampUtcMs)
            .ToArray();
        List<FocusInterval> rawIntervals = ordered.Length == 0
            ? []
            : BuildRawIntervals(ordered, bucketEndUtcMs);
        List<FocusInterval> combinedIntervals = rawIntervals
            .Concat(_observedIntervals)
            .ToList();
        List<FocusInterval> minDurationIntervals = combinedIntervals
            .Where(static interval => interval.DurationMs >= MinFocusIntervalMs)
            .OrderBy(static interval => interval.StartUtcMs)
            .ThenBy(static interval => interval.EndUtcMs)
            .ThenBy(static interval => interval.AppHash, StringComparer.Ordinal)
            .ToList();
        List<FocusInterval> suppressed = CollapseFlaps(minDurationIntervals);

        AggregateOverlapByBucket(suppressed, bucketStartUtcMs, bucketEndUtcMs);
        FocusSwitchesCount = CountBucketSwitches(suppressed, bucketStartUtcMs, bucketEndUtcMs);
        ActiveAppCount = _dwellByAppSeconds.Count;
        AppEntropy = ComputeShannonEntropy(_dwellByAppSeconds.Values);
        DominantAppHash = ResolveDominantAppHash(_dwellByAppSeconds);
    }

    private static List<FocusInterval> BuildRawIntervals(IReadOnlyList<FocusEventSample> ordered, long closeOpenAtUtcMs)
    {
        var result = new List<FocusInterval>(ordered.Count);
        if (ordered.Count == 0)
        {
            return result;
        }

        for (int i = 1; i < ordered.Count; i++)
        {
            FocusEventSample previous = ordered[i - 1];
            FocusEventSample current = ordered[i];
            if (current.TimestampUtcMs <= previous.TimestampUtcMs)
            {
                continue;
            }

            result.Add(new FocusInterval(previous.AppHash, previous.TimestampUtcMs, current.TimestampUtcMs));
        }

        FocusEventSample last = ordered[^1];
        if (closeOpenAtUtcMs > last.TimestampUtcMs)
        {
            result.Add(new FocusInterval(last.AppHash, last.TimestampUtcMs, closeOpenAtUtcMs));
        }

        return result;
    }

    private static List<FocusInterval> CollapseFlaps(IReadOnlyList<FocusInterval> intervals)
    {
        var collapsed = new List<FocusInterval>(intervals.Count);
        foreach (FocusInterval interval in intervals)
        {
            collapsed.Add(interval);
            while (collapsed.Count >= 3)
            {
                FocusInterval a = collapsed[^3];
                FocusInterval b = collapsed[^2];
                FocusInterval c = collapsed[^1];
                bool isFlap = string.Equals(a.AppHash, c.AppHash, StringComparison.Ordinal)
                    && !string.Equals(a.AppHash, b.AppHash, StringComparison.Ordinal)
                    && b.DurationMs < FlapCollapseMaxMs;
                if (!isFlap)
                {
                    break;
                }

                var merged = new FocusInterval(a.AppHash, a.StartUtcMs, c.EndUtcMs);
                collapsed.RemoveAt(collapsed.Count - 1);
                collapsed.RemoveAt(collapsed.Count - 1);
                collapsed.RemoveAt(collapsed.Count - 1);
                collapsed.Add(merged);
            }
        }

        return collapsed;
    }

    private void AggregateOverlapByBucket(IReadOnlyList<FocusInterval> intervals, long bucketStartUtcMs, long bucketEndUtcMs)
    {
        foreach (FocusInterval interval in intervals)
        {
            long overlapStart = Math.Max(interval.StartUtcMs, bucketStartUtcMs);
            long overlapEnd = Math.Min(interval.EndUtcMs, bucketEndUtcMs);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            double overlapSeconds = (overlapEnd - overlapStart) / 1000d;
            if (_dwellByAppSeconds.TryGetValue(interval.AppHash, out double existing))
            {
                _dwellByAppSeconds[interval.AppHash] = existing + overlapSeconds;
            }
            else
            {
                _dwellByAppSeconds[interval.AppHash] = overlapSeconds;
            }
        }
    }

    private static int CountBucketSwitches(IReadOnlyList<FocusInterval> intervals, long bucketStartUtcMs, long bucketEndUtcMs)
    {
        if (intervals.Count < 2)
        {
            return 0;
        }

        int switches = 0;
        for (int i = 1; i < intervals.Count; i++)
        {
            FocusInterval previous = intervals[i - 1];
            FocusInterval current = intervals[i];
            long switchAt = current.StartUtcMs;
            if (switchAt < bucketStartUtcMs || switchAt >= bucketEndUtcMs)
            {
                continue;
            }

            if (!string.Equals(previous.AppHash, current.AppHash, StringComparison.Ordinal))
            {
                switches++;
            }
        }

        return switches;
    }

    private static double ComputeShannonEntropy(IEnumerable<double> dwellSeconds)
    {
        double[] values = dwellSeconds.Where(static v => v > 0d).ToArray();
        if (values.Length == 0)
        {
            return 0d;
        }

        double total = values.Sum();
        if (total <= 0d)
        {
            return 0d;
        }

        double entropy = 0d;
        foreach (double dwell in values)
        {
            double p = dwell / total;
            entropy -= p * Math.Log(p);
        }

        return double.IsFinite(entropy) ? entropy : 0d;
    }

    private static string ResolveDominantAppHash(IReadOnlyDictionary<string, double> dwellByAppSeconds)
    {
        if (dwellByAppSeconds.Count == 0)
        {
            return string.Empty;
        }

        // Deterministic tie-break: max dwell, then lexical app hash.
        return dwellByAppSeconds
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key)
            .FirstOrDefault() ?? string.Empty;
    }
}

internal readonly record struct FocusEventSample(long TimestampUtcMs, string AppHash);
internal readonly record struct FocusInterval(string AppHash, long StartUtcMs, long EndUtcMs)
{
    public long DurationMs => Math.Max(0, EndUtcMs - StartUtcMs);
}
