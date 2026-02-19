using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Runtime.Diagnostics;

namespace Porta.Agent.Runtime.Transport
{
    internal sealed class Heartbeat
    {
        private const double MinimumFrequencyWindowSeconds = 5.0;
        private readonly PortaClient _client;
        private readonly TimeSpan _interval;
        private readonly Func<double, ControlParameters> _controlSnapshot;
        private readonly Func<AgentStatus> _statusSnapshot;
        private readonly object _gate = new();
        private readonly Queue<long> _loopTimestamps = new();
        private double _stabilizedFrequencyHz;

        public Heartbeat(
            PortaClient client,
            Func<double, ControlParameters> controlSnapshot,
            Func<AgentStatus> statusSnapshot,
            TimeSpan? interval = null)
        {
            _client = client;
            _controlSnapshot = controlSnapshot;
            _statusSnapshot = statusSnapshot;
            _interval = interval ?? TimeSpan.FromSeconds(5);
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    RecordLoopTick(Stopwatch.GetTimestamp());
                    await SendOnceAsync("periodic", token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Heartbeat loop iteration failed.");
                }

                try
                {
                    await Task.Delay(_interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public Task SendTransitionAsync(string note, CancellationToken token)
        {
            return SendOnceAsync(note, token);
        }

        private async Task SendOnceAsync(string note, CancellationToken token)
        {
            if (!_client.IsEnabled)
            {
                return;
            }

            AgentStatus status = _statusSnapshot();
            ControlParameters control = _controlSnapshot(GetStabilizedFrequencyHz());
            bool sent = await _client.SendHeartbeatAsync(status, control, note, token).ConfigureAwait(false);
            if (!sent)
            {
                Logger.Counter("heartbeat_send_failed", 1, ("status", status.ToString()));
            }
        }

        private void RecordLoopTick(long timestamp)
        {
            long windowTicks = (long)Math.Round(MinimumFrequencyWindowSeconds * Stopwatch.Frequency, MidpointRounding.AwayFromZero);

            lock (_gate)
            {
                _loopTimestamps.Enqueue(timestamp);
                while (_loopTimestamps.Count >= 3)
                {
                    long[] samples = _loopTimestamps.ToArray();
                    long second = samples[1];

                    // Keep at least a full minimum window of samples.
                    if ((timestamp - second) < windowTicks)
                    {
                        break;
                    }

                    _loopTimestamps.Dequeue();
                }

                if (_loopTimestamps.Count < 2)
                {
                    return;
                }

                long oldestTimestamp = _loopTimestamps.Peek();
                long span = timestamp - oldestTimestamp;
                if (span < windowTicks)
                {
                    return;
                }

                double avgPeriodTicks = span / (double)(_loopTimestamps.Count - 1);
                if (avgPeriodTicks <= 0.0)
                {
                    return;
                }

                double hz = Stopwatch.Frequency / avgPeriodTicks;
                _stabilizedFrequencyHz = Math.Round(hz, 1, MidpointRounding.AwayFromZero);
            }
        }

        private double GetStabilizedFrequencyHz()
        {
            lock (_gate)
            {
                return _stabilizedFrequencyHz;
            }
        }
    }
}
