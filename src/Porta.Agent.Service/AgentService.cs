using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Runtime.Layer1;

namespace Porta.Agent.Service;

internal sealed class AgentService : IHostedService, IDisposable
{
    private readonly Layer1AgentHost _agentHost = new();
    private int _started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        await _agentHost.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        await _agentHost.StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _agentHost.Dispose();
    }
}
