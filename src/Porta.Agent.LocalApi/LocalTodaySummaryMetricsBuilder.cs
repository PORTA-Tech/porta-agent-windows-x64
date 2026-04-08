namespace Porta.Agent.LocalApi;

public sealed record LocalTodaySummaryMetrics(
    bool HasProcessedTimeline,
    double FocusedTodaySeconds,
    double DistractionTodaySeconds,
    double AwayTodaySeconds,
    double ContextExcursionTodaySeconds,
    double TrackedTodaySeconds,
    double BestFocusBlockSeconds,
    double AverageFocusPercent,
    string MostDistractedHourLabel);

public static class LocalTodaySummaryMetricsBuilder
{
    public const string NotAvailableText = "Not available";

    public static LocalTodaySummaryMetrics Build(LocalApiTimelineResponse? timeline)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline?.Snapshots ?? [];
        if (timeline == null || snapshots.Count == 0)
        {
            return new LocalTodaySummaryMetrics(
                HasProcessedTimeline: false,
                FocusedTodaySeconds: 0d,
                DistractionTodaySeconds: 0d,
                AwayTodaySeconds: 0d,
                ContextExcursionTodaySeconds: 0d,
                TrackedTodaySeconds: 0d,
                BestFocusBlockSeconds: 0d,
                AverageFocusPercent: 0d,
                MostDistractedHourLabel: NotAvailableText);
        }

        double bucketSeconds = Math.Max(1d, timeline.BucketSeconds);
        double focusedTodaySeconds = 0d;
        double distractionTodaySeconds = 0d;
        double awayTodaySeconds = 0d;
        double contextExcursionTodaySeconds = 0d;
        double trackedTodaySeconds = 0d;
        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            double focusStrength = Math.Clamp(snapshot.FocusStrength, 0d, 1d);
            focusedTodaySeconds += bucketSeconds * focusStrength;
            trackedTodaySeconds += bucketSeconds;

            if (LocalAttentionSegmentAnalytics.IsDistractionEpisode(snapshot))
            {
                distractionTodaySeconds += bucketSeconds;
            }
            else if (LocalAttentionSegmentAnalytics.IsAwaySegment(snapshot))
            {
                awayTodaySeconds += bucketSeconds;
            }
            else if (LocalAttentionSegmentAnalytics.IsContextExcursionSegment(snapshot))
            {
                contextExcursionTodaySeconds += bucketSeconds;
            }
        }

        double bestFocusBlockSeconds = snapshots.Max(static snapshot => Math.Max(0d, snapshot.SustainedAttentionDurationSec));
        double averageFocusPercent = snapshots.Average(static snapshot => Math.Clamp(snapshot.FocusStrength, 0d, 1d)) * 100d;
        string mostDistractedHourLabel = ComputeMostDistractedHourLabel(snapshots, bucketSeconds);

        return new LocalTodaySummaryMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: focusedTodaySeconds,
            DistractionTodaySeconds: distractionTodaySeconds,
            AwayTodaySeconds: awayTodaySeconds,
            ContextExcursionTodaySeconds: contextExcursionTodaySeconds,
            TrackedTodaySeconds: trackedTodaySeconds,
            BestFocusBlockSeconds: bestFocusBlockSeconds,
            AverageFocusPercent: averageFocusPercent,
            MostDistractedHourLabel: mostDistractedHourLabel);
    }

    private static string ComputeMostDistractedHourLabel(
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        double bucketSeconds)
    {
        if (snapshots.Count == 0)
        {
            return NotAvailableText;
        }

        var worstHour = snapshots
            .GroupBy(static snapshot => snapshot.TimestampUtc.ToLocalTime().Hour)
            .Select(group => new
            {
                Hour = group.Key,
                DistractionSeconds = group.Sum(snapshot => LocalAttentionSegmentAnalytics.IsDistractionEpisode(snapshot) ? bucketSeconds : 0d)
            })
            .OrderByDescending(static item => item.DistractionSeconds)
            .ThenBy(static item => item.Hour)
            .FirstOrDefault();

        return worstHour == null || worstHour.DistractionSeconds <= 0d
            ? NotAvailableText
            : $"{worstHour.Hour:00}:00-{worstHour.Hour:00}:59";
    }
}
