using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Control;

namespace Porta.Agent.Runtime.Actuation;

internal sealed class DelayController
{
    private readonly int _fixedDelayMs;

    public DelayController(int fixedDelayMs = 8)
    {
        _fixedDelayMs = Math.Clamp(fixedDelayMs, 0, SafetyLimits.MaxActuationDelayMs);
    }

    public long ComputeTargetTimestamp(long sourceTimestamp)
    {
        if (_fixedDelayMs == 0)
        {
            return sourceTimestamp;
        }

        long delayTicks = (long)(_fixedDelayMs * (Stopwatch.Frequency / 1000.0));
        return sourceTimestamp + delayTicks;
    }

    public async ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();

            long remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                int sleepMs = (int)Math.Max(1, Math.Min(remainingMs - 1.0, 50.0));
                await Task.Delay(sleepMs, token).ConfigureAwait(false);
                continue;
            }

            Thread.SpinWait(150);
        }
    }
}
