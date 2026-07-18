namespace Porta.Agent.LocalApi;

/// <summary>
/// Single source of truth for the day's focus numbers. "Focused" time is focus-episode time —
/// the sum of contiguous runs of buckets with an active focus episode — matching the Control
/// Center History episode view. The strength-weighted sum is never shown as time; it only feeds
/// the focus-quality percentage (weighted ÷ episode). Every surface (check-in card, widget,
/// digest, Control Center) must read these two values from here so they can never disagree.
/// </summary>
public static class LocalFocusEpisodeMetrics
{
    /// <summary>A bucket belongs to a focus episode when its FocusEpisodeSeconds exceeds this.</summary>
    public const double ActiveEpisodeThresholdSeconds = 0.5d;

    /// <summary>Gap tolerance between buckets of the same episode run (mirrors the Control Center episode builder).</summary>
    public const double ContiguityGraceSeconds = 60d;

    /// <summary>
    /// Episode-based focused seconds: runs of snapshots with an active focus episode, where a run
    /// spans first-bucket start to last-bucket end and tolerates gaps up to bucket + grace. This
    /// replicates HistoryTimelineEpisodeBuilder's focus-episode segmentation exactly so the sum
    /// equals the Control Center's episode total.
    /// </summary>
    public static double ComputeEpisodeFocusSeconds(
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        double bucketSeconds)
    {
        if (snapshots == null || snapshots.Count == 0)
        {
            return 0d;
        }

        double bucket = Math.Max(1d, bucketSeconds);
        double totalSeconds = 0d;
        DateTimeOffset? runStartUtc = null;
        DateTimeOffset lastActiveUtc = default;

        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots.OrderBy(static s => s.TimestampUtc))
        {
            bool active = snapshot.FocusEpisodeSeconds > ActiveEpisodeThresholdSeconds;
            if (!active)
            {
                if (runStartUtc.HasValue)
                {
                    totalSeconds += RunSeconds(runStartUtc.Value, lastActiveUtc, bucket);
                    runStartUtc = null;
                }

                continue;
            }

            if (runStartUtc.HasValue
                && (snapshot.TimestampUtc - lastActiveUtc).TotalSeconds > bucket + ContiguityGraceSeconds)
            {
                totalSeconds += RunSeconds(runStartUtc.Value, lastActiveUtc, bucket);
                runStartUtc = null;
            }

            runStartUtc ??= snapshot.TimestampUtc;
            lastActiveUtc = snapshot.TimestampUtc;
        }

        if (runStartUtc.HasValue)
        {
            totalSeconds += RunSeconds(runStartUtc.Value, lastActiveUtc, bucket);
        }

        return totalSeconds;
    }

    /// <summary>
    /// Focus quality % = strength-weighted focus seconds ÷ episode focus seconds × 100, clamped
    /// to [0, 100]. Zero when there is no episode time (no denominator, nothing to grade).
    /// </summary>
    public static double ComputeFocusQualityPercent(double weightedFocusSeconds, double episodeFocusSeconds)
    {
        if (episodeFocusSeconds <= 0d || weightedFocusSeconds <= 0d)
        {
            return 0d;
        }

        return Math.Clamp(weightedFocusSeconds / episodeFocusSeconds * 100d, 0d, 100d);
    }

    private static double RunSeconds(DateTimeOffset runStartUtc, DateTimeOffset lastActiveUtc, double bucketSeconds) =>
        Math.Max(bucketSeconds, (lastActiveUtc.AddSeconds(bucketSeconds) - runStartUtc).TotalSeconds);
}
