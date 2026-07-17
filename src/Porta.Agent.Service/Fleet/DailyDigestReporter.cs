using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Analysis;
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
    private readonly DailyDigestStateStore _stateStore;
    // B1: seeded from the durable store on start so a restart after 19:30 UTC does not re-send.
    private DateOnly? _lastSentDate;
    private DateTimeOffset? _lastFailureLogUtc;

    public DailyDigestReporter(
        IAgentCoreController controller,
        ControlPlaneClient client,
        ILocalIdentityLinkStore identityLinkStore,
        DailyDigestStateStore? stateStore = null)
    {
        _controller = controller;
        _client = client;
        _identityLinkStore = identityLinkStore;
        _stateStore = stateStore ?? new DailyDigestStateStore();
        _lastSentDate = _stateStore.LoadLastSentDate();
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

            // C1 — the day's stated goal + midday answer (Workstream D). Null when the user never
            // set one; the email then omits the Goal section entirely.
            AgentDailyGoal? todayGoal = _controller.GetTodayGoal();

            // A3 — break compliance, counted from the real acceptance record (the lifecycle
            // "break_accepted" reason) and deduped per intervention. Shared with DailyStatsReporter
            // so the agent digest and the server-fallback digest can never disagree.
            BreakCompliance breaks = BreakComplianceCounter.Count(timeline.Interventions);
            int breaksSuggested = breaks.Suggested;
            int breaksTaken = breaks.Taken;

            // Timezone offset: Dubai is UTC+4 (+240 minutes)
            const int dubaiOffsetMinutes = 240;
            DateTimeOffset dubaiNow = nowUtc.ToOffset(TimeSpan.FromMinutes(dubaiOffsetMinutes));
            string dateLocal = dubaiNow.ToString("yyyy-MM-dd");

            string machineId = ResolveMachineId();

            // "Where your time went" + real per-category time, from per-app dwell joined to the
            // coarse label/category maps the user-agent persists.
            IReadOnlyDictionary<string, double> dwellByHash = _controller.GetFocusAppDwellSeconds(startOfDayUtc, nowUtc);
            // B4 (v1.2.112): file map merged with the live bridged registries, so stale files
            // can no longer starve the digest of names/categories.
            IReadOnlyDictionary<string, int> categoryByHash = FocusAppLabelMapReader.ReadCategoriesMergedWithLive();
            IReadOnlyList<FocusAppUsagePayload> topApps = FocusAppUsageBuilder.BuildTopApps(dwellByHash, FocusAppLabelMapReader.ReadLabelsMergedWithLive(), categoryByHash);

            // Real category seconds replace the old state-based proxies (entertainment=distraction,
            // development=focus-strength) when we actually have category data for this machine.
            bool haveCategories = categoryByHash.Count > 0;
            Dictionary<AppCategory, double> categorySeconds = haveCategories
                ? BuildCategorySeconds(dwellByHash, categoryByHash)
                : new Dictionary<AppCategory, double>();
            double developmentSeconds = haveCategories ? categorySeconds.GetValueOrDefault(AppCategory.Development) : agg.DevelopmentSeconds;
            double productivitySeconds = haveCategories ? categorySeconds.GetValueOrDefault(AppCategory.Productivity) : agg.ProductivitySeconds;
            double communicationSeconds = haveCategories ? categorySeconds.GetValueOrDefault(AppCategory.Communication) : agg.CommunicationSeconds;
            double entertainmentSeconds = haveCategories ? categorySeconds.GetValueOrDefault(AppCategory.Entertainment) : agg.EntertainmentSeconds;

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
                DevelopmentSeconds: developmentSeconds,
                ProductivitySeconds: productivitySeconds,
                CommunicationSeconds: communicationSeconds,
                EntertainmentSeconds: entertainmentSeconds,
                FirstFocusBlockUtc: agg.FirstFocusBlockUtc,
                PeakPressureUtc: agg.PeakPressureUtc,
                AvgDistractionEpisodeDurationSeconds: agg.AvgDistractionEpisodeDurationSeconds,
                ContextSwitchCount: agg.ContextSwitchCount,
                PreLongestDistractionFocusSeconds: agg.PreLongestDistractionFocusSeconds,
                // Social-media and gaming accumulators are pattern-detection windows
                // (grow/decay per bucket), not daily totals — declare them missing so
                // the email renders "no data" instead of a misleading number.
                MissingFields: new[] { "gaming", "socialMedia" },
                TopApps: topApps,
                Tasks: BuildTaskPayloads(),
                // C1 — lead the digest with goal vs reality. Null when no goal was set today, so
                // the email omits the section rather than showing an empty one.
                Goal: todayGoal?.GoalText,
                MiddayAnswer: todayGoal?.MiddayAnswer);

            bool sent = await _client.PostDailyDigestAsync(payload, token).ConfigureAwait(false);
            if (sent)
            {
                _lastSentDate = todayUtc;
                // B1: persist it so a restart later tonight doesn't POST a second digest.
                _stateStore.SaveLastSentDate(todayUtc);
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
    // Workstream 4 — plan vs. reality: today's CLOSED tasks only, exactly the measured
    // numbers. An empty day yields an empty list and the email omits the section.
    private IReadOnlyList<DigestTaskPayload> BuildTaskPayloads()
    {
        try
        {
            return _controller.GetTodayTasks()
                .Where(static t => t.State is "done" or "abandoned")
                .Select(static t => new DigestTaskPayload(
                    Title: t.Title,
                    PlannedSeconds: t.PlannedSeconds,
                    ActualSeconds: t.ActiveSeconds,
                    OfftaskSeconds: t.OfftaskSeconds,
                    State: t.State))
                .ToList();
        }
        catch
        {
            return Array.Empty<DigestTaskPayload>();
        }
    }

    private static Dictionary<AppCategory, double> BuildCategorySeconds(
        IReadOnlyDictionary<string, double> dwellByHash,
        IReadOnlyDictionary<string, int> categoryByHash)
    {
        var totals = new Dictionary<AppCategory, double>();
        foreach (KeyValuePair<string, double> entry in dwellByHash)
        {
            if (!categoryByHash.TryGetValue(entry.Key, out int categoryInt))
            {
                continue;
            }

            var category = (AppCategory)categoryInt;
            totals[category] = totals.TryGetValue(category, out double existing) ? existing + entry.Value : entry.Value;
        }

        return totals;
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
