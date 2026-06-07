using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Identity;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Background service that periodically fetches user context (firstName, weakHour, avgStreakMin)
/// from the control plane and stores it in AriContextCache for the local API to serve.
/// Refreshes every 30 minutes; retries every 5 minutes on failure.
/// </summary>
internal sealed class UserContextRefresher : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    private readonly ControlPlaneClient _client;
    private readonly ILocalIdentityLinkStore _identityLinkStore;
    private readonly AriContextCache _cache;

    public UserContextRefresher(
        ControlPlaneClient client,
        ILocalIdentityLinkStore identityLinkStore,
        AriContextCache cache)
    {
        _client = client;
        _identityLinkStore = identityLinkStore;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay — wait for the agent to link before first fetch
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);

        TimeSpan nextWait = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (nextWait > TimeSpan.Zero)
            {
                await Task.Delay(nextWait, stoppingToken).ConfigureAwait(false);
            }

            if (!_client.IsEnabled)
            {
                nextWait = RetryInterval;
                continue;
            }

            LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();
            if (string.IsNullOrWhiteSpace(identity.AccountId))
            {
                nextWait = RetryInterval;
                continue;
            }

            try
            {
                UserContextPayload? payload = await _client
                    .GetUserContextAsync(identity.AccountId, stoppingToken)
                    .ConfigureAwait(false);

                if (payload is not null)
                {
                    _cache.Update(new LocalApiAriContextResponse(
                        payload.FirstName,
                        payload.WeakHour,
                        payload.AvgStreakMin));
                    nextWait = RefreshInterval;
                }
                else
                {
                    nextWait = RetryInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                nextWait = RetryInterval;
            }
        }
    }
}
