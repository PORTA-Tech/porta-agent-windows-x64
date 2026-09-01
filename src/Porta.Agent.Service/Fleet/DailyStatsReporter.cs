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

            // Category bridge (v1.2.112): hourly registry re-seed piggybacks this loop.
            // Internally throttled to once per hour; cheap no-op in between.
            CategoryRegistrySeeder.SeedFromFiles();

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

            // A3 — break compliance, counted from the real acceptance record (the lifecycle
            // "break_accepted" reason) and deduped per intervention. Shared with DailyDigestReporter
            // so the daily-stats upload and the agent digest can never disagree.
            BreakCompliance breaks = BreakComplianceCounter.Count(timeline.Interventions);
            int breaksSuggested = breaks.Suggested;
            int breaksTaken = breaks.Taken;

            // Distraction social media loops from current session
            AgentDistractionSummary distraction = _controller.GetCurrentDistractionSummary();

            // Phase 1b — Step 4c: privacy-safe ground-truth label engagement (counts only, never content).
            AgentLabelDiagnostics labels = _controller.GetLabelDiagnostics();

            // Digest C1 — the day's stated goal + midday answer (Workstream D), uploaded so the
            // server-fallback digest can lead with it. Null when the user set no goal today.
            AgentDailyGoal? todayGoal = _controller.GetTodayGoal();

            // Coarse per-app foreground time for the day, joined to human labels — same ranking
            // the digest uses. Lets the console show "where the day went" per machine and gives
            // the server-fallback digest real app names.
            System.Collections.Generic.IReadOnlyList<FocusAppUsagePayload> topApps =
                FocusAppUsageBuilder.BuildTopApps(
                    _controller.GetFocusAppDwellSeconds(startOfDayUtc, nowUtc),
                    FocusAppLabelMapReader.ReadLabelsMergedWithLive(),
                    FocusAppLabelMapReader.ReadCategoriesMergedWithLive());

            // B3: label-persistence health from the user agent's heartbeat file.
            Porta.Agent.Common.UserAgent.UserAgentSessionContextSnapshot? sessionContext =
                TryReadSessionContext();

            // Part 4 — stamp the UTC date of the data window (the timeline above runs over the
            // UTC day), not "Dubai now": pushes between 20:00 and 23:59 UTC are past midnight in
            // Dubai and used to write today's numbers onto TOMORROW's daily_stats row, which the
            // server-cron fallback digest then read as the wrong day.
            string dateLocal = todayUtc.ToString("yyyy-MM-dd");
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
                    .ToList(),
                PromptsShownToday: (int)labels.PromptsShownLast24h,
                PromptsAnsweredToday: (int)labels.PromptsAnsweredLast24h,
                PromptsShownThisWeek: (int)labels.PromptsShownLast7d,
                PromptsAnsweredThisWeek: (int)labels.PromptsAnsweredLast7d,
                LabelPersistFailureCount: sessionContext?.LabelPersistFailureCount ?? 0,
                LabelPersistLastError: sessionContext?.LabelPersistLastError,
                // Digest C1 — carry the day's goal + midday answer up with the stats. daily_goals is
                // an agent-local table, so this 2-hourly upload is the ONLY way the server-cron
                // fallback digest (built from daily_stats) can lead with the goal.
                Goal: todayGoal?.GoalText,
                MiddayAnswer: todayGoal?.MiddayAnswer,
                // Task attribution — how much the drift gate held today and how many times it
                // fired. Read off the same 05:00 goal day the held rows were keyed by, so this
                // and the user's own Today line can never be answering different questions.
                UnassignedSecondsToday: _controller.GetUnassignedSecondsToday(),
                DriftHoldCountToday: _controller.GetDriftHoldCountToday());

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

    // B3: best-effort read of the user agent's heartbeat file for health counters.
    private static Porta.Agent.Common.UserAgent.UserAgentSessionContextSnapshot? TryReadSessionContext()
    {
        try
        {
            return new Porta.Agent.Common.UserAgent.UserAgentSessionContextStore().ReadCurrent();
        }
        catch
        {
            return null;
        }
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
