namespace Porta.Agent.LocalApi;

public sealed record LocalTodaySummaryMetrics(
    bool HasProcessedTimeline,
    // Focus-episode time — the one "focused" number every surface shows. The strength-weighted
    // sum is intentionally not exposed as time; its only surviving form is FocusQualityPercent.
    double FocusedTodaySeconds,
    double FocusQualityPercent,
    double DistractionTodaySeconds,
    double AwayTodaySeconds,
    double ContextExcursionTodaySeconds,
    double TrackedTodaySeconds,
    double DisplayedTrackedTodaySeconds,
    string MostDistractedHourLabel);

public static class LocalTodaySummaryMetricsBuilder
{
    public const string NotAvailableText = "Not available";

    public static LocalTodaySummaryMetrics Build(
        LocalApiTimelineResponse? timeline,
        LocalApiCurrentSnapshotResponse? snapshot = null,
        LocalApiLiveSessionStateResponse? liveSession = null,
        DateTimeOffset? nowUtc = null)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline?.Snapshots ?? [];
        if (timeline == null || snapshots.Count == 0)
        {
            return new LocalTodaySummaryMetrics(
                HasProcessedTimeline: false,
                FocusedTodaySeconds: 0d,
                FocusQualityPercent: 0d,
                DistractionTodaySeconds: 0d,
                AwayTodaySeconds: 0d,
                ContextExcursionTodaySeconds: 0d,
                TrackedTodaySeconds: 0d,
                DisplayedTrackedTodaySeconds: 0d,
                MostDistractedHourLabel: NotAvailableText);
        }

        double bucketSeconds = Math.Max(1d, timeline.BucketSeconds);
        double weightedFocusSeconds = 0d;
        double distractionTodaySeconds = 0d;
        double awayTodaySeconds = 0d;
        double contextExcursionTodaySeconds = 0d;
        double trackedTodaySeconds = 0d;
        foreach (LocalApiCurrentSnapshotResponse candidate in snapshots)
        {
            double focusStrength = Math.Clamp(candidate.FocusStrength, 0d, 1d);
            weightedFocusSeconds += bucketSeconds * focusStrength;
            trackedTodaySeconds += bucketSeconds;

            if (LocalAttentionSegmentAnalytics.IsDistractionEpisode(candidate))
            {
                distractionTodaySeconds += bucketSeconds;
            }
            else if (LocalAttentionSegmentAnalytics.IsAwaySegment(candidate))
            {
                awayTodaySeconds += bucketSeconds;
            }
            else if (LocalAttentionSegmentAnalytics.IsContextExcursionSegment(candidate))
            {
                contextExcursionTodaySeconds += bucketSeconds;
            }
        }

        string mostDistractedHourLabel = ComputeMostDistractedHourLabel(snapshots, bucketSeconds);
        double displayedTrackedTodaySeconds = trackedTodaySeconds + ResolveLiveTrackedProjectionSeconds(
            snapshots,
            snapshot,
            liveSession,
            nowUtc ?? DateTimeOffset.UtcNow);
        double episodeFocusSeconds = LocalFocusEpisodeMetrics.ComputeEpisodeFocusSeconds(snapshots, bucketSeconds);

        return new LocalTodaySummaryMetrics(
            HasProcessedTimeline: true,
            FocusedTodaySeconds: episodeFocusSeconds,
            FocusQualityPercent: LocalFocusEpisodeMetrics.ComputeFocusQualityPercent(weightedFocusSeconds, episodeFocusSeconds),
            DistractionTodaySeconds: distractionTodaySeconds,
            AwayTodaySeconds: awayTodaySeconds,
            ContextExcursionTodaySeconds: contextExcursionTodaySeconds,
            TrackedTodaySeconds: trackedTodaySeconds,
            DisplayedTrackedTodaySeconds: displayedTrackedTodaySeconds,
            MostDistractedHourLabel: mostDistractedHourLabel);
    }

    private static double ResolveLiveTrackedProjectionSeconds(
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        DateTimeOffset nowUtc)
    {
        if (snapshots.Count == 0
            || !IsFreshLiveSession(liveSession)
            || liveSession!.idle
            || !liveSession.user_active)
        {
            return 0d;
        }

        DateTimeOffset latestTrackedUtc = snapshots.Max(static candidate => candidate.TimestampUtc);
        if (snapshot != null && snapshot.TimestampUtc > latestTrackedUtc)
        {
            latestTrackedUtc = snapshot.TimestampUtc;
        }

        DateTimeOffset latestKnownUtc = liveSession.UpdatedAtUtc ?? nowUtc;
        if (latestKnownUtc <= latestTrackedUtc)
        {
            return 0d;
        }

        return Math.Clamp((latestKnownUtc - latestTrackedUtc).TotalSeconds, 0d, 90d);
    }

    private static bool IsFreshLiveSession(LocalApiLiveSessionStateResponse? liveSession)
    {
        return liveSession != null
            && string.Equals(liveSession.freshness_state, "fresh", StringComparison.OrdinalIgnoreCase);
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
