using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal sealed record TodayPageMetrics(
    bool HasProcessedTimeline,
    double FocusedTodaySeconds,
    double DistractionTodaySeconds,
    double TrackedTodaySeconds,
    double DisplayedTrackedTodaySeconds,
    string MostDistractedHourLabel);

internal static class TodayPageMetricsBuilder
{
    public static TodayPageMetrics Build(
        LocalApiTimelineResponse? timeline,
        LocalApiCurrentSnapshotResponse? snapshot = null,
        LocalApiLiveSessionStateResponse? liveSession = null,
        DateTimeOffset? nowUtc = null)
    {
        LocalTodaySummaryMetrics summary = LocalTodaySummaryMetricsBuilder.Build(
            timeline,
            snapshot,
            liveSession,
            nowUtc);

        return new TodayPageMetrics(
            HasProcessedTimeline: summary.HasProcessedTimeline,
            FocusedTodaySeconds: summary.FocusedTodaySeconds,
            DistractionTodaySeconds: summary.DistractionTodaySeconds,
            TrackedTodaySeconds: summary.TrackedTodaySeconds,
            DisplayedTrackedTodaySeconds: summary.DisplayedTrackedTodaySeconds,
            MostDistractedHourLabel: summary.MostDistractedHourLabel);
    }
}
