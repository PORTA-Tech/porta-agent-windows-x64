using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Identity;
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
    private readonly ILocalIdentityLinkStore _identityLinkStore;
    private DateTimeOffset? _lastSummaryFailureLogUtc;
    private DateTimeOffset? _lastActionFailureLogUtc;

    public ControlPlaneReporter(
        IAgentCoreController controller,
        ControlPlaneClient client,
        MachineSummaryMapper summaryMapper,
        MachineActionMapper actionMapper,
        ILocalIdentityLinkStore identityLinkStore,
        ControlPlaneReporterStateStore stateStore)
    {
        _controller = controller;
        _client = client;
        _summaryMapper = summaryMapper;
        _actionMapper = actionMapper;
        _identityLinkStore = identityLinkStore;
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
            LocalIdentityLinkSnapshot identityLink = _identityLinkStore.GetCurrent();
            identityLink = await SyncDeviceIdentityAsync(currentStatus, identityLink, stoppingToken).ConfigureAwait(false);

            if (nowUtc >= nextSummaryDueUtc)
            {
                bool summarySent = await TryPublishSummaryAsync(state, currentStatus, identityLink, userName, nowUtc, stoppingToken).ConfigureAwait(false);
                if (summarySent)
                {
                    state = state with { LastSummarySuccessUtc = nowUtc };
                }

                nextSummaryDueUtc = nowUtc.AddSeconds(SummaryIntervalSeconds);
            }

            ActionPublishResult actionPublishResult = await TryPublishActionsAsync(state, identityLink, userName, stoppingToken).ConfigureAwait(false);
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
        LocalIdentityLinkSnapshot identityLink,
        string userName,
        DateTimeOffset reportedAtUtc,
        CancellationToken token)
    {
        AgentLatestStateDetails? latestState = _controller.GetLatestStateDetails();
        bool backendConnected = status.BackendConnected || IsConnectivityFresh(state.LastSummarySuccessUtc, reportedAtUtc) || IsConnectivityFresh(state.LastActionSuccessUtc, reportedAtUtc);

        MachineSummaryReportPayload payload = _summaryMapper.Map(
            status,
            latestState,
            identityLink,
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

    private async Task<LocalIdentityLinkSnapshot> SyncDeviceIdentityAsync(
        AgentServiceStatusSnapshot status,
        LocalIdentityLinkSnapshot identityLink,
        CancellationToken token)
    {
        DeviceRegistrationRequestPayload request = new(
            DeviceId: identityLink.DeviceId,
            MachineName: Environment.MachineName,
            DeviceDisplayName: Environment.MachineName,
            Platform: "windows",
            AgentVersion: status.AgentVersion,
            AccountId: IsLinked(identityLink) ? identityLink.AccountId : null,
            OrgId: IsLinked(identityLink) ? identityLink.OrgId : null,
            SessionToken: null,
            LocalProofToken: null);

        DeviceLinkResultPayload? result = await _client.RegisterDeviceAsync(request, token).ConfigureAwait(false);
        if (result == null || !IsLinked(identityLink))
        {
            return identityLink;
        }

        if (!ShouldClearLocalIdentityLink(identityLink, result))
        {
            return identityLink;
        }

        LocalIdentityLinkSnapshot cleared = _identityLinkStore.ClearLink();
        Console.WriteLine(
            $"Fleet reporter cleared local identity link for device {identityLink.DeviceId} because control plane reported status={result.Device.Status} link_state={result.LinkState}.");
        return cleared;
    }

    private async Task<ActionPublishResult> TryPublishActionsAsync(
        ControlPlaneReporterState state,
        LocalIdentityLinkSnapshot identityLink,
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
                .Select(row => _actionMapper.Map(row, identityLink, userName))
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

    private static bool IsLinked(LocalIdentityLinkSnapshot snapshot) =>
        string.Equals(snapshot.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(snapshot.AccountId)
        && !string.IsNullOrWhiteSpace(snapshot.OrgId);

    private static bool ShouldClearLocalIdentityLink(LocalIdentityLinkSnapshot identityLink, DeviceLinkResultPayload result)
    {
        string remoteStatus = result.Device.Status?.Trim() ?? string.Empty;
        if (string.Equals(remoteStatus, "revoked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteStatus, "unlinked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(result.LinkState, "linked", StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(result.Device.CurrentAccountId, identityLink.AccountId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.Device.OrgId, identityLink.OrgId, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct ActionPublishResult(
        ControlPlaneReporterState State,
        bool AnySent);
}
