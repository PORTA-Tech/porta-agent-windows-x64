namespace Porta.UserAgent.Input;

internal readonly record struct UserSessionSignalSnapshot(
    KeyboardSignalSample[] KeyboardSamples,
    MouseMoveSignalSample[] MouseMoveSamples,
    MouseWheelSignalSample[] MouseWheelSamples,
    FocusObservationSample[] FocusObservations,
    IdleObservationSample[] IdleObservations,
    DateTimeOffset? LastInputEventUtc)
{
    private const double KeyboardBurstGapMs = 250d;
    private const int KeyboardBurstMinKeys = 3;
    private const double KeyboardCorrectionSpikeWindowMs = 800d;
    private const double MouseMicroCorrectionMagnitudeMaxPx = 12d;
    private const double MouseMicroCorrectionGapMs = 250d;
    private const double MouseScrollBurstGapMs = 300d;
    private const int MouseScrollBurstMinEvents = 2;
    private const double ShortIdleMinSeconds = 2d;
    private const double ShortIdleMaxSeconds = 30d;
    private const double MicroIdleMinSeconds = 3d;
    private const double MicroIdleMaxSeconds = 15d;
    private const double BreakIdleMinSeconds = 120d;

    public int KeyboardKeydownCount => KeyboardSamples.Length;

    public int KeyboardBackspaceCount => KeyboardSamples.Count(static sample => sample.IsBackspace);

    public int MouseWheelDelta => MouseWheelSamples.Sum(static sample => sample.Delta);

    public int MouseWheelEventCount => MouseWheelSamples.Length;

    public double MouseMoveDx => MouseMoveSamples.Sum(static sample => sample.Dx);

    public double MouseMoveDy => MouseMoveSamples.Sum(static sample => sample.Dy);

    public int MouseMoveSampleCount => MouseMoveSamples.Length;

    public UserSessionKeyboardTelemetrySummary BuildKeyboardSummary(double windowSeconds)
    {
        if (KeyboardSamples.Length == 0)
        {
            return new UserSessionKeyboardTelemetrySummary(
                IkiMeanMs: 0d,
                IkiStdMs: 0d,
                IkiCv: 0d,
                BurstCount: 0,
                BurstRate: 0d,
                CorrectionSpikeCount: 0,
                CorrectionSpikeRate: 0d);
        }

        KeyboardSignalSample[] ordered = KeyboardSamples
            .OrderBy(static sample => sample.TimestampUtcMs)
            .ToArray();
        double[] ikis = ordered.Length < 2
            ? []
            : ordered
                .Zip(ordered.Skip(1), static (previous, current) => Math.Max(0d, current.TimestampUtcMs - previous.TimestampUtcMs))
                .ToArray();

        double ikiMeanMs = ikis.Length == 0 ? 0d : ikis.Average();
        double ikiVariance = ikis.Length == 0
            ? 0d
            : ikis.Select(interval => (interval - ikiMeanMs) * (interval - ikiMeanMs)).Average();
        double ikiStdMs = Math.Sqrt(Math.Max(0d, ikiVariance));
        double ikiCv = ikiMeanMs <= 0d ? 0d : ikiStdMs / ikiMeanMs;
        int burstCount = CountClusters(
            ordered.Select(static sample => sample.TimestampUtcMs).ToArray(),
            KeyboardBurstGapMs,
            KeyboardBurstMinKeys);
        int correctionSpikeCount = CountCorrectionSpikes(ordered);

        return new UserSessionKeyboardTelemetrySummary(
            IkiMeanMs: ikiMeanMs,
            IkiStdMs: ikiStdMs,
            IkiCv: ikiCv,
            BurstCount: burstCount,
            BurstRate: ComputeRate(burstCount, windowSeconds),
            CorrectionSpikeCount: correctionSpikeCount,
            CorrectionSpikeRate: ComputeRate(correctionSpikeCount, windowSeconds));
    }

    public UserSessionMouseTelemetrySummary BuildMouseSummary(double windowSeconds)
    {
        MouseMoveSignalSample[] orderedMoves = MouseMoveSamples
            .OrderBy(static sample => sample.TimestampUtcMs)
            .ToArray();
        MouseWheelSignalSample[] orderedWheel = MouseWheelSamples
            .OrderBy(static sample => sample.TimestampUtcMs)
            .ToArray();

        double pathLengthPx = orderedMoves.Sum(static sample => sample.MagnitudePx);
        (double speedMeanPxPerSec, double jerkP95) = ComputeMoveSpeedAndJerk(orderedMoves);
        int microCorrectionCount = CountMicroCorrections(orderedMoves);
        int scrollBurstCount = CountClusters(
            orderedWheel.Select(static sample => sample.TimestampUtcMs).ToArray(),
            MouseScrollBurstGapMs,
            MouseScrollBurstMinEvents);
        double scrollSpeedMean = ComputeScrollSpeedMean(orderedWheel);

        return new UserSessionMouseTelemetrySummary(
            MoveSampleCount: orderedMoves.Length,
            PathLengthPx: pathLengthPx,
            SpeedMeanPxPerSec: speedMeanPxPerSec,
            JerkP95: jerkP95,
            MicroCorrectionCount: microCorrectionCount,
            MicroCorrectionRate: ComputeRate(microCorrectionCount, windowSeconds),
            WheelEventCount: orderedWheel.Length,
            ScrollBurstCount: scrollBurstCount,
            ScrollBurstRate: ComputeRate(scrollBurstCount, windowSeconds),
            ScrollSpeedMean: scrollSpeedMean);
    }

    public UserSessionFocusTelemetrySummary BuildFocusSummary(
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        string? fallbackAppHash)
    {
        long windowStartUtcMs = windowStartUtc.ToUnixTimeMilliseconds();
        long windowEndUtcMs = windowEndUtc.ToUnixTimeMilliseconds();
        if (windowEndUtcMs <= windowStartUtcMs)
        {
            return new UserSessionFocusTelemetrySummary(
                ObservationCount: 0,
                SwitchCount: 0,
                ActiveAppCount: 0,
                AppEntropy: 0d,
                DominantAppHash: NormalizeAppHash(fallbackAppHash));
        }

        FocusObservationSample[] observations = FocusObservations
            .OrderBy(static sample => sample.TimestampUtcMs)
            .ToArray();
        if (observations.Length == 0 && !string.IsNullOrWhiteSpace(fallbackAppHash))
        {
            observations =
            [
                new FocusObservationSample(windowEndUtcMs, NormalizeAppHash(fallbackAppHash))
            ];
        }

        var dwellByAppSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        int switchCount = 0;

        for (int i = 0; i < observations.Length; i++)
        {
            FocusObservationSample current = observations[i];
            long startUtcMs = Math.Clamp(current.TimestampUtcMs, windowStartUtcMs, windowEndUtcMs);
            long endUtcMs = i == observations.Length - 1
                ? windowEndUtcMs
                : Math.Clamp(observations[i + 1].TimestampUtcMs, windowStartUtcMs, windowEndUtcMs);
            if (endUtcMs <= startUtcMs)
            {
                continue;
            }

            string? appHash = NormalizeAppHash(current.AppHash);
            if (!string.IsNullOrWhiteSpace(appHash))
            {
                double dwellSeconds = (endUtcMs - startUtcMs) / 1000d;
                if (dwellByAppSeconds.TryGetValue(appHash, out double existing))
                {
                    dwellByAppSeconds[appHash] = existing + dwellSeconds;
                }
                else
                {
                    dwellByAppSeconds[appHash] = dwellSeconds;
                }
            }

            if (i > 0
                && !string.Equals(NormalizeAppHash(observations[i - 1].AppHash), appHash, StringComparison.Ordinal))
            {
                switchCount++;
            }
        }

        string? dominantAppHash = dwellByAppSeconds.Count == 0
            ? NormalizeAppHash(fallbackAppHash)
            : dwellByAppSeconds
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .First().Key;

        return new UserSessionFocusTelemetrySummary(
            ObservationCount: observations.Length,
            SwitchCount: switchCount,
            ActiveAppCount: dwellByAppSeconds.Count,
            AppEntropy: ComputeEntropy(dwellByAppSeconds.Values),
            DominantAppHash: dominantAppHash);
    }

    public UserSessionIdleTelemetrySummary BuildIdleSummary(
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        double fallbackEndIdleSeconds)
    {
        long windowStartUtcMs = windowStartUtc.ToUnixTimeMilliseconds();
        long windowEndUtcMs = windowEndUtc.ToUnixTimeMilliseconds();
        if (windowEndUtcMs <= windowStartUtcMs)
        {
            return new UserSessionIdleTelemetrySummary(
                ObservationCount: 0,
                IdleSecondsWithinWindow: 0d,
                MicroIdleCount: 0,
                MicroIdleSeconds: 0d,
                BreakSecondsWithinWindow: 0d,
                ShortIdleBoundaryObserved: false);
        }

        var intervals = new List<(long StartUtcMs, long EndUtcMs)>();
        foreach (IdleObservationSample observation in IdleObservations.OrderBy(static sample => sample.TimestampUtcMs))
        {
            if (observation.IdleSeconds <= 0d)
            {
                continue;
            }

            long endUtcMs = Math.Clamp(observation.TimestampUtcMs, windowStartUtcMs, windowEndUtcMs);
            long startUtcMs = Math.Max(windowStartUtcMs, endUtcMs - SecondsToMilliseconds(observation.IdleSeconds));
            if (endUtcMs > startUtcMs)
            {
                intervals.Add((startUtcMs, endUtcMs));
            }
        }

        if (intervals.Count == 0 && fallbackEndIdleSeconds > 0d)
        {
            long endUtcMs = windowEndUtcMs;
            long startUtcMs = Math.Max(windowStartUtcMs, endUtcMs - SecondsToMilliseconds(fallbackEndIdleSeconds));
            if (endUtcMs > startUtcMs)
            {
                intervals.Add((startUtcMs, endUtcMs));
            }
        }

        List<(long StartUtcMs, long EndUtcMs)> merged = MergeIntervals(intervals);
        double idleSecondsWithinWindow = merged.Sum(static interval => (interval.EndUtcMs - interval.StartUtcMs) / 1000d);
        int microIdleCount = 0;
        double microIdleSeconds = 0d;
        double breakSecondsWithinWindow = 0d;
        bool shortIdleBoundaryObserved = false;

        foreach ((long StartUtcMs, long EndUtcMs) interval in merged)
        {
            double intervalSeconds = (interval.EndUtcMs - interval.StartUtcMs) / 1000d;
            if (intervalSeconds >= ShortIdleMinSeconds && intervalSeconds <= ShortIdleMaxSeconds)
            {
                shortIdleBoundaryObserved = true;
            }

            if (intervalSeconds >= MicroIdleMinSeconds && intervalSeconds <= MicroIdleMaxSeconds)
            {
                microIdleCount++;
                microIdleSeconds += intervalSeconds;
            }

            if (intervalSeconds >= BreakIdleMinSeconds)
            {
                breakSecondsWithinWindow += intervalSeconds;
            }
        }

        return new UserSessionIdleTelemetrySummary(
            ObservationCount: IdleObservations.Length,
            IdleSecondsWithinWindow: idleSecondsWithinWindow,
            MicroIdleCount: microIdleCount,
            MicroIdleSeconds: microIdleSeconds,
            BreakSecondsWithinWindow: breakSecondsWithinWindow,
            ShortIdleBoundaryObserved: shortIdleBoundaryObserved);
    }

    private static (double SpeedMeanPxPerSec, double JerkP95) ComputeMoveSpeedAndJerk(IReadOnlyList<MouseMoveSignalSample> samples)
    {
        if (samples.Count < 2)
        {
            return (0d, 0d);
        }

        var speeds = new List<double>(samples.Count - 1);
        var jerks = new List<double>(Math.Max(0, samples.Count - 2));
        double? previousSpeed = null;

        for (int i = 1; i < samples.Count; i++)
        {
            double dtSeconds = Math.Max(0d, (samples[i].TimestampUtcMs - samples[i - 1].TimestampUtcMs) / 1000d);
            if (dtSeconds <= 0d)
            {
                continue;
            }

            double speed = samples[i].MagnitudePx / dtSeconds;
            speeds.Add(speed);

            if (previousSpeed.HasValue)
            {
                jerks.Add(Math.Abs(speed - previousSpeed.Value) / dtSeconds);
            }

            previousSpeed = speed;
        }

        return (
            SpeedMeanPxPerSec: speeds.Count == 0 ? 0d : speeds.Average(),
            JerkP95: PercentileNearestRank(jerks, 0.95d));
    }

    private static int CountMicroCorrections(IReadOnlyList<MouseMoveSignalSample> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        int count = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            MouseMoveSignalSample previous = samples[i - 1];
            MouseMoveSignalSample current = samples[i];
            double dtMs = current.TimestampUtcMs - previous.TimestampUtcMs;
            if (dtMs <= 0d || dtMs > MouseMicroCorrectionGapMs)
            {
                continue;
            }

            if (previous.MagnitudePx > MouseMicroCorrectionMagnitudeMaxPx
                || current.MagnitudePx > MouseMicroCorrectionMagnitudeMaxPx)
            {
                continue;
            }

            double dot = (previous.Dx * (double)current.Dx) + (previous.Dy * (double)current.Dy);
            if (dot < 0d)
            {
                count++;
            }
        }

        return count;
    }

    private static double ComputeScrollSpeedMean(IReadOnlyList<MouseWheelSignalSample> samples)
    {
        if (samples.Count < 2)
        {
            return 0d;
        }

        var speeds = new List<double>(samples.Count - 1);
        for (int i = 1; i < samples.Count; i++)
        {
            double dtSeconds = Math.Max(0d, (samples[i].TimestampUtcMs - samples[i - 1].TimestampUtcMs) / 1000d);
            if (dtSeconds <= 0d)
            {
                continue;
            }

            speeds.Add(Math.Abs(samples[i].Delta) / dtSeconds);
        }

        return speeds.Count == 0 ? 0d : speeds.Average();
    }

    private static int CountClusters(long[] orderedTimestampsUtcMs, double maxGapMs, int minEventsPerCluster)
    {
        if (orderedTimestampsUtcMs.Length == 0)
        {
            return 0;
        }

        int clusters = 0;
        int clusterLength = 1;
        for (int i = 1; i < orderedTimestampsUtcMs.Length; i++)
        {
            double gapMs = orderedTimestampsUtcMs[i] - orderedTimestampsUtcMs[i - 1];
            if (gapMs <= maxGapMs)
            {
                clusterLength++;
            }
            else
            {
                if (clusterLength >= minEventsPerCluster)
                {
                    clusters++;
                }

                clusterLength = 1;
            }
        }

        if (clusterLength >= minEventsPerCluster)
        {
            clusters++;
        }

        return clusters;
    }

    private static int CountCorrectionSpikes(IReadOnlyList<KeyboardSignalSample> orderedSamples)
    {
        int count = 0;
        for (int i = 1; i < orderedSamples.Count; i++)
        {
            KeyboardSignalSample current = orderedSamples[i];
            if (!current.IsBackspace)
            {
                continue;
            }

            double gapMs = current.TimestampUtcMs - orderedSamples[i - 1].TimestampUtcMs;
            if (gapMs >= 0d && gapMs <= KeyboardCorrectionSpikeWindowMs)
            {
                count++;
            }
        }

        return count;
    }

    private static List<(long StartUtcMs, long EndUtcMs)> MergeIntervals(IReadOnlyList<(long StartUtcMs, long EndUtcMs)> intervals)
    {
        if (intervals.Count == 0)
        {
            return [];
        }

        List<(long StartUtcMs, long EndUtcMs)> ordered = intervals
            .OrderBy(static interval => interval.StartUtcMs)
            .ThenBy(static interval => interval.EndUtcMs)
            .ToList();
        var merged = new List<(long StartUtcMs, long EndUtcMs)>(ordered.Count)
        {
            ordered[0]
        };

        for (int i = 1; i < ordered.Count; i++)
        {
            (long StartUtcMs, long EndUtcMs) current = ordered[i];
            (long StartUtcMs, long EndUtcMs) last = merged[^1];
            if (current.StartUtcMs <= last.EndUtcMs)
            {
                merged[^1] = (last.StartUtcMs, Math.Max(last.EndUtcMs, current.EndUtcMs));
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static double PercentileNearestRank(IReadOnlyCollection<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return 0d;
        }

        double[] ordered = samples.OrderBy(static value => value).ToArray();
        int rank = (int)Math.Ceiling(Math.Clamp(percentile, 0d, 1d) * ordered.Length);
        int index = Math.Clamp(rank - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static double ComputeEntropy(IEnumerable<double> dwellSeconds)
    {
        double[] values = dwellSeconds.Where(static value => value > 0d).ToArray();
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
        foreach (double value in values)
        {
            double probability = value / total;
            entropy -= probability * Math.Log(probability);
        }

        return double.IsFinite(entropy) ? entropy : 0d;
    }

    private static string? NormalizeAppHash(string? appHash)
    {
        return string.IsNullOrWhiteSpace(appHash)
            ? null
            : appHash.Trim().ToLowerInvariant();
    }

    private static long SecondsToMilliseconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0d)
        {
            return 0L;
        }

        return (long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero);
    }

    private static double ComputeRate(int count, double windowSeconds)
    {
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0d || count <= 0)
        {
            return 0d;
        }

        return count / windowSeconds;
    }
}

