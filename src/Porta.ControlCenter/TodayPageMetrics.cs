using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal sealed record TodayPageMetrics(
    bool HasProcessedTimeline,
    double FocusedTodaySeconds,
    double DistractionTodaySeconds,
    double BestFocusBlockSeconds,
    double TrackedTodaySeconds,
    double AverageFocusPercent,
    string MostDistractedHourLabel);

internal static class TodayPageMetricsBuilder
{
    private const string NotAvailableText = "Not available";

    public static TodayPageMetrics Build(LocalApiTimelineResponse? timeline)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline?.Snapshots ?? [];
        if (timeline == null || snapshots.Count == 0)
        {
            return new TodayPageMetrics(
                HasProcessedTimeline: false,
                FocusedTodaySeconds: 0d,
                DistractionTodaySeconds: 0d,
                BestFocusBlockSeconds: 0d,
                TrackedTodaySeconds: 0d,
                AverageFocusPercent: 0d,
                MostDistractedHourLabel: NotAvailableText);
        }

        double bucketSeconds = Math.Max(1d, timeline.BucketSeconds);
        double focusedTodaySeconds = 0d;
        double trackedTodaySeconds = 0d;
        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            double focusStrength = Math.Clamp(snapshot.FocusStrength, 0d, 1d);
            focusedTodaySeconds += bucketSeconds * focusStrength;
            trackedTodaySeconds += bucketSeconds;
        }

        double distractionTodaySeconds = Math.Max(0d, trackedTodaySeconds - focusedTodaySeconds);
        double bestFocusBlockSeconds = snapshots.Max(static snapshot => Math.Max(0d, snapshot.SustainedAttentionDurationSec));
        double averageFocusPercent = snapshots.Average(static snapshot => Math.Clamp(snapshot.FocusStrength, 0d, 1d)) * 100d;
        string mostDistractedHourLabel = ComputeMostDistractedHourLabel(snapshots);

        return new TodayPageMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: focusedTodaySeconds,
            DistractionTodaySeconds: distractionTodaySeconds,
            BestFocusBlockSeconds: bestFocusBlockSeconds,
            TrackedTodaySeconds: trackedTodaySeconds,
            AverageFocusPercent: averageFocusPercent,
            MostDistractedHourLabel: mostDistractedHourLabel);
    }

    private static string ComputeMostDistractedHourLabel(IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots)
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
                AverageDistraction = group.Average(static snapshot => 1d - Math.Clamp(snapshot.FocusStrength, 0d, 1d))
            })
            .OrderByDescending(static item => item.AverageDistraction)
            .ThenBy(static item => item.Hour)
            .FirstOrDefault();

        return worstHour == null
            ? NotAvailableText
            : $"{worstHour.Hour:00}:00-{worstHour.Hour:00}:59";
    }
}
