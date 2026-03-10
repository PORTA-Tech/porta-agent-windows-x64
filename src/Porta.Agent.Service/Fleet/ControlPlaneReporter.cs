using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Core;

namespace Porta.Agent.Service.Fleet;

internal sealed class ControlPlaneReporter : BackgroundService
{
    private const int SummaryIntervalSeconds = 10;
    private const int ActionPollIntervalSeconds = 5;
    private const int ActionBatchSize = 100;
    private static readonly TimeSpan ConnectivityFreshnessWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);

    private readonly IAgentCoreController _controller;
    private readonly ControlPlaneClient _client;
    private readonly MachineSummaryMapper _summaryMapper;
    private readonly MachineActionMapper _actionMapper;
    private readonly ControlPlaneReporterStateStore _stateStore;
    private DateTimeOffset? _lastSummaryFailureLogUtc;
    private DateTimeOffset? _lastActionFailureLogUtc;

    public ControlPlaneReporter(
        IAgentCoreController controller,
        ControlPlaneClient client,
        MachineSummaryMapper summaryMapper,
        MachineActionMapper actionMapper,
        ControlPlaneReporterStateStore stateStore)
    {
        _controller = controller;
        _client = client;
        _summaryMapper = summaryMapper;
        _actionMapper = actionMapper;
        _stateStore = stateStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ControlPlaneReporterState state = _stateStore.Load();
        DateTimeOffset nextSummaryDueUtc = DateTimeOffset.MinValue;
        bool disabledLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_client.IsEnabled)
            {
                if (!disabledLogged && !string.IsNullOrWhiteSpace(_client.DisabledReason))
                {
                    Console.WriteLine($"Fleet reporter disabled: {_client.DisabledReason}.");
                    disabledLogged = true;
                }

                await Task.Delay(TimeSpan.FromSeconds(SummaryIntervalSeconds), stoppingToken).ConfigureAwait(false);
                continue;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            AgentServiceStatusSnapshot currentStatus = _controller.GetStatus();
            string userName = _summaryMapper.ResolveUserName(currentStatus);

            if (nowUtc >= nextSummaryDueUtc)
            {
                bool summarySent = await TryPublishSummaryAsync(state, currentStatus, userName, nowUtc, stoppingToken).ConfigureAwait(false);
                if (summarySent)
                {
                    state = state with { LastSummarySuccessUtc = nowUtc };
                }

                nextSummaryDueUtc = nowUtc.AddSeconds(SummaryIntervalSeconds);
            }

            ActionPublishResult actionPublishResult = await TryPublishActionsAsync(state, userName, stoppingToken).ConfigureAwait(false);
            state = actionPublishResult.State;
            if (actionPublishResult.AnySent)
            {
                state = state with { LastActionSuccessUtc = DateTimeOffset.UtcNow };
            }

            _stateStore.Save(state);
            await Task.Delay(TimeSpan.FromSeconds(ActionPollIntervalSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryPublishSummaryAsync(
        ControlPlaneReporterState state,
        AgentServiceStatusSnapshot status,
        string userName,
        DateTimeOffset reportedAtUtc,
        CancellationToken token)
    {
        AgentLatestStateDetails? latestState = _controller.GetLatestStateDetails();
        bool backendConnected = status.BackendConnected || IsConnectivityFresh(state.LastSummarySuccessUtc, reportedAtUtc) || IsConnectivityFresh(state.LastActionSuccessUtc, reportedAtUtc);

        MachineSummaryReportPayload payload = _summaryMapper.Map(
            status,
            latestState,
            userName,
            backendConnected,
            reportedAtUtc);

        bool sent = await _client.PostMachineSummaryAsync(payload, token).ConfigureAwait(false);
        if (!sent)
        {
            LogSummaryFailureThrottled();
        }

        return sent;
    }

    private async Task<ActionPublishResult> TryPublishActionsAsync(
        ControlPlaneReporterState state,
        string userName,
        CancellationToken token)
    {
        bool anySent = false;

        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<AgentControlEvent> rows = _controller
                .GetEventsSinceId(state.LastSentEventId, ActionBatchSize)
                .Where(static row => row.EventId > 0)
                .OrderBy(static row => row.EventId)
                .ToArray();

            if (rows.Count == 0)
            {
                break;
            }

            MachineActionEventPayload[] payload = rows
                .Select(row => _actionMapper.Map(row, userName))
                .ToArray();

            bool sent = await _client.PostMachineActionEventsAsync(payload, token).ConfigureAwait(false);
            if (!sent)
            {
                LogActionFailureThrottled();
                break;
            }

            long maxEventId = rows.Max(static row => row.EventId);
            state = state with { LastSentEventId = maxEventId };
            _stateStore.Save(state);
            anySent = true;

            if (rows.Count < ActionBatchSize)
            {
                break;
            }
        }

        return new ActionPublishResult(state, anySent);
    }

    private static bool IsConnectivityFresh(DateTimeOffset? timestampUtc, DateTimeOffset nowUtc) =>
        timestampUtc.HasValue && (nowUtc - timestampUtc.Value.ToUniversalTime()) <= ConnectivityFreshnessWindow;

    private void LogSummaryFailureThrottled()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!_lastSummaryFailureLogUtc.HasValue || (nowUtc - _lastSummaryFailureLogUtc.Value) >= FailureLogInterval)
        {
            Console.WriteLine("Fleet reporter failed to post machine summary.");
            _lastSummaryFailureLogUtc = nowUtc;
        }
    }

    private void LogActionFailureThrottled()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!_lastActionFailureLogUtc.HasValue || (nowUtc - _lastActionFailureLogUtc.Value) >= FailureLogInterval)
        {
            Console.WriteLine("Fleet reporter failed to post action events.");
            _lastActionFailureLogUtc = nowUtc;
        }
    }

    private readonly record struct ActionPublishResult(
        ControlPlaneReporterState State,
        bool AnySent);
}
