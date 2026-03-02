using Microsoft.Extensions.Hosting;

namespace Porta.Agent.Core;

public sealed class BucketFinalizerWorker(IBucketFinalizerPump finalizerPump) : BackgroundService
{
    private static readonly TimeSpan TickCadence = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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

            await Task.Delay(TickCadence, stoppingToken).ConfigureAwait(false);
        }
    }
}
