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
/// Posts aggregated daily stats to the server every 2 hours throughout the day.
/// This ensures the control plane has fresh data in daily_stats even when the
/// machine is off at the 19:30 UTC primary email send time (DailyDigestReporter).
/// The server-side digest cron at 20:30 UTC reads these stats to send fallback emails.
/// </summary>
internal sealed class DailyStatsReporter : BackgroundService
{
    private const int PushIntervalHours = 2;
    private const int PollIntervalSeconds = 60;
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(30);

    // Dubai standard time = UTC+4
    private const int DubaiOffsetMinutes = 240;

    private readonly IAgentCoreController _controller;
    private readonly ControlPlaneClient _client;
    private readonly ILocalIdentityLinkStore _identityLinkStore;
    private DateTimeOffset _nextPushDueUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastFailureLogUtc;

    public DailyStatsReporter(
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
            if (nowUtc < _nextPushDueUtc)
            {
                continue;
            }

            LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();
            if (!IsLinked(identity))
            {
                // Not linked yet — schedule retry in 2 hours
                _nextPushDueUtc = nowUtc.AddHours(PushIntervalHours);
                continue;
            }

            await TryPushStatsAsync(identity, nowUtc, stoppingToken).ConfigureAwait(false);
            _nextPushDueUtc = nowUtc.AddHours(PushIntervalHours);
        }
    }

    private async Task TryPushStatsAsync(
        LocalIdentityLinkSnapshot identity,
        DateTimeOffset nowUtc,
        CancellationToken token)
    {
        try
        {
            DateOnly todayUtc = DateOnly.FromDateTime(nowUtc.UtcDateTime);
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

            DigestTimelineAggregation agg = DigestTimelineAggregator.Aggregate(timeline);

            // Break compliance from interventions
            int breaksSuggested = 0;
            int breaksTaken = 0;
            foreach (LocalApiEventResponse intervention in timeline.Interventions)
            {
                if (IsBreakSuggestionEvent(intervention.Action)) breaksSuggested++;
                if (IsBreakTakenEvent(intervention.Action)) breaksTaken++;
            }

            // Distraction social media loops from current session
            AgentDistractionSummary distraction = _controller.GetCurrentDistractionSummary();

            // Phase 1b — Step 4c: privacy-safe ground-truth label engagement (counts only, never content).
            AgentLabelDiagnostics labels = _controller.GetLabelDiagnostics();

            // Coarse per-app foreground time for the day, joined to human labels — same ranking
            // the digest uses. Lets the console show "where the day went" per machine and gives
            // the server-fallback digest real app names.
            System.Collections.Generic.IReadOnlyList<FocusAppUsagePayload> topApps =
                FocusAppUsageBuilder.BuildTopApps(
                    _controller.GetFocusAppDwellSeconds(startOfDayUtc, nowUtc),
                    FocusAppLabelMapReader.ReadLabels(),
                    FocusAppLabelMapReader.ReadCategories());

            DateTimeOffset dubaiNow = nowUtc.ToOffset(TimeSpan.FromMinutes(DubaiOffsetMinutes));
            string dateLocal = dubaiNow.ToString("yyyy-MM-dd");
            string machineId = ResolveMachineId();

            DailyStatsReportPayload payload = new(
                AccountId: identity.AccountId,
                DeviceId: identity.DeviceId,
                MachineId: machineId,
                DateLocal: dateLocal,
                TimezoneOffsetMinutes: DubaiOffsetMinutes,
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
                SocialMediaSeconds: distraction.SocialMediaAccumulatedSeconds,
                OverloadEpisodeCount: agg.OverloadEpisodeCount,
                BreaksTakenCount: breaksTaken,
                BreaksSuggestedCount: breaksSuggested,
                MostDistractedHourLabel: agg.MostDistractedHourLabel,
                FirstFocusBlockUtc: agg.FirstFocusBlockUtc,
                PeakPressureUtc: agg.PeakPressureUtc,
                AvgDistractionEpisodeDurationSeconds: agg.AvgDistractionEpisodeDurationSeconds,
                ContextSwitchCount: agg.ContextSwitchCount,
                TopApps: topApps,
                LabelsToday: (int)labels.LabelsLast24h,
                LabelsThisWeek: (int)labels.LabelsLast7d,
                LabelsByTarget: labels.Last24hByTarget
                    .Select(t => new LabelTargetCountPayload(t.Target, (int)t.Count, (int)t.NetValue))
                    .ToList());

            bool sent = await _client.PostDailyStatsAsync(payload, token).ConfigureAwait(false);
            if (!sent)
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
            Console.WriteLine($"Daily stats reporter error: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static bool IsBreakSuggestionEvent(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        return action.Contains("break", StringComparison.OrdinalIgnoreCase)
            && (action.Contains("suggest", StringComparison.OrdinalIgnoreCase)
                || action.Contains("short", StringComparison.OrdinalIgnoreCase)
                || action.Contains("long", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBreakTakenEvent(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return false;
        return action.Contains("break", StringComparison.OrdinalIgnoreCase)
            && action.Contains("ack", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMachineId()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().Name ?? Environment.MachineName; }
        catch { return Environment.MachineName; }
    }

    private void LogFailureThrottled()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_lastFailureLogUtc.HasValue || (now - _lastFailureLogUtc.Value) >= FailureLogInterval)
        {
            Console.WriteLine("Daily stats reporter failed to post stats.");
            _lastFailureLogUtc = now;
        }
    }

    private static bool IsLinked(LocalIdentityLinkSnapshot snapshot) =>
        string.Equals(snapshot.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(snapshot.AccountId)
        && !string.IsNullOrWhiteSpace(snapshot.DeviceId);
}
