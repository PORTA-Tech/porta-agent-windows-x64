using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Porta.Agent.Runtime.Diagnostics;

internal sealed class KillSwitch
{
    private readonly string _flagPath;
    private readonly TimeSpan _pollInterval;

    public KillSwitch(string? flagPath = null, TimeSpan? pollInterval = null)
    {
        _flagPath = flagPath ?? Path.Combine(AppContext.BaseDirectory, "porta-agent.kill");
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public async Task MonitorAsync(CancellationToken token, Action onTriggered)
    {
        while (!token.IsCancellationRequested)
        {
            bool isTriggered;
            try
            {
                isTriggered = File.Exists(_flagPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Kill switch probe failed.");
                isTriggered = false;
            }

            if (isTriggered)
            {
                TryDeleteFlag();

                try
                {
                    onTriggered();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Kill switch callback failed.");
                }

                return;
            }

            try
            {
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void TryDeleteFlag()
    {
        try
        {
            File.Delete(_flagPath);
        }
        catch
        {
            // Best effort. Keep kill switch non-throwing.
        }
    }

    public string DescribeTrigger()
    {
        return $"Create file '{_flagPath}' to request graceful shutdown.";
    }
}
