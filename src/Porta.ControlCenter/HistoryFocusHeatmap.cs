using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal sealed record HistoryFocusHeatmapDay(
    int DayIndex,
    DateTime LocalDate,
    string HeaderLabel,
    string ShortLabel);

internal sealed record HistoryFocusHeatmapCell(
    string Key,
    int DayIndex,
    DateTime LocalDate,
    int Hour,
    double FocusedSeconds,
    double TrackedSeconds,
    double FocusEpisodeSeconds,
    double ContextExcursionSeconds,
    double DistractionEpisodeSeconds,
    double AwayEpisodeSeconds,
    double OverloadEpisodeSeconds,
    double AverageFocusPercent,
    double AverageOverloadPercent,
    double AverageConfidencePercent,
    double LongestFocusBlockSeconds,
    int SnapshotCount,
    IReadOnlyList<HistoryTimelineEpisode> Episodes,
    IReadOnlyList<LocalApiEventResponse> Interventions,
    LocalApiCurrentSnapshotResponse? LatestSnapshot)
{
    public bool HasTracking => SnapshotCount > 0;
    public string HourLabel => $"{Hour:00}:00-{Hour:00}:59";
}

internal sealed record HistoryFocusHeatmap(
    bool HasTimeline,
    IReadOnlyList<HistoryFocusHeatmapDay> Days,
    IReadOnlyList<HistoryFocusHeatmapCell> Cells,
    IReadOnlyList<HistoryTimelineEpisode> Episodes,
    double TotalFocusedSeconds,
    double TotalTrackedSeconds,
    int ActionMarkerCount,
    double MaxFocusedSecondsPerHour,
    HistoryFocusHeatmapCell? BestFocusCell,
    HistoryFocusHeatmapCell? LatestPopulatedCell)
{
    public bool HasAnyTracking => Cells.Any(static cell => cell.HasTracking);
    public int FocusEpisodeCount => Episodes.Count(static episode => episode.Kind == HistoryTimelineEpisodeKind.FocusEpisode);
    public int DistractionEpisodeCount => Episodes.Count(static episode =>
        episode.Kind is
            HistoryTimelineEpisodeKind.OffTaskDistraction or
            HistoryTimelineEpisodeKind.FragmentationDistraction or
            HistoryTimelineEpisodeKind.ContextExcursion or
            HistoryTimelineEpisodeKind.RollingMicroBurst or
            HistoryTimelineEpisodeKind.ReturnLoopDistraction or
            HistoryTimelineEpisodeKind.UnfocusedSession or
            HistoryTimelineEpisodeKind.ShallowDwellDistraction or
            HistoryTimelineEpisodeKind.FocusDepartureDistraction);
    public int AwayEpisodeCount => Episodes.Count(static episode => episode.Kind == HistoryTimelineEpisodeKind.Away);
    public int OverloadEpisodeCount => Episodes.Count(static episode => episode.Kind == HistoryTimelineEpisodeKind.OverloadEpisode);

    public HistoryFocusHeatmapCell? FindCell(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Cells.FirstOrDefault(cell => string.Equals(cell.Key, key, StringComparison.Ordinal));
    }
}

