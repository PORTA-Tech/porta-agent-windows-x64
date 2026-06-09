using System;
using System.Collections.Generic;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Shared aggregation logic used by both DailyDigestReporter (email send at 19:30 UTC)
/// and DailyStatsReporter (periodic stats push every 2 hours).
/// </summary>
internal static class DigestTimelineAggregator
{
    public static DigestTimelineAggregation Aggregate(LocalApiTimelineResponse timeline)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline.Snapshots;
        if (snapshots.Count == 0)
        {
            return DigestTimelineAggregation.Empty;
        }

        double bucketSec = Math.Max(1d, timeline.BucketSeconds);
        double trackedSeconds = 0d;
        double focusSeconds = 0d;
        double distractionSeconds = 0d;
        double awaySeconds = 0d;
        double contextExcursionSeconds = 0d;
        double focusStrengthSum = 0d;
        int focusedBuckets = 0;
        double longestStreakSec = 0d;
        int overloadEpisodeCount = 0;
        int fragmentationEpisodeCount = 0;
        bool prevOverloadActive = false;
        string prevAttentionKind = string.Empty;

        double developmentSeconds = 0d;
        double productivitySeconds = 0d;
        double communicationSeconds = 0d;
        double entertainmentSeconds = 0d;

        var hourDistraction = new double[24];

        DateTimeOffset? firstFocusBlockTimestamp = null;
        DateTimeOffset? peakPressureTimestamp = null;
        double peakOverloadLikelihood = -1d;
        int contextSwitchCount = 0;

        bool inDistractionBlock = false;
        double currentDistractionBlockDuration = 0d;
        double preFocusForCurrentBlock = 0d;
        double focusSinceLastDistraction = 0d;
        double longestDistractionBlockDuration = 0d;
        double preLongestDistractionFocusSeconds = 0d;

        foreach (LocalApiCurrentSnapshotResponse s in snapshots)
        {
            trackedSeconds += bucketSec;
            double strength = Math.Clamp(s.FocusStrength, 0d, 1d);
            focusSeconds += bucketSec * strength;

            string kind = s.AttentionSegmentKind ?? string.Empty;
            bool isDistraction = LocalAttentionSegmentAnalytics.IsDistractionEpisode(s);
            bool isAway = LocalAttentionSegmentAnalytics.IsAwaySegment(s);
            bool isContextExcursion = LocalAttentionSegmentAnalytics.IsContextExcursionSegment(s);

            if (isDistraction)
            {
                distractionSeconds += bucketSec;
                entertainmentSeconds += bucketSec;
                int h = s.TimestampUtc.ToLocalTime().Hour;
                hourDistraction[h] += bucketSec;
            }
            else if (isAway)
            {
                awaySeconds += bucketSec;
            }
            else if (isContextExcursion)
            {
                contextExcursionSeconds += bucketSec;
                communicationSeconds += bucketSec;
            }
            else
            {
                focusStrengthSum += strength;
                focusedBuckets++;
                if (strength >= 0.65d)
                {
                    developmentSeconds += bucketSec * strength;
                    productivitySeconds += bucketSec * (1d - strength);
                }
                else
                {
                    productivitySeconds += bucketSec;
                }
            }

            if (s.SustainedAttentionDurationSec > longestStreakSec)
            {
                longestStreakSec = s.SustainedAttentionDurationSec;
            }

            if (s.OverloadActive && !prevOverloadActive)
            {
                overloadEpisodeCount++;
            }
            prevOverloadActive = s.OverloadActive;

            bool isFragmentation = string.Equals(kind,
                LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind,
                StringComparison.OrdinalIgnoreCase);
            bool prevFragmentation = string.Equals(prevAttentionKind,
                LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind,
                StringComparison.OrdinalIgnoreCase);
            if (isFragmentation && !prevFragmentation)
            {
                fragmentationEpisodeCount++;
            }

            if (!string.IsNullOrEmpty(prevAttentionKind) &&
                !string.Equals(kind, prevAttentionKind, StringComparison.OrdinalIgnoreCase))
            {
                contextSwitchCount++;
            }
            prevAttentionKind = kind;

            if (firstFocusBlockTimestamp == null && !isDistraction && !isAway && !isContextExcursion
                && s.FocusStrength > 0.5d)
            {
                firstFocusBlockTimestamp = s.TimestampUtc;
            }

            if (s.OverloadLikelihood > peakOverloadLikelihood)
            {
                peakOverloadLikelihood = s.OverloadLikelihood;
                peakPressureTimestamp = s.TimestampUtc;
            }

            if (isDistraction)
            {
                if (!inDistractionBlock)
                {
                    inDistractionBlock = true;
                    preFocusForCurrentBlock = focusSinceLastDistraction;
                    currentDistractionBlockDuration = 0d;
                    focusSinceLastDistraction = 0d;
                }
                currentDistractionBlockDuration += bucketSec;
            }
            else
            {
                if (inDistractionBlock)
                {
                    if (currentDistractionBlockDuration > longestDistractionBlockDuration)
                    {
                        longestDistractionBlockDuration = currentDistractionBlockDuration;
                        preLongestDistractionFocusSeconds = preFocusForCurrentBlock;
                    }
                    inDistractionBlock = false;
                    currentDistractionBlockDuration = 0d;
                }
                if (!isAway && !isContextExcursion)
                {
                    focusSinceLastDistraction += bucketSec;
                }
            }
        }

