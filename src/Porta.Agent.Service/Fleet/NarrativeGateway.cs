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

    // Part 1b — a ~2h away stretch marks the EOD wrap as away-aware (mirrors the user agent's
    // UserCheckInScheduler.AwayGapThreshold, which lives in a different assembly).
    private const double AwayGapThresholdSeconds = 2 * 3600d;

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
        => (await GetNarrativeLineWithReasonAsync(kind, userText, token).ConfigureAwait(false)).Line;

    /// <summary>
    /// Every exit carries a reason code. Before this, four fail-soft layers each turned a failure
    /// into a bare null — this gateway had three silent early returns and a bare catch, and
    /// ControlPlaneClient discarded the HTTP status — so "the model declined" and "the request
    /// never left the machine" were indistinguishable at the only place anyone can read, the
    /// agent's `checkin_narrative used=False` line. On 2026-08-07 that cost a full session of
    /// inference: the control plane logged nothing (its success path is silent and its failure
    /// paths all log, so silence alone could not tell us which had happened).
    /// </summary>
    public async Task<NarrativeGatewayResult> GetNarrativeLineWithReasonAsync(
        string kind,
        string? userText,
        CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return NarrativeGatewayResult.Failed("kind_missing");
            }

            if (!AllowedKinds.Contains(kind))
            {
                return NarrativeGatewayResult.Failed("kind_not_allowed");
            }

            if (!_client.IsEnabled)
            {
                // The control-plane channel is off — no HTTP call is made at all, which is why
                // this case leaves no trace on either side.
                return NarrativeGatewayResult.Failed($"client_disabled:{_client.DisabledReason ?? "unknown"}");
            }

            LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();
            if (!string.Equals(identity.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal))
            {
                return NarrativeGatewayResult.Failed($"identity_not_linked:{identity.LinkStatus ?? "null"}");
            }

            if (string.IsNullOrWhiteSpace(identity.AccountId) || string.IsNullOrWhiteSpace(identity.DeviceId))
            {
                return NarrativeGatewayResult.Failed("identity_incomplete");
            }

            NarrativeRequestPayload payload = BuildPayload(identity, kind, userText);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(CallBudget);

            NarrativeCallOutcome outcome = await _client
                .PostNarrativeWithOutcomeAsync(payload, cts.Token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(outcome.Line))
            {
                return NarrativeGatewayResult.Ok(outcome.Line!);
            }

            // Distinguish "we ran out of our own budget" from "the server answered, emptily".
            if (outcome.Reason == "cancelled" && !token.IsCancellationRequested)
            {
                return NarrativeGatewayResult.Failed($"gateway_budget_exceeded:{CallBudget.TotalSeconds:F0}s");
            }

            return NarrativeGatewayResult.Failed(outcome.Reason);
        }
        catch (Exception ex)
        {
            return NarrativeGatewayResult.Failed($"exception:{ex.GetType().Name}");
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

        // Part 1b — the EOD wrap's whole-day facts, derived from the same goal row + away seconds this
        // payload already carries (no new plumbing from the host needed). middaySkipped = a morning
        // goal was set but no midday answer was ever recorded; awayGap = a substantial away stretch.
        // Only the eod prompt uses these, but they are harmless to include on every kind.
        bool middaySkipped = !string.IsNullOrWhiteSpace(goal?.GoalText)
            && string.IsNullOrWhiteSpace(goal?.MiddayAnswer);
        bool awayGap = agg.AwaySeconds >= AwayGapThresholdSeconds;

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
            UserText: NullIfBlank(userText),
            MiddaySkipped: middaySkipped,
            AwayGap: awayGap);

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
