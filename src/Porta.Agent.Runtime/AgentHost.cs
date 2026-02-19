using System;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.Control;
using Porta.Agent.Runtime.Actuation;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Input;
using Porta.Agent.Runtime.Transport;

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
        private const int ControlClockHz = 20;
        private readonly MouseHook _mouseHook;
        private readonly KeyboardHook _keyboardHook;
        private readonly ForegroundHook _foregroundHook;
        private readonly FocusTracker _focusTracker;
        private DeterministicControlEngine _controlEngine;
        private readonly ScrollProcessor _processor = new();
        private readonly DelayController _delayController = new();
        private readonly ScrollInjector _scrollInjector = new();
        private readonly KillSwitch _killSwitch = new();
        private readonly PortaClient _portaClient;
        private readonly Heartbeat _heartbeat;
        private readonly ControlSessionStore _sessionStore;
        private readonly bool _controlDiagnosticsTelemetryEnabled;
        private readonly ControlSnapshotExporter? _controlSnapshotExporter;
        private readonly object _gate = new();

        private Channel<RuntimeInputEvent>? _events;
        private Channel<AgentTelemetry>? _telemetry;
        private CancellationTokenSource? _lifecycleCts;
        private Task? _processingTask;
        private Task? _metricsTask;
        private Task? _killSwitchTask;
        private Task? _telemetryTask;
        private Task? _heartbeatTask;
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
        private long _keyboardEnqueuedEvents;
        private long _keyboardProcessedEvents;
        private long _keyboardDroppedEvents;
        private long _keyboardInjectedEvents;
        private long _focusEnqueuedEvents;
        private long _focusProcessedEvents;
        private long _focusDroppedEvents;
        private long _controlTicks;
        private long _controlActuationApplied;
        private long _controlReducedAuthority;
        private long _lastProcessedControlTickTimestamp;
        private long _telemetryEnqueued;
        private long _telemetrySent;
        private long _telemetryDropped;
        private long _telemetrySendFailures;
        private int _telemetryQueueDepth;
        private int _keyboardCaptureEnabled = 1;

        public event Action? KillSwitchTriggered;

        public AgentHost()
        {
            _mouseHook = new MouseHook();
            _keyboardHook = new KeyboardHook();
            _foregroundHook = new ForegroundHook();
            _focusTracker = new FocusTracker(Environment.GetEnvironmentVariable("PORTA_TARGET_PROCESS"));
            _controlEngine = new DeterministicControlEngine(Stopwatch.Frequency, controlHz: ControlClockHz);
            _sessionStore = new ControlSessionStore(Environment.GetEnvironmentVariable("PORTA_CONTROL_SESSION_PATH"));
            _controlDiagnosticsTelemetryEnabled = ReadBoolEnvironment("PORTA_CONTROL_DIAGNOSTICS_TELEMETRY", defaultValue: false);
            if (_controlDiagnosticsTelemetryEnabled)
            {
                _controlSnapshotExporter = new ControlSnapshotExporter(Environment.GetEnvironmentVariable("PORTA_CONTROL_SNAPSHOT_EXPORT_PATH"));
            }
            _portaClient = new PortaClient();
            _heartbeat = new Heartbeat(_portaClient, BuildControlSnapshot, BuildAgentStatusSnapshot);
            _mouseHook.ScrollDeltaChanged += OnScrollDelta;
            _mouseHook.ShouldSuppressScroll = ShouldSuppressScroll;
            _keyboardHook.KeyChanged += OnKeyChanged;
            _foregroundHook.ForegroundChanged += OnForegroundChanged;
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

        public bool IsKeyboardCaptureEnabled => Volatile.Read(ref _keyboardCaptureEnabled) == 1;

        public void SetKeyboardCapture(bool enabled)
        {
            Volatile.Write(ref _keyboardCaptureEnabled, enabled ? 1 : 0);
            Logger.Info($"Keyboard capture {(enabled ? "enabled" : "disabled")}.");
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

                EnqueueTelemetry(new AgentTelemetry
                {
                    AgentId = _portaClient.AgentId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Kind = "scroll",
                    Delta = delta,
                    WasSuppressed = wasSuppressed
                });

                if (events.Writer.TryWrite(RuntimeInputEvent.FromScroll(new ScrollEvent(Stopwatch.GetTimestamp(), delta, wasSuppressed))))
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

        private void OnKeyChanged(int virtualKey, bool isKeyDown, bool isInjected)
        {
            var events = _events;
            if (!IsRunning || events == null)
            {
                return;
            }

            if (isInjected)
            {
                Interlocked.Increment(ref _keyboardInjectedEvents);
            }

            EnqueueTelemetry(new AgentTelemetry
            {
                AgentId = _portaClient.AgentId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = "keyboard",
                Note = $"vk={virtualKey} down={isKeyDown} injected={isInjected}"
            });

            if (events.Writer.TryWrite(RuntimeInputEvent.FromKeyboard(new KeyboardEvent(Stopwatch.GetTimestamp(), virtualKey, isKeyDown, isInjected))))
            {
                Interlocked.Increment(ref _keyboardEnqueuedEvents);
                Interlocked.Increment(ref _queueDepth);
            }
            else
            {
                Interlocked.Increment(ref _keyboardDroppedEvents);
                Interlocked.Increment(ref _droppedEvents);
            }
        }

        private void OnForegroundChanged(uint processId, IntPtr hwnd)
        {
            var events = _events;
            if (!IsRunning || events == null)
            {
                return;
            }

            EnqueueTelemetry(new AgentTelemetry
            {
                AgentId = _portaClient.AgentId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = "focus",
                Note = $"pid={processId} hwnd=0x{hwnd.ToInt64():X}"
            });

            if (events.Writer.TryWrite(RuntimeInputEvent.FromFocus(new FocusEvent(Stopwatch.GetTimestamp(), processId, hwnd))))
            {
                Interlocked.Increment(ref _focusEnqueuedEvents);
                Interlocked.Increment(ref _queueDepth);
            }
            else
            {
                Interlocked.Increment(ref _focusDroppedEvents);
                Interlocked.Increment(ref _droppedEvents);
            }
        }

        private async Task ProcessQueueAsync(ChannelReader<RuntimeInputEvent> reader, CancellationToken token)
        {
            long tickInterval = _controlEngine.TickIntervalTicks;
            long nextTickTimestamp = Stopwatch.GetTimestamp();
            long authorityTimestamp = nextTickTimestamp;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    long now = Stopwatch.GetTimestamp();
                    long remainingTicks = nextTickTimestamp - now;
                    if (remainingTicks > 0)
                    {
                        int delayMs = (int)Math.Max(1, Math.Min(remainingTicks * 1000 / Stopwatch.Frequency, 50));
                        Task<bool> waitReadTask = reader.WaitToReadAsync(token).AsTask();
                        Task delayTask = Task.Delay(delayMs, token);
                        Task completed = await Task.WhenAny(waitReadTask, delayTask).ConfigureAwait(false);
                        if (completed == waitReadTask && !await waitReadTask.ConfigureAwait(false))
                        {
                            break;
                        }
                    }

                    bool processedAnyInput = false;
                    long latestInputTimestamp = authorityTimestamp;
                    while (reader.TryRead(out RuntimeInputEvent evt))
                    {
                        bool trackQueueDepth = true;
                        try
                        {
                            if (evt.Kind == InputEventKind.Scroll)
                            {
                                var scroll = evt.Scroll;
                                _processor.Process(scroll);
                                _controlEngine.AddScrollSample(scroll.Timestamp, scroll.Delta);
                                Interlocked.Increment(ref _processedEvents);
                                processedAnyInput = true;
                                latestInputTimestamp = Math.Max(latestInputTimestamp, scroll.Timestamp);
                            }
                            else
                            if (evt.Kind == InputEventKind.Keyboard)
                            {
                                _controlEngine.AddKeyboardSample(evt.Keyboard.Timestamp, evt.Keyboard.IsKeyDown);
                                Interlocked.Increment(ref _keyboardProcessedEvents);
                                processedAnyInput = true;
                                latestInputTimestamp = Math.Max(latestInputTimestamp, evt.Keyboard.Timestamp);
                            }
                            else if (evt.Kind == InputEventKind.Focus)
                            {
                                _controlEngine.AddFocusSample(evt.Focus.Timestamp);
                                Interlocked.Increment(ref _focusProcessedEvents);
                                processedAnyInput = true;
                                latestInputTimestamp = Math.Max(latestInputTimestamp, evt.Focus.Timestamp);
                            }
                            else
                            {
                                Logger.Warn($"Unexpected runtime input event kind: {evt.Kind}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _processingErrors);
                            Logger.Error(ex, "Processing loop event failed.");
                        }
                        finally
                        {
                            if (trackQueueDepth)
                            {
                                Interlocked.Decrement(ref _queueDepth);
                            }
                        }
                    }

                    authorityTimestamp = processedAnyInput
                        ? Math.Max(authorityTimestamp, latestInputTimestamp)
                        : Math.Max(authorityTimestamp, Stopwatch.GetTimestamp());

                    while (nextTickTimestamp <= authorityTimestamp)
                    {
                        try
                        {
                            if (nextTickTimestamp <= Interlocked.Read(ref _lastProcessedControlTickTimestamp))
                            {
                                Logger.Warn($"Non-monotonic control tick ignored. tick={nextTickTimestamp}");
                                nextTickTimestamp += tickInterval;
                                continue;
                            }

                            ControlParameters control = BuildControlSnapshot();
                            DeterministicControlEngine.ControlEvaluationSnapshot snapshot =
                                _controlEngine.EvaluateAtTimestampWithSnapshot(nextTickTimestamp, control);
                            ActuationCommand command = snapshot.SafeCommand;
                            Interlocked.Exchange(ref _lastProcessedControlTickTimestamp, nextTickTimestamp);
                            Interlocked.Increment(ref _controlTicks);
                            if (_controlDiagnosticsTelemetryEnabled)
                            {
                                DateTimeOffset capturedUtc = DateTimeOffset.UtcNow;
                                ControlSnapshotTelemetry controlSnapshot = ControlSnapshotTelemetry.FromSnapshot(
                                    nextTickTimestamp,
                                    capturedUtc,
                                    snapshot);
                                _controlSnapshotExporter?.TryAppend(controlSnapshot);
                                EnqueueTelemetry(new AgentTelemetry
                                {
                                    AgentId = _portaClient.AgentId,
                                    TimestampUtc = capturedUtc,
                                    Kind = "control_snapshot",
                                    Note = controlSnapshot.ToCanonicalJson()
                                });
                            }

                            await ApplyActuationCommandAsync(command, nextTickTimestamp, token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _processingErrors);
                            Logger.Error(ex, $"Control tick evaluation failed. tick={nextTickTimestamp}");
                        }
                        finally
                        {
                            nextTickTimestamp += tickInterval;
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
                long keyboardEnqueued = Interlocked.Read(ref _keyboardEnqueuedEvents);
                long keyboardProcessed = Interlocked.Read(ref _keyboardProcessedEvents);
                long keyboardDropped = Interlocked.Read(ref _keyboardDroppedEvents);
                long keyboardInjected = Interlocked.Read(ref _keyboardInjectedEvents);
                long focusEnqueued = Interlocked.Read(ref _focusEnqueuedEvents);
                long focusProcessed = Interlocked.Read(ref _focusProcessedEvents);
                long focusDropped = Interlocked.Read(ref _focusDroppedEvents);
                long controlTicks = Interlocked.Read(ref _controlTicks);
                long controlActuationApplied = Interlocked.Read(ref _controlActuationApplied);
                long controlReducedAuthority = Interlocked.Read(ref _controlReducedAuthority);
                long telemetryEnqueued = Interlocked.Read(ref _telemetryEnqueued);
                long telemetrySent = Interlocked.Read(ref _telemetrySent);
                long telemetryDropped = Interlocked.Read(ref _telemetryDropped);
                long telemetrySendFailures = Interlocked.Read(ref _telemetrySendFailures);
                int queueDepth = Interlocked.CompareExchange(ref _queueDepth, 0, 0);
                int telemetryQueueDepth = Interlocked.CompareExchange(ref _telemetryQueueDepth, 0, 0);
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
                    ("keyboard_enqueued", keyboardEnqueued),
                    ("keyboard_processed", keyboardProcessed),
                    ("keyboard_dropped", keyboardDropped),
                    ("keyboard_injected_seen", keyboardInjected),
                    ("focus_enqueued", focusEnqueued),
                    ("focus_processed", focusProcessed),
                    ("focus_dropped", focusDropped),
                    ("control_ticks", controlTicks),
                    ("control_actuation_applied", controlActuationApplied),
                    ("control_reduced_authority", controlReducedAuthority),
                    ("queue_depth", queueDepth),
                    ("telemetry_enqueued", telemetryEnqueued),
                    ("telemetry_sent", telemetrySent),
                    ("telemetry_dropped", telemetryDropped),
                    ("telemetry_send_failures", telemetrySendFailures),
                    ("telemetry_queue_depth", telemetryQueueDepth),
                    ("eps", eventsPerSecond.ToString("F2")));
                Logger.Counter("queue_depth", queueDepth, ("state", LifecycleState.ToString()));
            }
        }

        private async Task ProcessTelemetryAsync(ChannelReader<AgentTelemetry> reader, CancellationToken token)
        {
            var batch = new List<AgentTelemetry>(64);
            try
            {
                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    batch.Clear();
                    while (batch.Count < 64 && reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        Interlocked.Decrement(ref _telemetryQueueDepth);
                    }

                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    bool sent = await _portaClient.SendTelemetryBatchAsync(batch, token).ConfigureAwait(false);
                    if (sent)
                    {
                        Interlocked.Add(ref _telemetrySent, batch.Count);
                    }
                    else
                    {
                        Interlocked.Add(ref _telemetrySendFailures, batch.Count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Telemetry loop terminated unexpectedly.");
            }
        }

        public async Task StartAsync(CancellationToken token)
        {
            CancellationTokenSource? lifecycleCts = null;
            Channel<RuntimeInputEvent>? events = null;
            Channel<AgentTelemetry>? telemetry = null;

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
                Interlocked.Exchange(ref _keyboardEnqueuedEvents, 0);
                Interlocked.Exchange(ref _keyboardProcessedEvents, 0);
                Interlocked.Exchange(ref _keyboardDroppedEvents, 0);
                Interlocked.Exchange(ref _keyboardInjectedEvents, 0);
                Interlocked.Exchange(ref _focusEnqueuedEvents, 0);
                Interlocked.Exchange(ref _focusProcessedEvents, 0);
                Interlocked.Exchange(ref _focusDroppedEvents, 0);
                Interlocked.Exchange(ref _controlTicks, 0);
                Interlocked.Exchange(ref _controlActuationApplied, 0);
                Interlocked.Exchange(ref _controlReducedAuthority, 0);
                Interlocked.Exchange(ref _queueDepth, 0);
                Interlocked.Exchange(ref _stopRequested, 0);
                Interlocked.Exchange(ref _telemetryEnqueued, 0);
                Interlocked.Exchange(ref _telemetrySent, 0);
                Interlocked.Exchange(ref _telemetryDropped, 0);
                Interlocked.Exchange(ref _telemetrySendFailures, 0);
                Interlocked.Exchange(ref _telemetryQueueDepth, 0);
                _injectedThisWindow = 0;
                _injectionWindowStartedTimestamp = Stopwatch.GetTimestamp();
                _controlEngine = new DeterministicControlEngine(Stopwatch.Frequency, controlHz: ControlClockHz);
                Interlocked.Exchange(ref _lastProcessedControlTickTimestamp, 0);
                events = Channel.CreateUnbounded<RuntimeInputEvent>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false
                    });
                telemetry = Channel.CreateBounded<AgentTelemetry>(
                    new BoundedChannelOptions(1024)
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        FullMode = BoundedChannelFullMode.DropOldest
                    });
                lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _events = events;
                _telemetry = telemetry;
                _lifecycleCts = lifecycleCts;
            }

            try
            {
                if (_sessionStore.TryLoad(out var restoredSession) && restoredSession != null)
                {
                    _controlEngine.RestoreSession(restoredSession);
                    Logger.Info("Control session restored (warm-start baseline).");
                }

                if (!_mouseHook.Install())
                {
                    throw new InvalidOperationException("Mouse hook install failed after retries.");
                }
                if (IsKeyboardCaptureEnabled && !_keyboardHook.Install())
                {
                    throw new InvalidOperationException("Keyboard hook install failed after retries.");
                }
                if (!_foregroundHook.Install())
                {
                    throw new InvalidOperationException("Foreground hook install failed after retries.");
                }

                await _portaClient.StartAsync(lifecycleCts.Token).ConfigureAwait(false);
                SendHeartbeatTransitionNoWait("starting");

                lock (_gate)
                {
                    if (_lifecycleCts == null || _events == null || _telemetry == null)
                    {
                        throw new InvalidOperationException("Host resources were disposed during startup.");
                    }

                    _processingTask = ProcessQueueAsync(_events.Reader, _lifecycleCts.Token);
                    _metricsTask = ReportMetricsAsync(_lifecycleCts.Token);
                    _killSwitchTask = _killSwitch.MonitorAsync(_lifecycleCts.Token, OnKillSwitchTriggered);
                    _telemetryTask = ProcessTelemetryAsync(_telemetry.Reader, _lifecycleCts.Token);
                    _heartbeatTask = _heartbeat.RunAsync(_lifecycleCts.Token);
                    TransitionState(HostLifecycleState.Running);
                    Logger.Event("lifecycle_transition", ("to", HostLifecycleState.Running.ToString()));
                    Logger.Info(_killSwitch.DescribeTrigger());
                }

                SendHeartbeatTransitionNoWait("running");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "AgentHost start failed.");
                _foregroundHook.Uninstall();
                _keyboardHook.Uninstall();
                _mouseHook.Uninstall();
                try
                {
                    await _portaClient.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
                lifecycleCts?.Cancel();
                lifecycleCts?.Dispose();

                lock (_gate)
                {
                    _events = null;
                    _telemetry = null;
                    _lifecycleCts = null;
                    _processingTask = null;
                    _metricsTask = null;
                    _killSwitchTask = null;
                    _telemetryTask = null;
                    _heartbeatTask = null;
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
            Task? telemetryTask;
            Task? heartbeatTask;
            CancellationTokenSource? lifecycleCts;
            Channel<RuntimeInputEvent>? events;
            Channel<AgentTelemetry>? telemetry;
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
                telemetryTask = _telemetryTask;
                heartbeatTask = _heartbeatTask;
                lifecycleCts = _lifecycleCts;
                telemetry = _telemetry;
                _events = null;
                _telemetry = null;
                _processingTask = null;
                _metricsTask = null;
                _killSwitchTask = null;
                _telemetryTask = null;
                _heartbeatTask = null;
                _lifecycleCts = null;
            }

            try
            {
                _keyboardHook.Uninstall();
                _mouseHook.Uninstall();
                _foregroundHook.Uninstall();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Input hook uninstall failed.");
            }

            events?.Writer.TryComplete();
            telemetry?.Writer.TryComplete();

            await SafeAwaitTask(processingTask, "processing task").ConfigureAwait(false);

            lifecycleCts?.Cancel();

            await SafeAwaitTask(metricsTask, "metrics task").ConfigureAwait(false);
            await SafeAwaitTask(killSwitchTask, "kill switch task").ConfigureAwait(false);
            SendHeartbeatTransitionNoWait("stopping");
            await SafeAwaitTask(telemetryTask, "telemetry task").ConfigureAwait(false);
            await SafeAwaitTask(heartbeatTask, "heartbeat task").ConfigureAwait(false);
            await _portaClient.StopAsync(CancellationToken.None).ConfigureAwait(false);

            _sessionStore.TrySave(_controlEngine.SaveSession());
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

        private async ValueTask ApplyActuationCommandAsync(ActuationCommand command, long sourceTimestamp, CancellationToken token)
        {
            if (!IsActuationEnabled)
            {
                return;
            }

            if (command.AuthorityLevel != AuthorityLevel.Normal)
            {
                Interlocked.Increment(ref _controlReducedAuthority);
            }

            if (!ActuationMapper.TryMapToWheelDelta(command, out int boundedDelta, out string mapNote))
            {
                Logger.Warn($"Actuation mapping rejected: {mapNote}");
                Interlocked.Increment(ref _blockedBySafetyEvents);
                return;
            }

            if (!SafetyLimits.IsInjectableDelta(boundedDelta))
            {
                return;
            }

            if (!TryConsumeInjectionBudget())
            {
                Interlocked.Increment(ref _blockedBySafetyEvents);
                return;
            }

            long targetTimestamp = _delayController.ComputeTargetTimestamp(sourceTimestamp);
            await _delayController.WaitUntilAsync(targetTimestamp, token).ConfigureAwait(false);
            _scrollInjector.InjectVerticalWheel(boundedDelta);
            Interlocked.Increment(ref _injectedEvents);
            Interlocked.Increment(ref _controlActuationApplied);
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

        private AgentStatus BuildAgentStatusSnapshot()
        {
            return LifecycleState switch
            {
                HostLifecycleState.Starting => AgentStatus.Starting,
                HostLifecycleState.Running => AgentStatus.Running,
                HostLifecycleState.Stopping => AgentStatus.Stopping,
                HostLifecycleState.Faulted => AgentStatus.Degraded,
                _ => AgentStatus.Stopped
            };
        }

        private ControlParameters BuildControlSnapshot()
        {
            return new ControlParameters
            {
                SuppressionEnabled = IsScrollSuppressionEnabled,
                ActuationEnabled = IsActuationEnabled,
                FixedDelayMs = _delayController.FixedDelayMs,
                TargetProcess = _focusTracker.TargetProcessName
            };
        }

        private void EnqueueTelemetry(AgentTelemetry telemetry)
        {
            var channel = _telemetry;
            if (channel == null)
            {
                return;
            }

            if (channel.Writer.TryWrite(telemetry))
            {
                Interlocked.Increment(ref _telemetryEnqueued);
                Interlocked.Increment(ref _telemetryQueueDepth);
            }
            else
            {
                Interlocked.Increment(ref _telemetryDropped);
            }
        }

        private void SendHeartbeatTransitionNoWait(string note)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _heartbeat.SendTransitionAsync(note, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Heartbeat transition '{note}' failed.");
                }
            });
        }

        private static bool ReadBoolEnvironment(string key, bool defaultValue)
        {
            string? raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        public void Start() => StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        public void Stop() => StopAsync().GetAwaiter().GetResult();
        public void Dispose() => Stop();
    }
}
