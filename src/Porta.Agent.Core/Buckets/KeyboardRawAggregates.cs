namespace Porta.Agent.Core.Buckets;

internal sealed class KeyboardRawAggregates
{
    private readonly List<long> _keydownTsUtcMs = [];
    private readonly HashSet<KeyboardSampleKey> _seenKeydownSamples = [];

    public int KeydownCount { get; private set; }
    public int BackspaceCount { get; private set; }
    public IReadOnlyList<long> KeydownTsList => _keydownTsUtcMs;

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

        _keydownTsUtcMs.Add(tsUtcMs);
    }

    public void RecomputeDerivedMetrics()
    {
        BackspaceRate = KeydownCount <= 0 ? 0d : Clamp01(BackspaceCount / (double)KeydownCount);

        if (_keydownTsUtcMs.Count < 2)
        {
            IkiMeanMs = 0d;
            IkiStdMs = 0d;
            IkiCv = 0d;
            BurstRate = 0d;
            CorrectionSpikeRate = 0d;
            return;
        }

        // IKI definition (pinned):
        // IKI = ts[i] - ts[i-1] between consecutive keydowns within the bucket.
        // No max-gap cutoff is applied here.
        long[] ordered = _keydownTsUtcMs.OrderBy(static x => x).ToArray();
        double[] ikis = new double[ordered.Length - 1];
        for (int i = 1; i < ordered.Length; i++)
        {
            ikis[i - 1] = Math.Max(0d, ordered[i] - ordered[i - 1]);
        }

        double mean = ikis.Average();
        double variance = ikis.Select(v => (v - mean) * (v - mean)).Average();
        double std = Math.Sqrt(Math.Max(0d, variance));
        IkiMeanMs = mean;
        IkiStdMs = std;
        IkiCv = mean <= 0d ? 0d : std / mean;

        // TODO(porta-step-5): lock exact deterministic burst definition before enabling.
        // Placeholder by contract until definition is frozen.
        BurstRate = 0d;

        // TODO(porta-step-5): lock exact deterministic correction spike definition before enabling.
        // Placeholder by contract until definition is frozen.
        CorrectionSpikeRate = 0d;
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

internal readonly record struct KeyboardSampleKey(long TimestampUtcMs, bool IsBackspace);
