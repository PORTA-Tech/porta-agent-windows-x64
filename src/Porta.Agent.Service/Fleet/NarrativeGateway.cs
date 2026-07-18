using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Identity;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Part 5 — serves the local API's /v1/narrative by assembling the user's situation (metadata and
/// numbers only, the same classes the digest uploads) and asking the control plane's narrative
/// endpoint for one line. The anti-spam gate in the user agent decided to SPEAK before this is
/// ever called — this only words the moment. Fail-soft throughout: any problem returns null and
/// the card renders its honest template.
/// </summary>
internal sealed class NarrativeGateway : INarrativeGateway
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "morning", "midday", "eod", "reply_morning", "reply_midday", "reply_eod"
    };

    // The card's own budget is slightly above this; the server's model timeout is 8s. One bound
    // here keeps a slow control plane from ever holding a card open longer than that.
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(9);

    private readonly IAgentCoreController _controller;
    private readonly ControlPlaneClient _client;
    private readonly ILocalIdentityLinkStore _identityLinkStore;

    public NarrativeGateway(
        IAgentCoreController controller,
        ControlPlaneClient client,
        ILocalIdentityLinkStore identityLinkStore)
    {
        _controller = controller;
        _client = client;
        _identityLinkStore = identityLinkStore;
    }

    public async Task<string?> GetNarrativeLineAsync(string kind, string? userText, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(kind) || !AllowedKinds.Contains(kind) || !_client.IsEnabled)
            {
                return null;
            }

            LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();
            if (!string.Equals(identity.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(identity.AccountId)
                || string.IsNullOrWhiteSpace(identity.DeviceId))
            {
                return null;
            }

            NarrativeRequestPayload payload = BuildPayload(identity, kind, userText);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(CallBudget);
            return await _client.PostNarrativeAsync(payload, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private NarrativeRequestPayload BuildPayload(LocalIdentityLinkSnapshot identity, string kind, string? userText)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset nowLocal = DateTimeOffset.Now;
        DateOnly todayUtc = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        DateTimeOffset startOfDayUtc = new(todayUtc.Year, todayUtc.Month, todayUtc.Day, 0, 0, 0, TimeSpan.Zero);

        // Same day-so-far aggregation the digest uses — post-Part-1, so FocusSeconds is episode
        // time and FocusQualityScore is weighted/episode. Best-effort: an empty timeline just
        // means the situation carries no numbers.
        DigestTimelineAggregation agg = default;
        IReadOnlyList<FocusAppUsagePayload> topApps = Array.Empty<FocusAppUsagePayload>();
        try
        {
            AgentTimelineData timelineData = _controller.GetTimeline(startOfDayUtc, nowUtc, 60);
            LocalApiTimelineResponse timeline = new(
                timelineData.FromUtc,
                timelineData.ToUtc,
                timelineData.BucketSeconds,
                timelineData.Snapshots.Select(static s => SnapshotMapper.Map(s, null)).ToList(),
                timelineData.Interventions.Select(EventMapper.Map).ToList());
            agg = DigestTimelineAggregator.Aggregate(timeline);

            topApps = FocusAppUsageBuilder.BuildTopApps(
                _controller.GetFocusAppDwellSeconds(startOfDayUtc, nowUtc),
                FocusAppLabelMapReader.ReadLabelsMergedWithLive(),
                FocusAppLabelMapReader.ReadCategoriesMergedWithLive());
        }
        catch
        {
        }

        AgentDailyGoal? goal = null;
        string? carriedPlan = null;
        try
        {
            goal = _controller.GetTodayGoal();
            carriedPlan = _controller.GetCarriedOverPlan();
        }
        catch
        {
        }

        // Same 5:00-local goal-day boundary as AgentCoreController.GoalDay: a card shown at 02:00
        // still belongs to yesterday's goal-day, and the server caches one line per moment per day.
        string dateLocal = (nowLocal.Hour < 5 ? nowLocal.AddDays(-1) : nowLocal).ToString("yyyy-MM-dd");

        var situation = new NarrativeSituationPayload(
            GoalToday: NullIfBlank(goal?.GoalText),
            CarriedPlan: NullIfBlank(carriedPlan),
            MiddayAnswer: NullIfBlank(goal?.MiddayAnswer),
            TrackedSeconds: agg.TrackedSeconds,
            FocusSeconds: agg.FocusSeconds,
            FocusQualityPct: agg.FocusQualityScore * 100d,
            DistractionSeconds: agg.DistractionSeconds,
            AwaySeconds: agg.AwaySeconds,
            LongestStreakSeconds: agg.LongestFocusStreakSeconds,
            TopApps: topApps.Count > 0 ? topApps.Take(3).ToList() : null,
            LocalTimeHHmm: nowLocal.ToString("HH\\:mm"),
            UserText: NullIfBlank(userText));

        return new NarrativeRequestPayload(
            AccountId: identity.AccountId!,
            DeviceId: identity.DeviceId!,
            MachineId: Environment.MachineName,
            DateLocal: dateLocal,
            Kind: kind,
            Situation: situation);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