internal readonly record struct KeyboardSignalSample(long TimestampUtcMs, bool IsBackspace);

internal readonly record struct MouseMoveSignalSample(long TimestampUtcMs, int Dx, int Dy)
{
    public double MagnitudePx => Math.Sqrt((Dx * (double)Dx) + (Dy * (double)Dy));
}

internal readonly record struct MouseWheelSignalSample(long TimestampUtcMs, int Delta);

internal readonly record struct FocusObservationSample(long TimestampUtcMs, string? AppHash);

internal readonly record struct IdleObservationSample(long TimestampUtcMs, double IdleSeconds);

internal readonly record struct UserSessionKeyboardTelemetrySummary(
    double IkiMeanMs,
    double IkiStdMs,
    double IkiCv,
    int BurstCount,
    double BurstRate,
    int CorrectionSpikeCount,
    double CorrectionSpikeRate);

internal readonly record struct UserSessionMouseTelemetrySummary(
    int MoveSampleCount,
    double PathLengthPx,
    double SpeedMeanPxPerSec,
    double JerkP95,
    int MicroCorrectionCount,
    double MicroCorrectionRate,
    int WheelEventCount,
    int ScrollBurstCount,
    double ScrollBurstRate,
    double ScrollSpeedMean);

internal readonly record struct UserSessionFocusTelemetrySummary(
    int ObservationCount,
    int SwitchCount,
    int ActiveAppCount,
    double AppEntropy,
    string? DominantAppHash);

internal readonly record struct UserSessionIdleTelemetrySummary(
    int ObservationCount,
    double IdleSecondsWithinWindow,
    int MicroIdleCount,
    double MicroIdleSeconds,
    double BreakSecondsWithinWindow,
    bool ShortIdleBoundaryObserved);
