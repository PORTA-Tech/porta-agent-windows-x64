using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Identity;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Fires once per day at 23:30 Dubai time (UTC+4 = 19:30 UTC) and sends a daily
/// productivity digest to the backend if the user has been active for at least
/// one hour today.
/// </summary>
internal sealed class DailyDigestReporter : BackgroundService
{
    // 23:30 Dubai standard time = UTC+4 → 19:30 UTC
    private const int DigestHourUtc = 19;
    private const int DigestMinuteUtc = 30;
    private const double MinimumTrackedSecondsToSend = 3600d; // 1 hour
    private const int PollIntervalSeconds = 30;
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(10);

    private readonly IAgentCoreController _controller;
    private readonly ControlPlaneClient _client;
    private readonly ILocalIdentityLinkStore _identityLinkStore;
    private DateOnly? _lastSentDate;
    private DateTimeOffset? _lastFailureLogUtc;

    public DailyDigestReporter(
        IAgentCoreController controller,
        ControlPlaneClient client,
        ILocalIdentityLinkStore identityLinkStore)
    {
        _controller = controller;
        _client = client;
        _identityLinkStore = identityLinkStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), stoppingToken).ConfigureAwait(false);

            if (!_client.IsEnabled)
            {
                continue;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateOnly todayUtc = DateOnly.FromDateTime(nowUtc.UtcDateTime);

            // Already sent today — skip
            if (_lastSentDate.HasValue && _lastSentDate.Value == todayUtc)
            {
                continue;
            }

            // Not yet 19:30 UTC (= 23:30 Dubai)
            if (nowUtc.Hour < DigestHourUtc || (nowUtc.Hour == DigestHourUtc && nowUtc.Minute < DigestMinuteUtc))
            {
                continue;
            }

            LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();
            if (!IsLinked(identity))
            {
                continue;
            }

            await TrySendDigestAsync(identity, todayUtc, nowUtc, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TrySendDigestAsync(
        LocalIdentityLinkSnapshot identity,
        DateOnly todayUtc,
        DateTimeOffset nowUtc,
        CancellationToken token)
    {
        try
        {
            // Pull today's full timeline (midnight to now UTC)
            DateTimeOffset startOfDayUtc = new(todayUtc.Year, todayUtc.Month, todayUtc.Day, 0, 0, 0, TimeSpan.Zero);
            AgentTimelineData timelineData = _controller.GetTimeline(startOfDayUtc, nowUtc, 60);

            LocalApiTimelineResponse timeline = new(
                timelineData.FromUtc,
                timelineData.ToUtc,
                timelineData.BucketSeconds,
                timelineData.Snapshots
                    .Select(static s => SnapshotMapper.Map(s, null))
                    .ToList(),
                timelineData.Interventions
                    .Select(EventMapper.Map)
                    .ToList());

            // Aggregate timeline stats
            DigestTimelineAggregation agg = AggregateTimeline(timeline);

            // Minimum activity gate: at least 1 hour tracked
            if (agg.TrackedSeconds < MinimumTrackedSecondsToSend)
            {
                return;
            }

            // Phase 3 distraction breakdown from current session
            AgentDistractionSummary distraction = _controller.GetCurrentDistractionSummary();

            // Break compliance from interventions
            int breaksSuggested = 0;
            int breaksTaken = 0;
            foreach (LocalApiEventResponse intervention in timeline.Interventions)
            {
                if (IsBreakSuggestionEvent(intervention.Action)) breaksSuggested++;
                if (IsBreakTakenEvent(intervention.Action)) breaksTaken++;
            }

            // Timezone offset: Dubai is UTC+4 (+240 minutes)
            const int dubaiOffsetMinutes = 240;
            DateTimeOffset dubaiNow = nowUtc.ToOffset(TimeSpan.FromMinutes(dubaiOffsetMinutes));
            string dateLocal = dubaiNow.ToString("yyyy-MM-dd");

            string machineId = ResolveMachineId();

            DailyDigestReportPayload payload = new(
                AccountId: identity.AccountId,
                DeviceId: identity.DeviceId,
                MachineId: machineId,
                DateLocal: dateLocal,
                TimezoneOffsetMinutes: dubaiOffsetMinutes,
                ReportedAtUtc: nowUtc.ToString("O"),
                TrackedSeconds: agg.TrackedSeconds,
                FocusSeconds: agg.FocusSeconds,
                FocusQualityScore: agg.FocusQualityScore,
                DistractionSeconds: agg.DistractionSeconds,
                AwaySeconds: agg.AwaySeconds,
                ContextExcursionSeconds: agg.ContextExcursionSeconds,
                LongestFocusStreakSeconds: agg.LongestFocusStreakSeconds,
                FragmentationEpisodeCount: agg.FragmentationEpisodeCount,
                SocialMediaLoopCount: distraction.SocialMediaReturnCount,
                GamingEpisodeCount: (int)Math.Round(
                    (distraction.GamingLauncherAccumulatedSeconds + distraction.GamingActiveAccumulatedSeconds) / 60d),
                SocialMediaSeconds: distraction.SocialMediaAccumulatedSeconds,
                GamingLauncherSeconds: distraction.GamingLauncherAccumulatedSeconds,
                GamingActiveSeconds: distraction.GamingActiveAccumulatedSeconds,
                PassiveConsumptionSeconds: distraction.PassiveConsumptionAccumulatedSeconds,
                CommMonitoringReturnCount: distraction.CommMonitoringReturnCount,
                SocialMediaReturnCount: distraction.SocialMediaReturnCount,
                OverloadEpisodeCount: agg.OverloadEpisodeCount,
                BreaksTakenCount: breaksTaken,
                BreaksSuggestedCount: breaksSuggested,
                MostDistractedHourLabel: agg.MostDistractedHourLabel,
                DevelopmentSeconds: agg.DevelopmentSeconds,
                ProductivitySeconds: agg.ProductivitySeconds,
                CommunicationSeconds: agg.CommunicationSeconds,
                EntertainmentSeconds: agg.EntertainmentSeconds,
                FirstFocusBlockUtc: agg.FirstFocusBlockUtc,
                PeakPressureUtc: agg.PeakPressureUtc,
                AvgDistractionEpisodeDurationSeconds: agg.AvgDistractionEpisodeDurationSeconds,
                ContextSwitchCount: agg.ContextSwitchCount,
                PreLongestDistractionFocusSeconds: agg.PreLongestDistractionFocusSeconds);

            bool sent = await _client.PostDailyDigestAsync(payload, token).ConfigureAwait(false);
            if (sent)
            {
                _lastSentDate = todayUtc;
                Console.WriteLine($"Daily digest sent for {dateLocal}.");
            }
            else
            {
                LogFailureThrottled();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Daily digest error: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static DigestTimelineAggregation AggregateTimeline(LocalApiTimelineResponse timeline)
    {
        System.Collections.Generic.IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline.Snapshots;
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

        // Category seconds (computed from attention segment — best approximation without FocusAppHash)
        double developmentSeconds = 0d;
        double productivitySeconds = 0d;
        double communicationSeconds = 0d;
        double entertainmentSeconds = 0d;

        // Most distracted hour
        var hourDistraction = new double[24];

        // Phase 1A — new tracking variables
        DateTimeOffset? firstFocusBlockTimestamp = null;
        DateTimeOffset? peakPressureTimestamp = null;
        double peakOverloadLikelihood = -1d;
        int contextSwitchCount = 0;
        // Distraction block tracking for PreLongestDistractionFocusSeconds
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
                // Focus bucket — classify as dev/productivity
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

            // Longest focus streak
            if (s.SustainedAttentionDurationSec > longestStreakSec)
            {
                longestStreakSec = s.SustainedAttentionDurationSec;
            }

            // Count overload episodes (transitions into overload_active = true)
            if (s.OverloadActive && !prevOverloadActive)
            {
                overloadEpisodeCount++;
            }
            prevOverloadActive = s.OverloadActive;

            // Count fragmentation distraction episodes (transitions into fragmentation)
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

            // Phase 1A — context switch count (any kind transition)
            if (!string.IsNullOrEmpty(prevAttentionKind) &&
                !string.Equals(kind, prevAttentionKind, StringComparison.OrdinalIgnoreCase))
            {
                contextSwitchCount++;
            }
            prevAttentionKind = kind;

            // Phase 1A — first focus block UTC
            if (firstFocusBlockTimestamp == null && !isDistraction && !isAway && !isContextExcursion
                && s.FocusStrength > 0.5d)
            {
                firstFocusBlockTimestamp = s.TimestampUtc;
            }

            // Phase 1A — peak pressure UTC
            if (s.OverloadLikelihood > peakOverloadLikelihood)
            {
                peakOverloadLikelihood = s.OverloadLikelihood;
                peakPressureTimestamp = s.TimestampUtc;
            }

            // Phase 1A — pre-longest-distraction focus tracking
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
                // Only count pure focus (not away/context excursion) toward pre-distraction accumulator
                if (!isAway && !isContextExcursion)
                {
                    focusSinceLastDistraction += bucketSec;
                }
            }
        }

        // Handle trailing distraction block that extends to end of session
        if (inDistractionBlock && currentDistractionBlockDuration > longestDistractionBlockDuration)
        {
            preLongestDistractionFocusSeconds = preFocusForCurrentBlock;
        }

        double focusQualityScore = focusedBuckets > 0 ? focusStrengthSum / focusedBuckets : 0d;
        double avgDistractionEpisodeDurationSeconds = fragmentationEpisodeCount > 0
            ? distractionSeconds / fragmentationEpisodeCount
            : 0d;

        // Most distracted hour label
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

    private static bool IsBreakSuggestionEvent(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        return action.Contains("break", StringComparison.OrdinalIgnoreCase)
            && (action.Contains("suggest", StringComparison.OrdinalIgnoreCase)
                || action.Contains("short", StringComparison.OrdinalIgnoreCase)
                || action.Contains("long", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBreakTakenEvent(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        return action.Contains("break", StringComparison.OrdinalIgnoreCase)
            && action.Contains("ack", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMachineId()
    {
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().Name ?? Environment.MachineName;
        }
        catch
        {
            return Environment.MachineName;
        }
    }

    private void LogFailureThrottled()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_lastFailureLogUtc.HasValue || (now - _lastFailureLogUtc.Value) >= FailureLogInterval)
        {
            Console.WriteLine("Daily digest reporter failed to post digest.");
            _lastFailureLogUtc = now;
        }
    }

    private static bool IsLinked(LocalIdentityLinkSnapshot snapshot) =>
        string.Equals(snapshot.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(snapshot.AccountId)
        && !string.IsNullOrWhiteSpace(snapshot.DeviceId);

    private readonly record struct DigestTimelineAggregation(
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
        // Phase 1A — new fields
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
}
