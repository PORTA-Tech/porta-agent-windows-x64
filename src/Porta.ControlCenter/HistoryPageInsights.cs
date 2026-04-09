using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal sealed record HistoryPageInsights(
    bool HasTimeline,
    double FocusedSeconds,
    double DistractionSeconds,
    double AwaySeconds,
    double ContextExcursionSeconds,
    double TrackedSeconds,
    double LongestFocusBlockSeconds,
    string MostDistractedPeriodLabel,
    string LatestAttentionReasonText,
    string LatestFocusEpisodeReasonText,
    int ActionMarkerCount,
    int SuggestionCount,
    int ChangeCount);

internal static class HistoryPageInsightsBuilder
{
    private const string NotAvailableText = "Not available";

    public static HistoryPageInsights Build(LocalApiTimelineResponse? timeline)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline?.Snapshots ?? [];
        if (timeline == null || snapshots.Count == 0)
        {
            return new HistoryPageInsights(
                HasTimeline: false,
                FocusedSeconds: 0d,
                DistractionSeconds: 0d,
                AwaySeconds: 0d,
                ContextExcursionSeconds: 0d,
                TrackedSeconds: 0d,
                LongestFocusBlockSeconds: 0d,
                MostDistractedPeriodLabel: NotAvailableText,
                LatestAttentionReasonText: string.Empty,
                LatestFocusEpisodeReasonText: string.Empty,
                ActionMarkerCount: 0,
                SuggestionCount: 0,
                ChangeCount: 0);
        }

        double bucketSeconds = Math.Max(1d, timeline.BucketSeconds);
        double focusedSeconds = 0d;
        double distractionSeconds = 0d;
        double awaySeconds = 0d;
        double contextExcursionSeconds = 0d;
        double trackedSeconds = 0d;
        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            double focusStrength = Math.Clamp(snapshot.FocusStrength, 0d, 1d);
            focusedSeconds += bucketSeconds * focusStrength;
            trackedSeconds += bucketSeconds;
            if (LocalAttentionSegmentAnalytics.IsDistractionEpisode(snapshot))
            {
                distractionSeconds += bucketSeconds;
            }
            else if (LocalAttentionSegmentAnalytics.IsAwaySegment(snapshot))
            {
                awaySeconds += bucketSeconds;
            }
            else if (LocalAttentionSegmentAnalytics.IsContextExcursionSegment(snapshot))
            {
                contextExcursionSeconds += bucketSeconds;
            }
        }

        double longestFocusBlockSeconds = snapshots.Max(static snapshot => Math.Max(0d, snapshot.SustainedAttentionDurationSec));
        string mostDistractedPeriodLabel = ComputeMostDistractedPeriodLabel(snapshots, bucketSeconds);
        LocalApiCurrentSnapshotResponse latestSnapshot = snapshots[^1];
        int suggestionCount = timeline.Interventions.Count(static intervention => IsSuggestionEvent(intervention.Action));
        int changeCount = timeline.Interventions.Count(static intervention => IsSystemChangeEvent(intervention));

        return new HistoryPageInsights(
            HasTimeline: true,
            FocusedSeconds: focusedSeconds,
            DistractionSeconds: distractionSeconds,
            AwaySeconds: awaySeconds,
            ContextExcursionSeconds: contextExcursionSeconds,
            TrackedSeconds: trackedSeconds,
            LongestFocusBlockSeconds: longestFocusBlockSeconds,
            MostDistractedPeriodLabel: mostDistractedPeriodLabel,
            LatestAttentionReasonText: LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(latestSnapshot),
            LatestFocusEpisodeReasonText: LocalAttentionSegmentAnalytics.ResolveFocusEpisodeReasonText(latestSnapshot),
            ActionMarkerCount: timeline.Interventions.Count,
            SuggestionCount: suggestionCount,
            ChangeCount: changeCount);
    }

    private static string ComputeMostDistractedPeriodLabel(
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

        if (worstHour == null || worstHour.DistractionSeconds <= 0d)
        {
            return NotAvailableText;
        }

        return worstHour.Hour switch
        {
            >= 5 and < 12 => "morning",
            >= 12 and < 17 => "afternoon",
            >= 17 and < 22 => "evening",
            _ => "late night"
        };
    }

    private static bool IsSuggestionEvent(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        string normalized = action.Trim().ToLowerInvariant();
        return normalized.EndsWith("_suggestion", StringComparison.Ordinal)
            || normalized.Contains("suggestion", StringComparison.Ordinal);
    }

    private static bool IsSystemChangeEvent(LocalApiEventResponse intervention)
    {
        if (IsSuggestionEvent(intervention.Action))
        {
            return false;
        }

        string action = intervention.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (action.StartsWith("inform_", StringComparison.Ordinal))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(intervention.WhatChanged)
            || !string.IsNullOrWhiteSpace(action);
    }
}
