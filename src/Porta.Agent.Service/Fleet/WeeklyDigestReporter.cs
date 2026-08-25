using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Identity;
using Porta.Agent.Core;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Sends the weekly digest on <b>Monday at 07:00 local</b> for the seven goal days that ended the
/// night before. Yadin's decision, 2026-08-25; Sunday's daily still sends and this does not
/// replace it.
///
/// <b>Why the agent and not a server cron.</b> Four of the email's eight sections are built from
/// task and project data, and the control plane holds neither — no task store, no project store,
/// and the daily's task array is rendered into that email and discarded. So the weekly cannot be
/// assembled server-side at all, and there is no fallback: if this does not post, no weekly goes
/// out. That is the honest outcome, because the alternative is an email missing half of itself.
///
/// <b>Where the numbers come from.</b> The day shape and the week's tracked/focused/away totals
/// are both summed from the SAME per-day timeline reads, so the strip and the four figures cannot
/// disagree with each other. The task side comes from task_day_hours through the store, which
/// cannot disagree with tasks.active_seconds. Nothing here re-derives a number that already exists.
/// </summary>
internal sealed class WeeklyDigestReporter : BackgroundService
{
    private const int PollIntervalSeconds = 120;
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(30);

    private readonly IAgentCoreController _controller;
    private readonly ControlPlaneClient _client;
    private readonly ILocalIdentityLinkStore _identityLinkStore;
    private readonly WeeklyDigestStateStore _stateStore;
    // Seeded from the durable store on start, so a restart on Monday afternoon does not re-send.
    private DateOnly? _lastSentWeekEnd;
    private DateTimeOffset? _lastFailureLogUtc;