internal static class HistoryFocusHeatmapBuilder
{
    public static HistoryFocusHeatmap Build(
        LocalApiTimelineResponse? timeline,
        DateTimeOffset fallbackFromUtc,
        DateTimeOffset fallbackToUtc)
    {
        DateTimeOffset fromLocal = (timeline?.FromUtc ?? fallbackFromUtc).ToLocalTime();
        DateTimeOffset toLocal = (timeline?.ToUtc ?? fallbackToUtc).ToLocalTime();
        if (toLocal < fromLocal)
        {
            (fromLocal, toLocal) = (toLocal, fromLocal);
        }

        IReadOnlyList<HistoryFocusHeatmapDay> days = BuildDays(fromLocal.Date, toLocal.Date);
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline?.Snapshots ?? [];
        IReadOnlyList<LocalApiEventResponse> interventions = timeline?.Interventions ?? [];
        IReadOnlyList<HistoryTimelineEpisode> episodes = HistoryTimelineEpisodeBuilder.Build(timeline);
        double bucketSeconds = Math.Max(1d, timeline?.BucketSeconds ?? 60);

        Dictionary<string, IReadOnlyList<LocalApiCurrentSnapshotResponse>> snapshotsByCell = snapshots
            .GroupBy(GetCellKey)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<LocalApiCurrentSnapshotResponse>)group
                    .OrderBy(snapshot => snapshot.TimestampUtc)
                    .ToArray(),
                StringComparer.Ordinal);

        Dictionary<string, IReadOnlyList<LocalApiEventResponse>> interventionsByCell = interventions
            .GroupBy(GetCellKey)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<LocalApiEventResponse>)group
                    .OrderBy(intervention => intervention.TimestampUtc)
                    .ToArray(),
                StringComparer.Ordinal);

        List<HistoryFocusHeatmapCell> cells = new(days.Count * 24);
        double totalFocusedSeconds = 0d;
        double totalTrackedSeconds = 0d;
        double maxFocusedSecondsPerHour = 0d;
        HistoryFocusHeatmapCell? bestFocusCell = null;
        HistoryFocusHeatmapCell? latestPopulatedCell = null;

        foreach (HistoryFocusHeatmapDay day in days)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                string key = BuildCellKey(day.LocalDate, hour);
                snapshotsByCell.TryGetValue(key, out IReadOnlyList<LocalApiCurrentSnapshotResponse>? cellSnapshots);
                interventionsByCell.TryGetValue(key, out IReadOnlyList<LocalApiEventResponse>? cellInterventions);

                cellSnapshots ??= [];
                cellInterventions ??= [];

                double focusedSeconds = 0d;
                double trackedSeconds = 0d;
                double focusEpisodeSeconds = 0d;
                double contextExcursionSeconds = 0d;
                double distractionEpisodeSeconds = 0d;
                double awayEpisodeSeconds = 0d;
                double overloadEpisodeSeconds = 0d;
                double averageOverloadPercent = 0d;
                double averageConfidencePercent = 0d;
                double longestFocusBlockSeconds = 0d;
                IReadOnlyList<HistoryTimelineEpisode> cellEpisodes = episodes
                    .Where(episode => episode.OverlapsHour(day.LocalDate, hour))
                    .ToArray();

                foreach (HistoryTimelineEpisode episode in cellEpisodes)
                {
                    double overlapSeconds = episode.GetOverlapSeconds(day.LocalDate, hour);
                    if (overlapSeconds <= 0d)
                    {
                        continue;
                    }

                    switch (episode.Kind)
                    {
                        case HistoryTimelineEpisodeKind.FocusEpisode:
                            focusEpisodeSeconds += overlapSeconds;
                            break;
                        case HistoryTimelineEpisodeKind.ContextExcursion:
                            contextExcursionSeconds += overlapSeconds;
                            break;
                        case HistoryTimelineEpisodeKind.OffTaskDistraction:
                        case HistoryTimelineEpisodeKind.FragmentationDistraction:
                            distractionEpisodeSeconds += overlapSeconds;
                            break;
                        case HistoryTimelineEpisodeKind.Away:
                            awayEpisodeSeconds += overlapSeconds;
                            break;
                        case HistoryTimelineEpisodeKind.OverloadEpisode:
                            overloadEpisodeSeconds += overlapSeconds;
                            break;
                    }
                }

                if (cellSnapshots.Count > 0)
                {
                    foreach (LocalApiCurrentSnapshotResponse snapshot in cellSnapshots)
                    {
                        double focusStrength = Math.Clamp(snapshot.FocusStrength, 0d, 1d);
                        focusedSeconds += bucketSeconds * focusStrength;
                        trackedSeconds += bucketSeconds;
                    }

                    averageOverloadPercent = cellSnapshots.Average(snapshot => Math.Clamp(snapshot.PressureLevel ?? snapshot.OverloadLikelihood, 0d, 1d)) * 100d;
                    averageConfidencePercent = cellSnapshots.Average(snapshot => Math.Clamp(snapshot.Confidence, 0d, 1d)) * 100d;
                    longestFocusBlockSeconds = cellSnapshots.Max(ResolveFocusEpisodeSeconds);
                }

                double averageFocusPercent = trackedSeconds <= 0d
                    ? 0d
                    : Math.Clamp(focusedSeconds / trackedSeconds, 0d, 1d) * 100d;

                HistoryFocusHeatmapCell cell = new(
                    Key: key,
                    DayIndex: day.DayIndex,
                    LocalDate: day.LocalDate,
                    Hour: hour,
                    FocusedSeconds: focusedSeconds,
                    TrackedSeconds: trackedSeconds,
                    FocusEpisodeSeconds: focusEpisodeSeconds,
                    ContextExcursionSeconds: contextExcursionSeconds,
                    DistractionEpisodeSeconds: distractionEpisodeSeconds,
                    AwayEpisodeSeconds: awayEpisodeSeconds,
                    OverloadEpisodeSeconds: overloadEpisodeSeconds,
                    AverageFocusPercent: averageFocusPercent,
                    AverageOverloadPercent: averageOverloadPercent,
                    AverageConfidencePercent: averageConfidencePercent,
                    LongestFocusBlockSeconds: longestFocusBlockSeconds,
                    SnapshotCount: cellSnapshots.Count,
                    Episodes: cellEpisodes,
                    Interventions: cellInterventions,
                    LatestSnapshot: cellSnapshots.LastOrDefault());

                cells.Add(cell);
                totalFocusedSeconds += focusedSeconds;
                totalTrackedSeconds += trackedSeconds;
                maxFocusedSecondsPerHour = Math.Max(maxFocusedSecondsPerHour, focusedSeconds);

                if (cell.HasTracking
                    && (bestFocusCell == null
                        || cell.FocusedSeconds > bestFocusCell.FocusedSeconds
                        || (Math.Abs(cell.FocusedSeconds - bestFocusCell.FocusedSeconds) < 0.001d
                            && cell.LongestFocusBlockSeconds > bestFocusCell.LongestFocusBlockSeconds)))
                {
                    bestFocusCell = cell;
                }

                if (cell.HasTracking)
                {
                    latestPopulatedCell = cell;
                }
            }
        }

        return new HistoryFocusHeatmap(
            HasTimeline: timeline != null,
            Days: days,
            Cells: cells,
            Episodes: episodes,
            TotalFocusedSeconds: totalFocusedSeconds,
            TotalTrackedSeconds: totalTrackedSeconds,
            ActionMarkerCount: interventions.Count,
            MaxFocusedSecondsPerHour: maxFocusedSecondsPerHour,
            BestFocusCell: bestFocusCell,
            LatestPopulatedCell: latestPopulatedCell);
    }

    private static IReadOnlyList<HistoryFocusHeatmapDay> BuildDays(DateTime fromLocalDate, DateTime toLocalDate)
    {
        DateTime start = fromLocalDate.Date;
        DateTime end = toLocalDate.Date;
        int dayCount = Math.Max(1, (end - start).Days + 1);

        return Enumerable.Range(0, dayCount)
            .Select(index =>
            {
                DateTime day = start.AddDays(index);
                return new HistoryFocusHeatmapDay(
                    DayIndex: index,
                    LocalDate: day,
                    HeaderLabel: day.ToString("ddd dd MMM"),
                    ShortLabel: day.ToString("ddd dd"));
            })
            .ToArray();
    }

    private static string GetCellKey(LocalApiCurrentSnapshotResponse snapshot)
    {
        DateTime local = snapshot.TimestampUtc.ToLocalTime().DateTime;
        return BuildCellKey(local.Date, local.Hour);
    }

    private static string GetCellKey(LocalApiEventResponse intervention)
    {
        DateTime local = intervention.TimestampUtc.ToLocalTime().DateTime;
        return BuildCellKey(local.Date, local.Hour);
    }

    private static string BuildCellKey(DateTime localDate, int hour) =>
        $"{localDate:yyyy-MM-dd}|{hour:00}";

    private static double ResolveFocusEpisodeSeconds(LocalApiCurrentSnapshotResponse snapshot) =>
        Math.Max(0d, snapshot.FocusEpisodeSeconds);
}
