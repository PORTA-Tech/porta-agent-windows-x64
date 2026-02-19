using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Runtime.Transport;

internal sealed class TelemetryWindowEmitter : IDisposable
{
    private readonly ChannelReader<AgentTelemetry> _reader;
    private readonly Func<IReadOnlyList<AgentTelemetry>, CancellationToken, Task> _emitWindowAsync;
    private readonly Action<int>? _onDequeued;
    private readonly PeriodicTimer _timer;
    private readonly int _maxBatchSize;
    private int _running;
    private int _disposed;

    public TelemetryWindowEmitter(
        ChannelReader<AgentTelemetry> reader,
        Func<IReadOnlyList<AgentTelemetry>, CancellationToken, Task> emitWindowAsync,
        Action<int>? onDequeued,
        TimeSpan interval,
        int maxBatchSize)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _emitWindowAsync = emitWindowAsync ?? throw new ArgumentNullException(nameof(emitWindowAsync));
        _onDequeued = onDequeued;
        _timer = new PeriodicTimer(interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval)));
        _maxBatchSize = maxBatchSize > 0 ? maxBatchSize : throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
    }

    public async Task RunAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("TelemetryWindowEmitter is already running.");
        }

        var batch = new List<AgentTelemetry>(_maxBatchSize);
        try
        {
            while (await WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Drain(batch);
                if (batch.Count == 0)
                {
                    if (_reader.Completion.IsCompleted)
                    {
                        break;
                    }

                    continue;
                }

                await _emitWindowAsync(batch, token).ConfigureAwait(false);
                batch.Clear();

                if (_reader.Completion.IsCompleted)
                {
                    Drain(batch);
                    if (batch.Count > 0)
                    {
                        await _emitWindowAsync(batch, token).ConfigureAwait(false);
                    }

                    break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _timer.Dispose();
    }

    private async Task<bool> WaitForNextTickAsync(CancellationToken token)
    {
        try
        {
            return await _timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Drain(List<AgentTelemetry> batch)
    {
        int dequeued = 0;
        while (batch.Count < _maxBatchSize && _reader.TryRead(out AgentTelemetry? item))
        {
            batch.Add(item);
            dequeued++;
        }

        if (dequeued > 0)
        {
            _onDequeued?.Invoke(dequeued);
        }
    }
}
