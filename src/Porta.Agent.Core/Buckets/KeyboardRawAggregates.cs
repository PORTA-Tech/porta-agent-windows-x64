namespace Porta.Agent.Core.Buckets;

internal sealed class KeyboardRawAggregates
{
    private readonly List<KeyboardSampleKey> _keydownSamples = [];
    private readonly HashSet<KeyboardSampleKey> _seenKeydownSamples = [];
    private int _summaryBurstCount;
    private int _summaryCorrectionSpikeCount;
    private double _summaryWindowSeconds;
    private double _summaryIkiWeight;
    private double _summaryIkiWeightedMeanSum;
    private double _summaryIkiWeightedSecondMomentSum;

    public int KeydownCount { get; private set; }
    public int BackspaceCount { get; private set; }
    public IReadOnlyList<long> KeydownTsList => _keydownSamples.Select(static sample => sample.TimestampUtcMs).ToArray();

    public double IkiMeanMs { get; private set; }
    public double IkiStdMs { get; private set; }
    public double IkiCv { get; private set; }
    public double BurstRate { get; private set; }
    public double BackspaceRate { get; private set; }
    public double CorrectionSpikeRate { get; private set; }

    public void RecordKeydown(long tsUtcMs, bool isBackspace)
    {
        var sample = new KeyboardSampleKey(tsUtcMs, isBackspace);
        if (!_seenKeydownSamples.Add(sample))
        {
            return;
        }

        KeydownCount++;
        if (isBackspace)
        {
            BackspaceCount++;
        }

        _keydownSamples.Add(sample);
    }

    public void RecordAggregatedCounts(long startUtcMs, long endUtcMs, int keydownCount, int backspaceCount)
    {
        int nonBackspaceCount = Math.Max(0, keydownCount);
        int backspaceOnlyCount = Math.Max(0, backspaceCount);
        int totalCount = nonBackspaceCount + backspaceOnlyCount;
        if (totalCount <= 0)
        {
            return;
        }

        long[] timestamps = BuildSampleTimestamps(startUtcMs, endUtcMs, totalCount);
        for (int i = 0; i < nonBackspaceCount; i++)
        {
            RecordKeydown(timestamps[i], isBackspace: false);
        }

        for (int i = 0; i < backspaceOnlyCount; i++)
        {
            RecordKeydown(timestamps[nonBackspaceCount + i], isBackspace: true);
        }
    }

    public void RecordTelemetrySummary(
        int keydownCount,
        int backspaceCount,
        double ikiMeanMs,
        double ikiStdMs,
        int burstCount,
        int correctionSpikeCount,
        double windowSeconds)
    {
        int normalizedKeydownCount = Math.Max(0, keydownCount);
        int normalizedBackspaceCount = Math.Clamp(backspaceCount, 0, normalizedKeydownCount);
        int intervalCount = Math.Max(0, normalizedKeydownCount - 1);

        KeydownCount += normalizedKeydownCount;
        BackspaceCount += normalizedBackspaceCount;
        _summaryBurstCount += Math.Max(0, burstCount);
        _summaryCorrectionSpikeCount += Math.Max(0, correctionSpikeCount);
        _summaryWindowSeconds += NormalizeNonNegative(windowSeconds);

        if (intervalCount <= 0)
        {
            return;
        }

        double mean = NormalizeNonNegative(ikiMeanMs);
        double std = NormalizeNonNegative(ikiStdMs);
        double secondMoment = (std * std) + (mean * mean);
        _summaryIkiWeight += intervalCount;
        _summaryIkiWeightedMeanSum += mean * intervalCount;
        _summaryIkiWeightedSecondMomentSum += secondMoment * intervalCount;
    }

