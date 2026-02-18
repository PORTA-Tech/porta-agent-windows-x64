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
    internal enum HostLifecycleState
    {
        Stopped = 0,
        Starting = 1,
        Running = 2,
        Stopping = 3,
        Faulted = 4
    }

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
        private int _lifecycleState = (int)HostLifecycleState.Stopped;
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
        private int _stopRequested;
        private long _injectionWindowStartedTimestamp;
        private long _processingErrors;

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
            if (IsRunning && events != null)
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
            try
            {
                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var evt))
                    {
                        try
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
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _processingErrors);
                            Logger.Error(ex, "Processing loop event failed.");
                        }
                        finally
                        {
                            Interlocked.Increment(ref _processedEvents);
                            Interlocked.Decrement(ref _queueDepth);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _processingErrors);
                Logger.Error(ex, "Processing loop terminated unexpectedly.");
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
                long processingErrors = Interlocked.Read(ref _processingErrors);
                int queueDepth = Interlocked.CompareExchange(ref _queueDepth, 0, 0);
                double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                double eventsPerSecond = (processed - previousProcessed) / elapsedSeconds;
                previousProcessed = processed;
                stopwatch.Restart();

                Logger.Event(
                    "runtime_metrics",
                    ("enqueued", enqueued),
                    ("processed", processed),
                    ("dropped", dropped),
                    ("suppressed", suppressed),
                    ("injected", injected),
                    ("blocked_by_safety", blockedBySafety),
                    ("blocked_by_focus", blockedByFocus),
                    ("processing_errors", processingErrors),
                    ("queue_depth", queueDepth),
                    ("eps", eventsPerSecond.ToString("F2")));
                Logger.Counter("queue_depth", queueDepth, ("state", LifecycleState.ToString()));
            }
        }

        public async Task StartAsync(CancellationToken token)
        {
            CancellationTokenSource? lifecycleCts = null;
            Channel<ScrollEvent>? events = null;

            lock (_gate)
            {
                if (LifecycleState is HostLifecycleState.Running or HostLifecycleState.Starting)
                {
                    Logger.Event("lifecycle_transition_ignored", ("operation", "start"), ("state", LifecycleState.ToString()));
                    return;
                }

                if (LifecycleState == HostLifecycleState.Stopping)
                {
                    Logger.Warn("Start ignored while host is stopping.");
                    return;
                }

                TransitionState(HostLifecycleState.Starting);
                Logger.Info("AgentHost starting.");
                Interlocked.Exchange(ref _enqueuedEvents, 0);
                Interlocked.Exchange(ref _processedEvents, 0);
                Interlocked.Exchange(ref _droppedEvents, 0);
                Interlocked.Exchange(ref _suppressedEvents, 0);
                Interlocked.Exchange(ref _injectedEvents, 0);
                Interlocked.Exchange(ref _blockedBySafetyEvents, 0);
                Interlocked.Exchange(ref _blockedByFocusEvents, 0);
                Interlocked.Exchange(ref _processingErrors, 0);
                Interlocked.Exchange(ref _queueDepth, 0);
                Interlocked.Exchange(ref _stopRequested, 0);
                _injectedThisWindow = 0;
                _injectionWindowStartedTimestamp = Stopwatch.GetTimestamp();
                events = Channel.CreateUnbounded<ScrollEvent>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = true
                    });
                lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _events = events;
                _lifecycleCts = lifecycleCts;
            }

            try
            {
                if (!_mouseHook.Install())
                {
                    throw new InvalidOperationException("Mouse hook install failed after retries.");
                }

                lock (_gate)
                {
                    if (_lifecycleCts == null || _events == null)
                    {
                        throw new InvalidOperationException("Host resources were disposed during startup.");
                    }

                    _processingTask = ProcessQueueAsync(_events.Reader, _lifecycleCts.Token);
                    _metricsTask = ReportMetricsAsync(_lifecycleCts.Token);
                    _killSwitchTask = _killSwitch.MonitorAsync(_lifecycleCts.Token, OnKillSwitchTriggered);
                    TransitionState(HostLifecycleState.Running);
                    Logger.Event("lifecycle_transition", ("to", HostLifecycleState.Running.ToString()));
                    Logger.Info(_killSwitch.DescribeTrigger());
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "AgentHost start failed.");
                _mouseHook.Uninstall();
                lifecycleCts?.Cancel();
                lifecycleCts?.Dispose();

                lock (_gate)
                {
                    _events = null;
                    _lifecycleCts = null;
                    _processingTask = null;
                    _metricsTask = null;
                    _killSwitchTask = null;
                    TransitionState(HostLifecycleState.Faulted);
                }

                throw;
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
            HostLifecycleState stateAtStop;

            lock (_gate)
            {
                stateAtStop = LifecycleState;
                if (stateAtStop == HostLifecycleState.Stopped)
                {
                    Logger.Event("lifecycle_transition_ignored", ("operation", "stop"), ("state", stateAtStop.ToString()));
                    return;
                }

                if (stateAtStop == HostLifecycleState.Stopping)
                {
                    Logger.Event("lifecycle_transition_ignored", ("operation", "stop"), ("state", stateAtStop.ToString()));
                    return;
                }

                TransitionState(HostLifecycleState.Stopping);
                Logger.Info("AgentHost stopping.");

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

            try
            {
                _mouseHook.Uninstall();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Mouse hook uninstall failed.");
            }

            events?.Writer.TryComplete();

            await SafeAwaitTask(processingTask, "processing task").ConfigureAwait(false);

            lifecycleCts?.Cancel();

            await SafeAwaitTask(metricsTask, "metrics task").ConfigureAwait(false);
            await SafeAwaitTask(killSwitchTask, "kill switch task").ConfigureAwait(false);

            lifecycleCts?.Dispose();

            lock (_gate)
            {
                TransitionState(HostLifecycleState.Stopped);
            }
            Logger.Event("lifecycle_transition", ("to", HostLifecycleState.Stopped.ToString()));
            Logger.Info("AgentHost stopped.");
        }

        private void OnKillSwitchTriggered()
        {
            Logger.Warn("Kill switch triggered.");
            RequestStop("kill_switch");
            _ = Task.Run(async () =>
            {
                try
                {
                    await StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "StopAsync failed after kill switch trigger.");
                }
            });
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

        private void RequestStop(string reason)
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
            {
                return;
            }

            Logger.Event("stop_requested", ("reason", reason), ("state", LifecycleState.ToString()));
            _lifecycleCts?.Cancel();
            TryRaiseKillSwitchTriggered();
        }

        private void TryRaiseKillSwitchTriggered()
        {
            try
            {
                KillSwitchTriggered?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "KillSwitchTriggered handler failed.");
            }
        }

        private async Task SafeAwaitTask(Task? task, string taskName)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Error while awaiting {taskName}.");
            }
        }

        private HostLifecycleState LifecycleState => (HostLifecycleState)Volatile.Read(ref _lifecycleState);

        private bool IsRunning => LifecycleState == HostLifecycleState.Running;

        private void TransitionState(HostLifecycleState state)
        {
            Volatile.Write(ref _lifecycleState, (int)state);
        }

        public void Start() => StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        public void Stop() => StopAsync().GetAwaiter().GetResult();
        public void Dispose() => Stop();
    }
}
