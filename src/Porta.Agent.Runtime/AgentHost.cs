using System;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Input;

namespace Porta.Agent.Runtime
{
    internal sealed class ScrollProcessor
    {
        private ScrollEvent? _lastEvent;

        public void Process(ScrollEvent evt)
        {
            double velocity = evt.Delta;

            if (_lastEvent != null)
            {
                double deltaTime = (evt.Timestamp - _lastEvent.Value.Timestamp) / (double)Stopwatch.Frequency;
                if (deltaTime > 0)
                    velocity = evt.Delta / deltaTime;
            }

            Console.WriteLine($"Scroll delta: {evt.Delta}, velocity: {velocity:F2}, timestamp: {evt.Timestamp}");

            _lastEvent = evt;

            // TODO: send to PORTA processing modules
            // Example: EmotionalEngine.ProcessScroll(evt.Delta, velocity);
        }
    }

    public sealed class AgentHost : IDisposable
    {
        private readonly MouseHook _mouseHook;
        private readonly ScrollProcessor _processor = new();
        private readonly KillSwitch _killSwitch = new();
        private readonly object _gate = new();

        private Channel<ScrollEvent>? _events;
        private CancellationTokenSource? _lifecycleCts;
        private Task? _processingTask;
        private Task? _metricsTask;
        private Task? _killSwitchTask;
        private bool _running;
        private long _enqueuedEvents;
        private long _processedEvents;
        private long _droppedEvents;
        private int _queueDepth;

        public event Action? KillSwitchTriggered;

        public AgentHost()
        {
            _mouseHook = new MouseHook();
            _mouseHook.ScrollDeltaChanged += OnScrollDelta;
        }

        private void OnScrollDelta(int delta)
        {
            var events = _events;
            if (_running && events != null)
            {
                if (events.Writer.TryWrite(new ScrollEvent(Stopwatch.GetTimestamp(), delta)))
                {
                    Interlocked.Increment(ref _enqueuedEvents);
                    Interlocked.Increment(ref _queueDepth);
                }
                else
                {
                    Interlocked.Increment(ref _droppedEvents);
                }
            }
        }

        private async Task ProcessQueueAsync(ChannelReader<ScrollEvent> reader, CancellationToken token)
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    _processor.Process(evt);
                    Interlocked.Increment(ref _processedEvents);
                    Interlocked.Decrement(ref _queueDepth);
                }
            }
        }

        private async Task ReportMetricsAsync(CancellationToken token)
        {
            const int reportIntervalMs = 5000;
            long previousProcessed = 0;
            var stopwatch = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(reportIntervalMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                long enqueued = Interlocked.Read(ref _enqueuedEvents);
                long processed = Interlocked.Read(ref _processedEvents);
                long dropped = Interlocked.Read(ref _droppedEvents);
                int queueDepth = Interlocked.CompareExchange(ref _queueDepth, 0, 0);
                double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double eventsPerSecond = (processed - previousProcessed) / elapsedSeconds;
                previousProcessed = processed;
                stopwatch.Restart();

                Console.WriteLine(
                    $"[METRIC] ts={DateTimeOffset.UtcNow:O} enqueued={enqueued} processed={processed} dropped={dropped} queue_depth={queueDepth} eps={eventsPerSecond:F2}");
            }
        }

        public async Task StartAsync(CancellationToken token)
        {
            lock (_gate)
            {
                if (_running)
                {
                    return;
                }

                Logger.Info("AgentHost starting.");
                Interlocked.Exchange(ref _enqueuedEvents, 0);
                Interlocked.Exchange(ref _processedEvents, 0);
                Interlocked.Exchange(ref _droppedEvents, 0);
                Interlocked.Exchange(ref _queueDepth, 0);
                _events = Channel.CreateUnbounded<ScrollEvent>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = true
                    });
                _lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _mouseHook.Install();
                _processingTask = ProcessQueueAsync(_events.Reader, _lifecycleCts.Token);
                _metricsTask = ReportMetricsAsync(_lifecycleCts.Token);
                _killSwitchTask = _killSwitch.MonitorAsync(_lifecycleCts.Token, OnKillSwitchTriggered);
                _running = true;
                Logger.Info(_killSwitch.DescribeTrigger());
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            Task? processingTask;
            Task? metricsTask;
            Task? killSwitchTask;
            CancellationTokenSource? lifecycleCts;
            Channel<ScrollEvent>? events;

            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }

                Logger.Info("AgentHost stopping.");
                _running = false;
                _mouseHook.Uninstall();

                events = _events;
                processingTask = _processingTask;
                metricsTask = _metricsTask;
                killSwitchTask = _killSwitchTask;
                lifecycleCts = _lifecycleCts;
                _events = null;
                _processingTask = null;
                _metricsTask = null;
                _killSwitchTask = null;
                _lifecycleCts = null;
            }

            events?.Writer.TryComplete();

            if (processingTask != null)
            {
                try
                {
                    await processingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (lifecycleCts != null)
            {
                lifecycleCts.Cancel();
            }

            if (metricsTask != null)
            {
                try
                {
                    await metricsTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (killSwitchTask != null)
            {
                try
                {
                    await killSwitchTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            lifecycleCts?.Dispose();
            Logger.Info("AgentHost stopped.");
        }

        private void OnKillSwitchTriggered()
        {
            Logger.Warn("Kill switch triggered.");
            _lifecycleCts?.Cancel();
            KillSwitchTriggered?.Invoke();
        }

        public void Start() => StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        public void Stop() => StopAsync().GetAwaiter().GetResult();
        public void Dispose() => Stop();
    }
}