    public void RecomputeDerivedMetrics()
    {
        BackspaceRate = KeydownCount <= 0 ? 0d : Clamp01(BackspaceCount / (double)KeydownCount);

        if (_keydownSamples.Count < 2)
        {
            RecomputeFromTelemetrySummary();
            return;
        }

        // IKI definition (pinned):
        // IKI = ts[i] - ts[i-1] between consecutive keydowns within the bucket.
        // No max-gap cutoff is applied here.
        KeyboardSampleKey[] ordered = _keydownSamples
            .OrderBy(static sample => sample.TimestampUtcMs)
            .ToArray();
        double[] ikis = new double[ordered.Length - 1];
        for (int i = 1; i < ordered.Length; i++)
        {
            ikis[i - 1] = Math.Max(0d, ordered[i].TimestampUtcMs - ordered[i - 1].TimestampUtcMs);
        }

        double mean = ikis.Average();
        double variance = ikis.Select(v => (v - mean) * (v - mean)).Average();
        double std = Math.Sqrt(Math.Max(0d, variance));
        IkiMeanMs = mean;
        IkiStdMs = std;
        IkiCv = mean <= 0d ? 0d : std / mean;

        BurstRate = ComputeBurstRateFromSamples(ordered);
        CorrectionSpikeRate = ComputeCorrectionSpikeRateFromSamples(ordered);
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private void RecomputeFromTelemetrySummary()
    {
        if (KeydownCount <= 0)
        {
            IkiMeanMs = 0d;
            IkiStdMs = 0d;
            IkiCv = 0d;
            BurstRate = 0d;
            CorrectionSpikeRate = 0d;
            return;
        }

        if (_summaryIkiWeight > 0d)
        {
            double mean = _summaryIkiWeightedMeanSum / _summaryIkiWeight;
            double secondMoment = _summaryIkiWeightedSecondMomentSum / _summaryIkiWeight;
            double variance = Math.Max(0d, secondMoment - (mean * mean));
            IkiMeanMs = mean;
            IkiStdMs = Math.Sqrt(variance);
            IkiCv = mean <= 0d ? 0d : IkiStdMs / mean;
        }
        else
        {
            IkiMeanMs = 0d;
            IkiStdMs = 0d;
            IkiCv = 0d;
        }

        BurstRate = _summaryWindowSeconds <= 0d
            ? 0d
            : Clamp01(_summaryBurstCount / _summaryWindowSeconds);
        CorrectionSpikeRate = _summaryWindowSeconds <= 0d
            ? 0d
            : Clamp01(_summaryCorrectionSpikeCount / _summaryWindowSeconds);
    }

    private static double ComputeBurstRateFromSamples(IReadOnlyList<KeyboardSampleKey> orderedSamples)
    {
        if (orderedSamples.Count < 3)
        {
            return 0d;
        }

        int burstCount = 0;
        int clusterLength = 1;
        for (int i = 1; i < orderedSamples.Count; i++)
        {
            long gapMs = orderedSamples[i].TimestampUtcMs - orderedSamples[i - 1].TimestampUtcMs;
            if (gapMs <= 250L)
            {
                clusterLength++;
            }
            else
            {
                if (clusterLength >= 3)
                {
                    burstCount++;
                }

                clusterLength = 1;
            }
        }

        if (clusterLength >= 3)
        {
            burstCount++;
        }

        double windowSeconds = Math.Max(0.001d, (orderedSamples[^1].TimestampUtcMs - orderedSamples[0].TimestampUtcMs) / 1000d);
        return Clamp01(burstCount / windowSeconds);
    }

    private static double ComputeCorrectionSpikeRateFromSamples(IReadOnlyList<KeyboardSampleKey> orderedSamples)
    {
        if (orderedSamples.Count < 2)
        {
            return 0d;
        }

        int correctionSpikes = 0;
        for (int i = 1; i < orderedSamples.Count; i++)
        {
            if (orderedSamples[i].IsBackspace
                && (orderedSamples[i].TimestampUtcMs - orderedSamples[i - 1].TimestampUtcMs) <= 800L)
            {
                correctionSpikes++;
            }
        }

        double windowSeconds = Math.Max(0.001d, (orderedSamples[^1].TimestampUtcMs - orderedSamples[0].TimestampUtcMs) / 1000d);
        return Clamp01(correctionSpikes / windowSeconds);
    }

    private static double NormalizeNonNegative(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
        {
            return 0d;
        }

        return value;
    }

    private static long[] BuildSampleTimestamps(long startUtcMs, long endUtcMs, int count)
    {
        var timestamps = new long[count];
        long safeStartUtcMs = Math.Min(startUtcMs, endUtcMs);
        long safeEndUtcMs = Math.Max(safeStartUtcMs + 1, endUtcMs);
        long spanMs = Math.Max(1, safeEndUtcMs - safeStartUtcMs);

        for (int i = 0; i < count; i++)
        {
            double fraction = (i + 1d) / (count + 1d);
            long offset = (long)Math.Round(spanMs * fraction, MidpointRounding.AwayFromZero);
            long clampedOffset = Math.Clamp(offset, 0L, Math.Max(0L, spanMs - 1L));
            timestamps[i] = safeStartUtcMs + clampedOffset;
        }

        return timestamps;
    }
}

internal readonly record struct KeyboardSampleKey(long TimestampUtcMs, bool IsBackspace);
