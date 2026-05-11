using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Porta.Agent.Service;

/// <summary>
/// Re-applies the Windows Defender path exclusion for C:\ProgramData\PortaAgent on startup
/// and every 60 minutes thereafter.
///
/// Without this, Group Policy refreshes (every 90 min by default) can silently strip exclusions
/// added by Add-MpPreference, causing Defender to resume scanning the WAL file on every
/// SQLite write commit — producing multi-second fsync stalls that exhaust the thread pool.
/// </summary>
internal sealed class DefenderExclusionWorker : BackgroundService
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromHours(1);
    private const string ExclusionPath = @"C:\ProgramData\PortaAgent";

    private readonly ILogger<DefenderExclusionWorker> _logger;

    public DefenderExclusionWorker(ILogger<DefenderExclusionWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Apply immediately so protection is in place before any DB I/O begins.
        ApplyExclusion();

        using var timer = new PeriodicTimer(RenewalInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ApplyExclusion();
        }
    }

    private void ApplyExclusion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Add-MpPreference -ExclusionPath '{ExclusionPath}' -ErrorAction SilentlyContinue\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            process?.WaitForExit(5000);
            _logger.LogInformation("defender_exclusion_applied path={Path}", ExclusionPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "defender_exclusion_failed (non-fatal) path={Path}", ExclusionPath);
        }
    }
}
