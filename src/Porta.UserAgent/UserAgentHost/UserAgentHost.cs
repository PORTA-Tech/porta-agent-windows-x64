namespace Porta.UserAgent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

public sealed class UserAgentHost : BackgroundService
{
    private readonly ILogger<UserAgentHost> _logger;

    public UserAgentHost(ILogger<UserAgentHost> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Porta.UserAgent running in process {ProcessId}.", Environment.ProcessId);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Porta.UserAgent heartbeat.");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Porta.UserAgent stopping.");
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
