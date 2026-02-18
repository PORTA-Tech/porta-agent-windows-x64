using System;
using System.Diagnostics;
using System.Threading;
using Porta.Agent.Runtime;

namespace Porta.Agent.Service;

#if WINDOWS
using System.ServiceProcess;

internal sealed class AgentService : ServiceBase
{
    private readonly AgentHost _agentHost;
    private readonly object _gate = new();
    private CancellationTokenSource? _serviceCts;
    private int _started;

    public AgentService()
    {
        _agentHost = new AgentHost();
        ServiceName = "PortaAgentService";
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    public void StartForConsole()
    {
        StartCore(CancellationToken.None);
    }

    public void StopForConsole()
    {
        StopCore();
    }

    protected override void OnStart(string[] args)
    {
        StartCore(CancellationToken.None);
    }

    protected override void OnStop()
    {
        StopCore();
    }

    protected override void OnShutdown()
    {
        StopCore();
        base.OnShutdown();
    }

    private void StartCore(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        lock (_gate)
        {
            _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            _agentHost.StartAsync(_serviceCts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _started, 0);
            lock (_gate)
            {
                _serviceCts?.Dispose();
                _serviceCts = null;
            }

            TryLogError("Failed to start AgentService runtime bridge.", ex);
            throw;
        }
    }

    private void StopCore()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _serviceCts;
            _serviceCts = null;
        }

        try
        {
            cts?.Cancel();
            _agentHost.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            TryLogError("Failed to stop AgentService runtime bridge.", ex);
        }
        finally
        {
            cts?.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopCore();
            _agentHost.Dispose();
        }

        base.Dispose(disposing);
    }

    private void TryLogError(string message, Exception ex)
    {
        try
        {
            EventLog.WriteEntry(ServiceName, $"{message} {ex.GetType().Name}: {ex.Message}", EventLogEntryType.Error);
        }
        catch
        {
        }
    }
}
#else
internal sealed class AgentService : IDisposable
{
    private readonly AgentHost _agentHost = new();

    public void StartForConsole()
    {
        _agentHost.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void StopForConsole()
    {
        _agentHost.StopAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _agentHost.Dispose();
    }
}
#endif
