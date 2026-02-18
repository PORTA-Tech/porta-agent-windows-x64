using System;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Control;
using Porta.Agent.Runtime.Actuation;
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
        private readonly FocusTracker _focusTracker;
        private readonly ScrollProcessor _processor = new();
        private readonly DelayController _delayController = new();
        private readonly ScrollInjector _scrollInjector = new();
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
        private long _suppressedEvents;
        private long _injectedEvents;
        private long _blockedBySafetyEvents;
        private long _blockedByFocusEvents;
        private int _queueDepth;
        private int _suppressScrollInput;
        private int _actuationEnabled;
        private int _injectedThisWindow;
        private long _injectionWindowStartedTimestamp;

        public event Action? KillSwitchTriggered;

        public AgentHost()
        {
            _mouseHook = new MouseHook();
            _focusTracker = new FocusTracker(Environment.GetEnvironmentVariable("PORTA_TARGET_PROCESS"));
            _mouseHook.ScrollDeltaChanged += OnScrollDelta;
            _mouseHook.ShouldSuppressScroll = ShouldSuppressScroll;
            _injectionWindowStartedTimestamp = Stopwatch.GetTimestamp();
            Logger.Info(
                _focusTracker.IsEnabled
                    ? $"Focus tracking enabled for process '{_focusTracker.TargetProcessName}'."
                    : "Focus tracking disabled (set PORTA_TARGET_PROCESS to enable).");
        }

        public bool IsScrollSuppressionEnabled => Volatile.Read(ref _suppressScrollInput) == 1;

        public void SetScrollSuppression(bool enabled)
        {
            Volatile.Write(ref _suppressScrollInput, enabled ? 1 : 0);
            Logger.Info($"Scroll suppression {(enabled ? "enabled" : "disabled")}.");
        }

        public bool IsActuationEnabled => Volatile.Read(ref _actuationEnabled) == 1;

        public void SetActuationEnabled(bool enabled)
        {
            Volatile.Write(ref _actuationEnabled, enabled ? 1 : 0);
            Logger.Info($"Scroll actuation {(enabled ? "enabled" : "disabled")}.");
        }

        private void OnScrollDelta(int delta, bool wasSuppressed)
        {
            var events = _events;
            if (_running && events != null)
            {
                if (wasSuppressed)
                {
                    Interlocked.Increment(ref _suppressedEvents);
                }
                else if (IsScrollSuppressionEnabled)
                {
                    Interlocked.Increment(ref _blockedByFocusEvents);
                }

                if (events.Writer.TryWrite(new ScrollEvent(Stopwatch.GetTimestamp(), delta, wasSuppressed)))
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

                    if (evt.WasSuppressed && IsActuationEnabled)
                    {
                        int boundedDelta = SafetyLimits.ClampInjectionDelta(evt.Delta);
                        if (SafetyLimits.IsInjectableDelta(boundedDelta) && TryConsumeInjectionBudget())
                        {
                            long targetTimestamp = _delayController.ComputeTargetTimestamp(evt.Timestamp);
                            await _delayController.WaitUntilAsync(targetTimestamp, token).ConfigureAwait(false);
                            _scrollInjector.InjectVerticalWheel(boundedDelta);
                            Interlocked.Increment(ref _injectedEvents);
                        }
                        else
                        {
                            Interlocked.Increment(ref _blockedBySafetyEvents);
                        }
                    }

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
                long suppressed = Interlocked.Read(ref _suppressedEvents);
                long injected = Interlocked.Read(ref _injectedEvents);
                long blockedBySafety = Interlocked.Read(ref _blockedBySafetyEvents);
                long blockedByFocus = Interlocked.Read(ref _blockedByFocusEvents);
                int queueDepth = Interlocked.CompareExchange(ref _queueDepth, 0, 0);
                double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double eventsPerSecond = (processed - previousProcessed) / elapsedSeconds;
                previousProcessed = processed;
                stopwatch.Restart();

                Console.WriteLine(
                    $"[METRIC] ts={DateTimeOffset.UtcNow:O} enqueued={enqueued} processed={processed} dropped={dropped} suppressed={suppressed} injected={injected} blocked_by_safety={blockedBySafety} blocked_by_focus={blockedByFocus} queue_depth={queueDepth} eps={eventsPerSecond:F2}");
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
                Interlocked.Exchange(ref _suppressedEvents, 0);
                Interlocked.Exchange(ref _injectedEvents, 0);
                Interlocked.Exchange(ref _blockedBySafetyEvents, 0);
                Interlocked.Exchange(ref _blockedByFocusEvents, 0);
                Interlocked.Exchange(ref _queueDepth, 0);
                _injectedThisWindow = 0;
                _injectionWindowStartedTimestamp = Stopwatch.GetTimestamp();
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

        private bool ShouldSuppressScroll(int _)
        {
            return IsScrollSuppressionEnabled && _focusTracker.IsTargetInForeground();
        }

        private bool TryConsumeInjectionBudget()
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _injectionWindowStartedTimestamp >= Stopwatch.Frequency)
            {
                _injectionWindowStartedTimestamp = now;
                _injectedThisWindow = 0;
            }

            if (_injectedThisWindow >= SafetyLimits.MaxInjectedEventsPerSecond)
            {
                return false;
            }

            _injectedThisWindow++;
            return true;
        }

        public void Start() => StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        public void Stop() => StopAsync().GetAwaiter().GetResult();
        public void Dispose() => Stop();
    }
}
