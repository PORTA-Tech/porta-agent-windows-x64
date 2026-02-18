using System;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Runtime;
using Porta.Agent.Runtime.Diagnostics;

class Program
{
    static async Task Main()
    {
        using var host = new AgentHost();
        using var stopCts = new CancellationTokenSource();

        host.KillSwitchTriggered += () => stopCts.Cancel();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopCts.Cancel();
        };

        await host.StartAsync(stopCts.Token);
        Logger.Info("Scroll anywhere. Press Ctrl+C to exit.");

        try
        {
            await Task.Delay(Timeout.Infinite, stopCts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await host.StopAsync();
    }
}
