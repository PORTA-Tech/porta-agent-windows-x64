using System;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Runtime.Diagnostics;

namespace Porta.Agent.Runtime.Transport
{
    internal sealed class Heartbeat
    {
        private readonly PortaClient _client;
        private readonly TimeSpan _interval;
        private readonly Func<ControlParameters> _controlSnapshot;
        private readonly Func<AgentStatus> _statusSnapshot;

        public Heartbeat(
            PortaClient client,
            Func<ControlParameters> controlSnapshot,
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
            ControlParameters control = _controlSnapshot();
            bool sent = await _client.SendHeartbeatAsync(status, control, note, token).ConfigureAwait(false);
            if (!sent)
            {
                Logger.Counter("heartbeat_send_failed", 1, ("status", status.ToString()));
            }
        }
    }
}
