using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service;

internal sealed class LocalApiHealthWatchdog : BackgroundService
{
    private const int MaxFailures = 3;
    private const int DefaultPort = 17887;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger<LocalApiHealthWatchdog> _logger;
    private int _failureCount;

    public LocalApiHealthWatchdog(ILogger<LocalApiHealthWatchdog> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            int port = LocalApiRuntimeSettingsStore.LoadEndpoint()?.Port
                ?? LocalApiRuntimeSettingsStore.Load()?.Port
                ?? DefaultPort;

            bool ok = false;
            try
            {
                HttpResponseMessage response = await http
                    .GetAsync($"http://localhost:{port}/healthz", stoppingToken)
                    .ConfigureAwait(false);
                ok = response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // timeout, connection refused, etc. — count as failure below
            }

            if (ok)
            {
                _failureCount = 0;
            }
            else
            {
                _failureCount++;
                _logger.LogWarning(
                    "local_api_health_watchdog check failed (failure {Count}/{Max}) port={Port}",
                    _failureCount, MaxFailures, port);

                if (_failureCount >= MaxFailures)
                {
                    _logger.LogCritical(
                        "local_api_health_watchdog triggering self-restart after {Count} consecutive failures",
                        _failureCount);
                    Environment.Exit(1);
                }
            }
        }
    }
}
