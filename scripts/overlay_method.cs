    private static HistoryFocusHeatmap OverlayTodayTimeline(
        HistoryFocusHeatmap heatmap,
        LocalApiTimelineResponse todayTimeline)
    {
        DateTime today = DateTime.Today;
        IReadOnlyList<HistoryTimelineEpisode> todayEps =
            HistoryTimelineEpisodeBuilder.Build(todayTimeline);
        List<HistoryTimelineEpisode> merged = [
            ..heatmap.Episodes.Where(e => e.StartUtc.ToLocalTime().Date != today),
            ..todayEps.Where(e => e.StartUtc.ToLocalTime().Date == today),
        ];
        merged.Sort(static (a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return heatmap with { Episodes = merged };
    }

