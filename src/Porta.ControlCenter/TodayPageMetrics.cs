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
    public static TodayPageMetrics Build(LocalApiTimelineResponse? timeline)
    {
        LocalTodaySummaryMetrics summary = LocalTodaySummaryMetricsBuilder.Build(timeline);

        return new TodayPageMetrics(
            HasProcessedTimeline: summary.HasProcessedTimeline,
            FocusedTodaySeconds: summary.FocusedTodaySeconds,
            DistractionTodaySeconds: summary.DistractionTodaySeconds,
            BestFocusBlockSeconds: summary.BestFocusBlockSeconds,
            TrackedTodaySeconds: summary.TrackedTodaySeconds,
            AverageFocusPercent: summary.AverageFocusPercent,
            MostDistractedHourLabel: summary.MostDistractedHourLabel);
    }
}
