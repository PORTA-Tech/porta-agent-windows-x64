namespace Porta.Agent.Core.Buckets;

internal sealed class MouseRawAggregates
{
    private readonly List<MouseMoveDeltaSample> _moveSamples = [];
    private readonly List<MouseWheelDeltaSample> _wheelSamples = [];
    private readonly HashSet<MouseMoveDeltaSample> _seenMoveSamples = [];
    private readonly HashSet<MouseWheelDeltaSample> _seenWheelSamples = [];

    public int MoveSampleCount => _moveSamples.Count;
    public int WheelSampleCount => _wheelSamples.Count;
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

    public void RecordWheel(long tsUtcMs, int delta)
    {
        var sample = new MouseWheelDeltaSample(tsUtcMs, delta);
        if (!_seenWheelSamples.Add(sample))
        {
            return;
        }

        _wheelSamples.Add(sample);
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
            SpeedMeanPxPerSec = 0d;
            JerkP95 = 0d;
            MicroCorrectionRate = 0d;
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

        // TODO(porta-step-5): micro-correction definition pending (do not invent).
        MicroCorrectionRate = 0d;
    }

    private void RecomputeWheelDerivedMetrics()
    {
        if (_wheelSamples.Count < 2)
        {
            ScrollSpeed = 0d;
            ScrollBursts = 0d;
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

        // TODO(porta-step-5): scroll burst definition pending (do not invent).
        ScrollBursts = 0d;
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
}

internal readonly record struct MouseMoveDeltaSample(long TimestampUtcMs, int Dx, int Dy);
internal readonly record struct MouseWheelDeltaSample(long TimestampUtcMs, int Delta);
