using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal enum HistoryTimelineEpisodeKind
{
    FocusEpisode,
    ContextExcursion,
    OffTaskDistraction,
    FragmentationDistraction,
    Away,
    OverloadEpisode
}

internal sealed record HistoryTimelineEpisode(
    string Key,
    HistoryTimelineEpisodeKind Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DurationSeconds,
    string ReasonCode,
    string ReasonText,
    double AveragePressurePercent,
    double PeakPressurePercent,
    double AverageConfidencePercent,
    bool OverloadActive,
    IReadOnlyList<LocalApiEventResponse> Interventions,
    LocalApiCurrentSnapshotResponse? LatestSnapshot)
{
    public string DisplayLabel => Kind switch
    {
        HistoryTimelineEpisodeKind.FocusEpisode => "Focus episode",
        HistoryTimelineEpisodeKind.ContextExcursion => "Context excursion",
        HistoryTimelineEpisodeKind.OffTaskDistraction => "Off-task distraction",
        HistoryTimelineEpisodeKind.FragmentationDistraction => "Fragmentation distraction",
        HistoryTimelineEpisodeKind.Away => "Away segment",
        HistoryTimelineEpisodeKind.OverloadEpisode => "Overload episode",
        _ => "Episode"
    };

    public bool Contains(DateTimeOffset timestampUtc) =>
        timestampUtc >= StartUtc && timestampUtc < EndUtc;

    public bool OverlapsHour(DateTime localDate, int hour)
    {
        DateTime hourStartLocal = localDate.Date.AddHours(hour);
        DateTime hourEndLocal = hourStartLocal.AddHours(1);
        DateTime episodeStartLocal = StartUtc.ToLocalTime().DateTime;
        DateTime episodeEndLocal = EndUtc.ToLocalTime().DateTime;
        return episodeStartLocal < hourEndLocal && episodeEndLocal > hourStartLocal;
    }
}

internal static class HistoryTimelineEpisodeBuilder
{
    public static IReadOnlyList<HistoryTimelineEpisode> Build(LocalApiTimelineResponse? timeline)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline?.Snapshots ?? [];
        if (timeline == null || snapshots.Count == 0)
        {
            return [];
        }

        LocalApiCurrentSnapshotResponse[] orderedSnapshots = snapshots
            .OrderBy(static snapshot => snapshot.TimestampUtc)
            .ToArray();
        IReadOnlyList<LocalApiEventResponse> interventions = timeline.Interventions
            .OrderBy(static intervention => intervention.TimestampUtc)
            .ToArray();
        double bucketSeconds = Math.Max(1d, timeline.BucketSeconds);

        List<HistoryTimelineEpisode> episodes = new();
        AppendAttentionEpisodes(episodes, orderedSnapshots, interventions, bucketSeconds);
        AppendFocusEpisodes(episodes, orderedSnapshots, interventions, bucketSeconds);
        AppendOverloadEpisodes(episodes, orderedSnapshots, interventions, bucketSeconds);

