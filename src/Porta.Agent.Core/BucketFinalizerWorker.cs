using Microsoft.Extensions.Hosting;

namespace Porta.Agent.Core;

public sealed class BucketFinalizerWorker(IBucketFinalizerPump finalizerPump) : BackgroundService
{
    private static readonly TimeSpan TickCadence = TimeSpan.FromMilliseconds(200);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run on a dedicated OS thread, NOT from the shared thread pool.
        // SQLite write operations (bucket finalization) block the calling thread while
        // Windows Defender scans the WAL file. Running on the thread pool exhausts it,
        // starving Kestrel of threads to dispatch HTTP requests (API becomes unreachable).
        return Task.Factory.StartNew(
            () => RunLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                finalizerPump.RunFinalizerTick(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"bucket_finalizer_tick_failed error={ex.GetType().Name}:{ex.Message}");
            }

            stoppingToken.WaitHandle.WaitOne(TickCadence);
        }
    }
}