    public WeeklyDigestReporter(
        IAgentCoreController controller,
        ControlPlaneClient client,
        ILocalIdentityLinkStore identityLinkStore,
        WeeklyDigestStateStore? stateStore = null)
    {
        _controller = controller;
        _client = client;
        _identityLinkStore = identityLinkStore;
        _stateStore = stateStore ?? new WeeklyDigestStateStore();
        _lastSentWeekEnd = _stateStore.LoadLastSentWeekEnd();
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

            (DateOnly Start, DateOnly End)? due = WeeklyDigestSchedule.DueWeek(
                DateTimeOffset.Now, _lastSentWeekEnd);
            if (due is not { } week)
            {
                continue;
            }

            LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();
            if (!IsLinked(identity))
            {
                continue;
            }

            await TrySendWeeklyAsync(identity, week.Start, week.End, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TrySendWeeklyAsync(
        LocalIdentityLinkSnapshot identity,
        DateOnly weekStart,
        DateOnly weekEnd,
        CancellationToken token)
    {
        try
        {
            // Re-check at send time. The loop's decision can be STALE when the machine sleeps
            // between the gate and this call resuming — the daily's confirmed failure was a digest
            // built at one hour and POSTed at another, stamped with the wrong day. If the week is
            // no longer the one that is due, drop this attempt rather than send a mislabelled week.
            (DateOnly Start, DateOnly End)? recheck = WeeklyDigestSchedule.DueWeek(
                DateTimeOffset.Now, _lastSentWeekEnd);
            if (recheck is not { } still || still.End != weekEnd)
            {
                Console.WriteLine("Weekly digest skipped: the due week changed between the gate and the send.");
                return;
            }

            string startLocal = weekStart.ToString("yyyy-MM-dd");
            string endLocal = weekEnd.ToString("yyyy-MM-dd");

            IReadOnlyList<WeeklyDigestDayPayload> days = BuildDays(weekStart);

            // A week with nothing in it is not a week to report on. Same principle as the daily's
            // minimum-activity gate: an email that says nothing costs more trust than no email.
            if (!days.Any(static d => d.Worked))
            {
                Console.WriteLine($"Weekly digest skipped for {startLocal}–{endLocal}: no worked days.");
                _lastSentWeekEnd = weekEnd;
                _stateStore.SaveLastSentWeekEnd(weekEnd);
                return;
            }

            AgentWeeklyDigestData data = ReadWeekData(startLocal, endLocal);

            // Tracked is time AT the machine — focused plus active, away excluded. Summed from the
            // same per-day reads the strip is drawn from, so the two cannot drift apart.
            double focusSeconds = days.Sum(static d => d.FocusSeconds);
            (double tracked, double away) = SumTrackedAndAway(weekStart);

            var payload = new WeeklyDigestReportPayload(
                AccountId: identity.AccountId,
                DeviceId: identity.DeviceId,
                MachineId: ResolveMachineId(),
                WeekStartLocal: startLocal,
                WeekEndLocal: endLocal,
                TimezoneOffsetMinutes: (int)DateTimeOffset.Now.Offset.TotalMinutes,
                ReportedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                PlannedCount: data.PlannedCount,
                FinishedCount: data.FinishedCount,
                TrackedSeconds: tracked,
                FocusSeconds: focusSeconds,
                AwaySeconds: away,
                Days: days,
                Finished: data.Finished
                    .Select(static l => new WeeklyDigestFinishedPayload(l.Title, l.ProjectName, l.Seconds))
                    .ToList(),
                Carried: data.Carried
                    .Select(static l => new WeeklyDigestCarriedPayload(l.Title, l.ProjectName, l.Seconds, l.DaysOpen))
                    .ToList(),
                Projects: data.Projects
                    .Select(static p => new WeeklyDigestProjectPayload(p.Name, p.Seconds))
                    .ToList(),
                // Ari's take is NOT drafted here. The rule is that it comes from result lines and
                // statuses only, never from app usage, and nothing in this repo drafts a week's
                // worth of that yet. Omitted means the server drops the section rather than
                // printing something that would be true of any week.
                AriTake: null,
                MissingFields: null);

            bool sent = await _client.PostWeeklyDigestAsync(payload, token).ConfigureAwait(false);
            if (sent)
            {
                _lastSentWeekEnd = weekEnd;
                _stateStore.SaveLastSentWeekEnd(weekEnd);
                Console.WriteLine($"Weekly digest sent for {startLocal}–{endLocal}.");
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
            Console.WriteLine($"Weekly digest error: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// The seven day columns. A day the machine recorded nothing is <c>Worked: false</c> and draws
    /// an empty track; a day with time but no focus is worked with a short bar. Those are different
    /// claims and the strip renders the difference, so they must not be collapsed here.
    /// </summary>
    private IReadOnlyList<WeeklyDigestDayPayload> BuildDays(DateOnly weekStart)
    {
        var days = new List<WeeklyDigestDayPayload>(7);
        for (int i = 0; i < 7; i++)
        {
            DateOnly day = weekStart.AddDays(i);
            double focus = 0d;
            bool recorded = false;

            try
            {
                IReadOnlyList<AgentDayTimelineHour> hours = ReadDay(day);
                recorded = hours.Count > 0;
                focus = hours.Sum(static h => h.FocusSeconds);
            }
            catch
            {
                // A day that cannot be read is not a day that was not worked. Leaving it false
                // draws an empty track, which is the honest picture of "nothing known".
                recorded = false;
                focus = 0d;
            }

            days.Add(new WeeklyDigestDayPayload(
                DateLocal: day.ToString("yyyy-MM-dd"),
                Label: WeeklyDigestSchedule.LabelFor(day),
                FocusSeconds: focus,
                Worked: recorded));
        }

        return days;
    }

    private (double Tracked, double Away) SumTrackedAndAway(DateOnly weekStart)
    {
        double tracked = 0d;
        double away = 0d;
        for (int i = 0; i < 7; i++)
        {
            try
            {
                foreach (AgentDayTimelineHour h in ReadDay(weekStart.AddDays(i)))
                {
                    tracked += h.FocusSeconds + h.ActiveSeconds;
                    away += h.AwaySeconds;
                }
            }
            catch
            {
                // Skip the day rather than abandon the week.
            }
        }

        return (tracked, away);
    }

    /// <summary>One goal day's hours: 05:00 local on the day, to 05:00 local the next.</summary>
    private IReadOnlyList<AgentDayTimelineHour> ReadDay(DateOnly day)
    {
        TimeSpan offset = DateTimeOffset.Now.Offset;
        var startLocal = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), offset).AddHours(5);
        return _controller.GetDayTimeline(startLocal.ToUniversalTime(), startLocal.AddDays(1).ToUniversalTime());
    }

    private AgentWeeklyDigestData ReadWeekData(string startLocal, string endLocal)
    {
        try
        {
            return _controller.GetWeeklyDigestData(startLocal, endLocal);
        }
        catch
        {
            // The strip and the figures still stand on their own; the task blocks are simply
            // omitted by the server when they arrive empty.
            return AgentWeeklyDigestData.Empty;
        }
    }

    private static bool IsLinked(LocalIdentityLinkSnapshot identity)
        => !string.IsNullOrWhiteSpace(identity.AccountId) && !string.IsNullOrWhiteSpace(identity.DeviceId);

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
            Console.WriteLine("Weekly digest reporter failed to post the weekly digest.");
            _lastFailureLogUtc = now;
        }
    }
}