        return episodes
            .OrderBy(static episode => episode.StartUtc)
            .ThenBy(static episode => episode.Kind)
            .ToArray();
    }

    private static void AppendAttentionEpisodes(
        ICollection<HistoryTimelineEpisode> episodes,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        IReadOnlyList<LocalApiEventResponse> interventions,
        double bucketSeconds)
    {
        List<LocalApiCurrentSnapshotResponse> run = new();
        string? currentKind = null;

        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            string kind = LocalAttentionSegmentAnalytics.ResolveAttentionSegmentKind(snapshot);
            if (string.Equals(kind, LocalAttentionSegmentAnalytics.FocusSegmentKind, StringComparison.Ordinal))
            {
                FlushAttentionRun(episodes, run, currentKind, interventions, bucketSeconds);
                currentKind = null;
                continue;
            }

            bool sameRun = run.Count > 0
                && string.Equals(kind, currentKind, StringComparison.Ordinal)
                && IsContiguous(run[^1], snapshot, bucketSeconds);
            if (!sameRun)
            {
                FlushAttentionRun(episodes, run, currentKind, interventions, bucketSeconds);
                currentKind = kind;
            }

            run.Add(snapshot);
        }

        FlushAttentionRun(episodes, run, currentKind, interventions, bucketSeconds);
    }

    private static void FlushAttentionRun(
        ICollection<HistoryTimelineEpisode> episodes,
        List<LocalApiCurrentSnapshotResponse> run,
        string? currentKind,
        IReadOnlyList<LocalApiEventResponse> interventions,
        double bucketSeconds)
    {
        if (run.Count == 0 || string.IsNullOrWhiteSpace(currentKind))
        {
            run.Clear();
            return;
        }

        HistoryTimelineEpisodeKind kind = currentKind switch
        {
            LocalAttentionSegmentAnalytics.ContextExcursionSegmentKind => HistoryTimelineEpisodeKind.ContextExcursion,
            LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind => HistoryTimelineEpisodeKind.OffTaskDistraction,
            LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind => HistoryTimelineEpisodeKind.FragmentationDistraction,
            LocalAttentionSegmentAnalytics.AwaySegmentKind => HistoryTimelineEpisodeKind.Away,
            _ => HistoryTimelineEpisodeKind.ContextExcursion
        };
        LocalApiCurrentSnapshotResponse latest = run[^1];
        episodes.Add(CreateEpisode(
            kind,
            run,
            interventions,
            bucketSeconds,
            LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonCode(latest),
            LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(latest)));
        run.Clear();
    }

    private static void AppendFocusEpisodes(
        ICollection<HistoryTimelineEpisode> episodes,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        IReadOnlyList<LocalApiEventResponse> interventions,
        double bucketSeconds)
    {
        List<LocalApiCurrentSnapshotResponse> run = new();
        LocalApiCurrentSnapshotResponse? closingSnapshot = null;

        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            bool active = ResolveFocusEpisodeSeconds(snapshot) > 0.5d;
            if (active)
            {
                bool contiguous = run.Count == 0 || IsContiguous(run[^1], snapshot, bucketSeconds);
                if (!contiguous)
                {
                    FlushFocusRun(episodes, run, closingSnapshot, interventions, bucketSeconds);
                    closingSnapshot = null;
                }

                run.Add(snapshot);
                continue;
            }

            if (run.Count > 0)
            {
                closingSnapshot = snapshot;
                FlushFocusRun(episodes, run, closingSnapshot, interventions, bucketSeconds);
                closingSnapshot = null;
            }
        }

        FlushFocusRun(episodes, run, closingSnapshot, interventions, bucketSeconds);
    }

    private static void FlushFocusRun(
        ICollection<HistoryTimelineEpisode> episodes,
        List<LocalApiCurrentSnapshotResponse> run,
        LocalApiCurrentSnapshotResponse? closingSnapshot,
        IReadOnlyList<LocalApiEventResponse> interventions,
        double bucketSeconds)
    {
        if (run.Count == 0)
        {
            return;
        }

        LocalApiCurrentSnapshotResponse reasonSnapshot = closingSnapshot ?? run[^1];
        episodes.Add(CreateEpisode(
            HistoryTimelineEpisodeKind.FocusEpisode,
            run,
            interventions,
            bucketSeconds,
            LocalAttentionSegmentAnalytics.ResolveFocusEpisodeReasonCode(reasonSnapshot),
            LocalAttentionSegmentAnalytics.ResolveFocusEpisodeReasonText(reasonSnapshot)));
        run.Clear();
    }

    private static void AppendOverloadEpisodes(
        ICollection<HistoryTimelineEpisode> episodes,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        IReadOnlyList<LocalApiEventResponse> interventions,
        double bucketSeconds)
    {
        List<LocalApiCurrentSnapshotResponse> run = new();

        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            bool active = snapshot.OverloadActive || snapshot.ActivationWindowOpen;
            if (active)
            {
                bool contiguous = run.Count == 0 || IsContiguous(run[^1], snapshot, bucketSeconds);
                if (!contiguous)
                {
                    FlushOverloadRun(episodes, run, interventions, bucketSeconds);
                }

                run.Add(snapshot);
                continue;
            }

            FlushOverloadRun(episodes, run, interventions, bucketSeconds);
        }

        FlushOverloadRun(episodes, run, interventions, bucketSeconds);
    }

    private static void FlushOverloadRun(
        ICollection<HistoryTimelineEpisode> episodes,
        List<LocalApiCurrentSnapshotResponse> run,
        IReadOnlyList<LocalApiEventResponse> interventions,
        double bucketSeconds)
    {
        if (run.Count == 0)
        {
            return;
        }

        LocalApiCurrentSnapshotResponse latest = run[^1];
        episodes.Add(CreateEpisode(
            HistoryTimelineEpisodeKind.OverloadEpisode,
            run,
            interventions,
            bucketSeconds,
            "overload_active",
            BuildOverloadReasonText(latest)));
        run.Clear();
    }

    private static HistoryTimelineEpisode CreateEpisode(
        HistoryTimelineEpisodeKind kind,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> run,
        IReadOnlyList<LocalApiEventResponse> interventions,
        double bucketSeconds,
        string reasonCode,
        string reasonText)
    {
        LocalApiCurrentSnapshotResponse first = run[0];
        LocalApiCurrentSnapshotResponse latest = run[^1];
        DateTimeOffset startUtc = first.TimestampUtc;
        DateTimeOffset endUtc = latest.TimestampUtc.AddSeconds(bucketSeconds);
        IReadOnlyList<LocalApiEventResponse> overlappingInterventions = interventions
            .Where(intervention => intervention.TimestampUtc >= startUtc && intervention.TimestampUtc < endUtc)
            .ToArray();
        double averagePressurePercent = run.Average(snapshot => ResolvePressureLevel(snapshot) * 100d);
        double peakPressurePercent = run.Max(snapshot => ResolvePressureLevel(snapshot) * 100d);
        double averageConfidencePercent = run.Average(snapshot => Math.Clamp(snapshot.Confidence, 0d, 1d) * 100d);
        bool overloadActive = run.Any(static snapshot => snapshot.OverloadActive || snapshot.ActivationWindowOpen);

        return new HistoryTimelineEpisode(
            Key: $"{kind}:{startUtc:O}:{endUtc:O}",
            Kind: kind,
            StartUtc: startUtc,
            EndUtc: endUtc,
            DurationSeconds: Math.Max(bucketSeconds, (endUtc - startUtc).TotalSeconds),
            ReasonCode: reasonCode,
            ReasonText: string.IsNullOrWhiteSpace(reasonText) ? "Episode detail unavailable." : reasonText.Trim(),
            AveragePressurePercent: averagePressurePercent,
            PeakPressurePercent: peakPressurePercent,
            AverageConfidencePercent: averageConfidencePercent,
            OverloadActive: overloadActive,
            Interventions: overlappingInterventions,
            LatestSnapshot: latest);
    }

    private static bool IsContiguous(
        LocalApiCurrentSnapshotResponse previous,
        LocalApiCurrentSnapshotResponse current,
        double bucketSeconds)
    {
        double gapSeconds = (current.TimestampUtc - previous.TimestampUtc).TotalSeconds;
        return gapSeconds <= Math.Max(bucketSeconds * 2d, bucketSeconds + 5d);
    }

    private static string BuildOverloadReasonText(LocalApiCurrentSnapshotResponse snapshot)
    {
        IReadOnlyList<string> contributors = snapshot.Contributors ?? [];
        if (contributors.Count > 0)
        {
            string top = string.Join(", ", contributors.Where(static value => !string.IsNullOrWhiteSpace(value)).Take(3));
            if (!string.IsNullOrWhiteSpace(top))
            {
                return $"Pressure stayed high enough to keep overload active, led by {top}.";
            }
        }

        return "Pressure stayed high enough to keep overload active.";
    }

    private static double ResolvePressureLevel(LocalApiCurrentSnapshotResponse snapshot) =>
        Math.Clamp(snapshot.PressureLevel ?? snapshot.OverloadLikelihood, 0d, 1d);

    private static double ResolveFocusEpisodeSeconds(LocalApiCurrentSnapshotResponse snapshot)
    {
        if (snapshot.FocusEpisodeSeconds > 0d)
        {
            return snapshot.FocusEpisodeSeconds;
        }

        if (snapshot.TaskContinuitySeconds > 0d)
        {
            return snapshot.TaskContinuitySeconds;
        }

        return Math.Max(0d, snapshot.SustainedAttentionDurationSec);
    }
}
