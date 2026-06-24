using System;
using System.Collections.Generic;
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
            DigestTimelineAggregation agg = DigestTimelineAggregator.Aggregate(timeline);

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

            // "Where your time went": per-app foreground seconds for today, joined to coarse labels.
            IReadOnlyList<FocusAppUsagePayload> topApps = BuildTopApps(
                _controller.GetFocusAppDwellSeconds(startOfDayUtc, nowUtc),
                FocusAppLabelMapReader.Read());

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
                PreLongestDistractionFocusSeconds: agg.PreLongestDistractionFocusSeconds,
                // Social-media and gaming accumulators are pattern-detection windows
                // (grow/decay per bucket), not daily totals — declare them missing so
                // the email renders "no data" instead of a misleading number.
                MissingFields: new[] { "gaming", "socialMedia" },
                TopApps: topApps);

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
    private static IReadOnlyList<FocusAppUsagePayload> BuildTopApps(
        IReadOnlyDictionary<string, double> dwellByHash,
        IReadOnlyDictionary<string, string> labelByHash)
    {
        var byLabel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in dwellByHash)
        {
            if (!labelByHash.TryGetValue(entry.Key, out string? label) || string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            byLabel[label] = byLabel.TryGetValue(label, out double existing) ? existing + entry.Value : entry.Value;
        }

        return byLabel
            .Where(static kv => kv.Value >= 60d)
            .OrderByDescending(static kv => kv.Value)
            .Take(8)
            .Select(static kv => new FocusAppUsagePayload(kv.Key, Math.Round(kv.Value)))
            .ToList();
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
}