        if (inDistractionBlock && currentDistractionBlockDuration > longestDistractionBlockDuration)
        {
            preLongestDistractionFocusSeconds = preFocusForCurrentBlock;
        }

        double focusQualityScore = focusedBuckets > 0 ? focusStrengthSum / focusedBuckets : 0d;
        double avgDistractionEpisodeDurationSeconds = fragmentationEpisodeCount > 0
            ? distractionSeconds / fragmentationEpisodeCount
            : 0d;

        int worstHour = -1;
        double worstSec = 0d;
        for (int h = 0; h < 24; h++)
        {
            if (hourDistraction[h] > worstSec)
            {
                worstSec = hourDistraction[h];
                worstHour = h;
            }
        }
        string mostDistractedHourLabel = worstHour >= 0 && worstSec > 0d
            ? $"{worstHour:00}:00-{worstHour:00}:59"
            : "n/a";

        return new DigestTimelineAggregation(
            TrackedSeconds: trackedSeconds,
            FocusSeconds: focusSeconds,
            FocusQualityScore: focusQualityScore,
            DistractionSeconds: distractionSeconds,
            AwaySeconds: awaySeconds,
            ContextExcursionSeconds: contextExcursionSeconds,
            LongestFocusStreakSeconds: longestStreakSec,
            FragmentationEpisodeCount: fragmentationEpisodeCount,
            OverloadEpisodeCount: overloadEpisodeCount,
            MostDistractedHourLabel: mostDistractedHourLabel,
            DevelopmentSeconds: developmentSeconds,
            ProductivitySeconds: productivitySeconds,
            CommunicationSeconds: communicationSeconds,
            EntertainmentSeconds: entertainmentSeconds,
            FirstFocusBlockUtc: firstFocusBlockTimestamp?.ToUniversalTime().ToString("O"),
            PeakPressureUtc: peakPressureTimestamp?.ToUniversalTime().ToString("O"),
            AvgDistractionEpisodeDurationSeconds: avgDistractionEpisodeDurationSeconds,
            ContextSwitchCount: contextSwitchCount,
            PreLongestDistractionFocusSeconds: preLongestDistractionFocusSeconds);
    }
}

internal readonly record struct DigestTimelineAggregation(
    double TrackedSeconds,
    double FocusSeconds,
    double FocusQualityScore,
    double DistractionSeconds,
    double AwaySeconds,
    double ContextExcursionSeconds,
    double LongestFocusStreakSeconds,
    int FragmentationEpisodeCount,
    int OverloadEpisodeCount,
    string MostDistractedHourLabel,
    double DevelopmentSeconds,
    double ProductivitySeconds,
    double CommunicationSeconds,
    double EntertainmentSeconds,
    string? FirstFocusBlockUtc,
    string? PeakPressureUtc,
    double AvgDistractionEpisodeDurationSeconds,
    int ContextSwitchCount,
    double PreLongestDistractionFocusSeconds)
{
    public static DigestTimelineAggregation Empty => new(
        0d, 0d, 0d, 0d, 0d, 0d, 0d, 0, 0, "n/a", 0d, 0d, 0d, 0d,
        null, null, 0d, 0, 0d);
}
