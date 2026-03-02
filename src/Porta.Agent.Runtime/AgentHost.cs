using System;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.Control;
using PortaCommonEnvironment = Porta.Agent.Common.Environment;
using Porta.Agent.Runtime.Actuation;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Input;
using Porta.Agent.Runtime.Render;
using Porta.Agent.Runtime.Transport;
using Porta.Agent.Runtime.Win32;

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

    internal enum RuntimeMode
    {
        Normal = 0,
        Degraded = 1,
        Safe = 2
    }

    internal sealed class ScrollProcessor
    {
        private ScrollEvent? _lastEvent;

        public void Process(ScrollEvent evt)
        {
            double velocity = evt.Delta;

            if (_lastEvent != null)
            {
                double deltaTime = (evt.MonotonicTimestamp - _lastEvent.Value.MonotonicTimestamp) / (double)Stopwatch.Frequency;
                if (deltaTime > 0)
                    velocity = evt.Delta / deltaTime;
            }

            Console.WriteLine($"Scroll delta: {evt.Delta}, velocity: {velocity:F2}, timestamp_utc_ms: {evt.TimestampUtcMs}");

            _lastEvent = evt;

            // TODO: send to PORTA processing modules
            // Example: EmotionalEngine.ProcessScroll(evt.Delta, velocity);
        }
    }

    public sealed class AgentHost : IDisposable
    {
        private const int ControlClockHz = 20;
        private const int RenderLoopHz = 60;
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
        private readonly PortaCommonEnvironment.ActuationStateCache _actuationStateCache;
        private readonly PortaCommonEnvironment.VisualInterpolator _visualInterpolator;
        private readonly IRenderSurfaceSink _renderSurfaceSink;
        private readonly bool _visualSmokeTestEnabled;
        private readonly VisualSmokeSnapshotExporter? _visualSmokeSnapshotExporter;
        private readonly bool _renderFrameStreamEnabled;
        private readonly RenderFrameStreamExporter? _renderFrameStreamExporter;
        private readonly object _gate = new();

        private Channel<RuntimeInputEvent>? _events;
        private Channel<AgentTelemetry>? _telemetry;
        private CancellationTokenSource? _lifecycleCts;
        private Task? _processingTask;
        private Task? _metricsTask;
        private Task? _idleCaptureTask;
        private Task? _killSwitchTask;
        private Task? _telemetryTask;
        private Task? _heartbeatTask;
        private Task? _renderTask;
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
        private long _telemetryObserved;
        private long _telemetrySent;
        private long _telemetryDropped;
        private long _telemetrySendFailures;
        private long _telemetryOutboundSuppressed;
        private int _telemetryQueueDepth;
        private int _keyboardCaptureEnabled = 1;
        private long _visualSmokeStartTimestamp;
        private long _renderFrameCounter;
        private readonly string _runtimeProfile;
        private int _renderFrameStreamWriteFailedLogged;
        private int _runtimeMode = (int)RuntimeMode.Normal;
        private int _telemetryHealthyWindows;
        private int _telemetryOutboundSuspended;

        private const int TelemetryModeQueueDepthDegraded = 512;
        private const int TelemetryModeQueueDepthSafe = 900;
        private const int TelemetryModeFailureDeltaDegraded = 8;
        private const int TelemetryModeFailureDeltaSafe = 32;
        private const int TelemetryHealthyWindowsToNormal = 6;
        private const int TelemetryHealthyWindowsSafeToDegraded = 3;
        private const double DegradedMaxStimulationIntensity = 0.35;
        private const double DegradedMaxSceneDynamics = 0.30;
        private const double DegradedMaxVisualShift = 0.40;
        private const double DegradedMaxPacingModifier = 0.25;
        private const int DegradedMaxAbsWheelDelta = 24;

        public event Action? KillSwitchTriggered;

        public AgentHost()
        {
            _mouseHook = new MouseHook();
            _keyboardHook = new KeyboardHook();
            _foregroundHook = new ForegroundHook();
            _focusTracker = new FocusTracker(Environment.GetEnvironmentVariable("PORTA_TARGET_PROCESS"));
            _controlEngine = new DeterministicControlEngine(Stopwatch.Frequency, controlHz: ControlClockHz);
            _sessionStore = new ControlSessionStore(Environment.GetEnvironmentVariable("PORTA_CONTROL_SESSION_PATH"));
            _actuationStateCache = new PortaCommonEnvironment.ActuationStateCache();
            _visualInterpolator = new PortaCommonEnvironment.VisualInterpolator();
            _runtimeProfile = (Environment.GetEnvironmentVariable("PORTA_RUNTIME_PROFILE") ?? "headless_prod").Trim().ToLowerInvariant();
            bool renderSurfaceRequested = ReadBoolEnvironment("PORTA_RENDER_SURFACE_ENABLED", defaultValue: false);
            bool allowDebugVisual = _runtimeProfile is "debug_visual" or "smoke_test";
            bool renderSurfaceEnabled = renderSurfaceRequested && allowDebugVisual;
            _visualSmokeTestEnabled = ReadBoolEnvironment("PORTA_VISUAL_SMOKE_TEST", defaultValue: false) && _runtimeProfile == "smoke_test";
            if (renderSurfaceRequested && !allowDebugVisual)
            {
                Logger.Warn($"Render surface request ignored in profile '{_runtimeProfile}'.");
            }
            if (ReadBoolEnvironment("PORTA_VISUAL_SMOKE_TEST", defaultValue: false) && _runtimeProfile != "smoke_test")
            {
                Logger.Warn($"Visual smoke request ignored in profile '{_runtimeProfile}'.");
            }
            if (_visualSmokeTestEnabled)
            {
                _visualSmokeSnapshotExporter = new VisualSmokeSnapshotExporter(Environment.GetEnvironmentVariable("PORTA_VISUAL_SMOKE_SNAPSHOT_PATH"));
            }
            _renderSurfaceSink = CreateRenderSurfaceSink(renderSurfaceEnabled);
            _renderFrameStreamEnabled = ReadBoolEnvironment("PORTA_RENDER_FRAME_STREAM_ENABLED", defaultValue: true);
            if (_renderFrameStreamEnabled)
            {
                _renderFrameStreamExporter = new RenderFrameStreamExporter(Environment.GetEnvironmentVariable("PORTA_RENDER_FRAME_STREAM_PATH"));
            }
            _controlDiagnosticsTelemetryEnabled = ReadBoolEnvironment("PORTA_CONTROL_DIAGNOSTICS_TELEMETRY", defaultValue: false);
            if (_controlDiagnosticsTelemetryEnabled)
            {
                _controlSnapshotExporter = new ControlSnapshotExporter(Environment.GetEnvironmentVariable("PORTA_CONTROL_SNAPSHOT_EXPORT_PATH"));
            }
            _portaClient = new PortaClient();
            _heartbeat = new Heartbeat(_portaClient, hz => BuildControlSnapshot(hz), BuildAgentStatusSnapshot);
            _mouseHook.ScrollDeltaChanged += OnScrollDelta;
            _mouseHook.MouseMoved += OnMouseMoved;
            _mouseHook.ShouldSuppressScroll = ShouldSuppressScroll;
            _keyboardHook.KeyChanged += OnKeyChanged;
            _foregroundHook.ForegroundChanged += OnForegroundChanged;
            _injectionWindowStartedTimestamp = Stopwatch.GetTimestamp();
            Logger.Info(
                _focusTracker.IsEnabled
                    ? $"Focus tracking enabled for process '{_focusTracker.TargetProcessName}'."
                    : "Focus tracking disabled (set PORTA_TARGET_PROCESS to enable).");
            Logger.Info($"Render surface {(renderSurfaceEnabled ? "enabled" : "disabled")}.");
            if (_visualSmokeTestEnabled)
            {
                Logger.Info("Visual smoke test mode enabled.");
            }
            Logger.Info($"Render frame stream {(_renderFrameStreamEnabled ? "enabled" : "disabled")}.");
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
                long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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

                if (events.Writer.TryWrite(
                        RuntimeInputEvent.FromMouseWheel(
                            new ScrollEvent(Stopwatch.GetTimestamp(), tsUtcMs, delta, wasSuppressed))))
                {
                    Interlocked.Increment(ref _enqueuedEvents);
                    IncrementDepth(ref _queueDepth);
                }
                else
                {
                    Interlocked.Increment(ref _droppedEvents);
                }
            }
        }

        private void OnKeyChanged(bool isKeyDown, bool isBackspace, bool isInjected)
        {
            var events = _events;
            if (!IsRunning || events == null)
            {
                return;
            }

            long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (isInjected)
            {
                Interlocked.Increment(ref _keyboardInjectedEvents);
            }

            EnqueueTelemetry(new AgentTelemetry
            {
                AgentId = _portaClient.AgentId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = "keyboard",
                Note = $"down={isKeyDown} backspace={isBackspace} injected={isInjected}"
            });

            if (events.Writer.TryWrite(
                    RuntimeInputEvent.FromKeyboard(
                        new KeyboardEvent(Stopwatch.GetTimestamp(), tsUtcMs, isKeyDown, isBackspace, isInjected))))
            {
                Interlocked.Increment(ref _keyboardEnqueuedEvents);
                IncrementDepth(ref _queueDepth);
            }
            else
            {
                Interlocked.Increment(ref _keyboardDroppedEvents);
                Interlocked.Increment(ref _droppedEvents);
            }
        }

        private void OnForegroundChanged(uint processId, IntPtr __)
        {
            var events = _events;
            if (!IsRunning || events == null)
            {
                return;
            }

            long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string appHash = FocusIdentityNormalizer.ComputeAppHash(processId);

            EnqueueTelemetry(new AgentTelemetry
            {
                AgentId = _portaClient.AgentId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = "focus",
                Note = $"foreground_changed app_hash={appHash}"
            });

            if (events.Writer.TryWrite(
                    RuntimeInputEvent.FromFocus(
                        new FocusEvent(Stopwatch.GetTimestamp(), tsUtcMs, appHash))))
            {
                Interlocked.Increment(ref _focusEnqueuedEvents);
                IncrementDepth(ref _queueDepth);
            }
            else
            {
                Interlocked.Increment(ref _focusDroppedEvents);
                Interlocked.Increment(ref _droppedEvents);
            }
        }

        private void OnMouseMoved(int dx, int dy)
        {
            var events = _events;
            if (!IsRunning || events == null)
            {
                return;
            }

            long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (events.Writer.TryWrite(
                    RuntimeInputEvent.FromMouseMove(
                        new MouseMoveEvent(Stopwatch.GetTimestamp(), tsUtcMs, dx, dy))))
            {
                Interlocked.Increment(ref _enqueuedEvents);
                IncrementDepth(ref _queueDepth);
            }
            else
            {
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
                            if (evt.Kind == InputEventKind.MouseWheel)
                            {
                                var scroll = evt.MouseWheel;
                                _processor.Process(scroll);
                                _controlEngine.AddScrollSample(scroll.MonotonicTimestamp, scroll.Delta);
                                Interlocked.Increment(ref _processedEvents);
                                processedAnyInput = true;
                                latestInputTimestamp = Math.Max(latestInputTimestamp, scroll.MonotonicTimestamp);
                            }
                            else
                            if (evt.Kind == InputEventKind.Keyboard)
                            {
                                _controlEngine.AddKeyboardSample(evt.Keyboard.MonotonicTimestamp, evt.Keyboard.IsKeyDown);
                                Interlocked.Increment(ref _keyboardProcessedEvents);
                                processedAnyInput = true;
                                latestInputTimestamp = Math.Max(latestInputTimestamp, evt.Keyboard.MonotonicTimestamp);
                            }
                            else if (evt.Kind == InputEventKind.Focus)
                            {
                                _controlEngine.AddFocusSample(evt.Focus.MonotonicTimestamp);
                                Interlocked.Increment(ref _focusProcessedEvents);
                                processedAnyInput = true;
                                latestInputTimestamp = Math.Max(latestInputTimestamp, evt.Focus.MonotonicTimestamp);
                            }
                            else if (evt.Kind == InputEventKind.MouseMove)
                            {
                                // Mouse move samples are captured for bucket-layer aggregation.
                                // Captor and queueing only; no direct control computation here.
                                Interlocked.Increment(ref _processedEvents);
                                processedAnyInput = true;
                                latestInputTimestamp = Math.Max(latestInputTimestamp, evt.MouseMove.MonotonicTimestamp);
                            }
                            else if (evt.Kind == InputEventKind.IdleSample)
                            {
                                // Idle samples are queue events for downstream bucket aggregates.
                                // They do not count as active input timestamps.
                                Interlocked.Increment(ref _processedEvents);
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
                                DecrementDepth(ref _queueDepth);
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
                            ActuationCommand command = ApplyRuntimeModeToCommand(snapshot.SafeCommand);
                            ActuationCommand renderCommand = _visualSmokeTestEnabled
                                ? BuildVisualSmokeRenderCommand(nextTickTimestamp)
                                : command;
                            _actuationStateCache.UpdateFromCommand(renderCommand, nextTickTimestamp, out _, out _);
                            PortaCommonEnvironment.RenderParameters renderTarget = PortaCommonEnvironment.DeterministicRenderMapper.Map(renderCommand);
                            _visualInterpolator.SetTarget(renderTarget);
                            Interlocked.Exchange(ref _lastProcessedControlTickTimestamp, nextTickTimestamp);
                            Interlocked.Increment(ref _controlTicks);
                            if (_controlDiagnosticsTelemetryEnabled)
                            {
                                DateTimeOffset capturedUtc = DateTimeOffset.UtcNow;
                                ControlSnapshotTelemetry controlSnapshot = ControlSnapshotTelemetry.FromSnapshot(
                                    nextTickTimestamp,
                                    capturedUtc,
                                    snapshot);
                                if (!IsTelemetryOutboundSuspended())
                                {
                                    _controlSnapshotExporter?.TryAppend(controlSnapshot);
                                    EnqueueTelemetry(new AgentTelemetry
                                    {
                                        AgentId = _portaClient.AgentId,
                                        TimestampUtc = capturedUtc,
                                        Kind = "control_snapshot",
                                        Note = controlSnapshot.ToCanonicalJson()
                                    });
                                }
                                else
                                {
                                    Interlocked.Increment(ref _telemetryOutboundSuppressed);
                                }
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

        private async Task CaptureIdleSamplesAsync(ChannelWriter<RuntimeInputEvent> writer, CancellationToken token)
        {
            TimeSpan cadence = TimeSpan.FromSeconds(1);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(cadence, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                double idleSeconds = ReadIdleDurationSeconds();
                RuntimeInputEvent idleEvent = RuntimeInputEvent.FromIdleSample(
                    new IdleSampleEvent(tsUtcMs, idleSeconds));
                if (writer.TryWrite(idleEvent))
                {
                    Interlocked.Increment(ref _enqueuedEvents);
                    IncrementDepth(ref _queueDepth);
                }
                else
                {
                    Interlocked.Increment(ref _droppedEvents);
                }
            }
        }

        private static double ReadIdleDurationSeconds()
        {
            NativeMethods.LastInputInfo info = new()
            {
                CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>()
            };

            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                return 0d;
            }

            uint tickCount = unchecked((uint)Environment.TickCount64);
            uint elapsedMs = unchecked(tickCount - info.DwTime);
            return Math.Max(0d, elapsedMs / 1000d);
        }

        private async Task ReportMetricsAsync(CancellationToken token)
        {
            const int reportIntervalMs = 5000;
            long previousProcessed = 0;
            long previousTelemetryDropped = 0;
            long previousTelemetrySendFailures = 0;
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
                long telemetryObserved = Interlocked.Read(ref _telemetryObserved);
                long telemetrySent = Interlocked.Read(ref _telemetrySent);
                long telemetryDropped = Interlocked.Read(ref _telemetryDropped);
                long telemetrySendFailures = Interlocked.Read(ref _telemetrySendFailures);
                long telemetryOutboundSuppressed = Interlocked.Read(ref _telemetryOutboundSuppressed);
                int queueDepth = ReadDepth(ref _queueDepth);
                int telemetryQueueDepth = ReadDepth(ref _telemetryQueueDepth);
                long telemetryDroppedDelta = Math.Max(0, telemetryDropped - previousTelemetryDropped);
                long telemetrySendFailuresDelta = Math.Max(0, telemetrySendFailures - previousTelemetrySendFailures);
                previousTelemetryDropped = telemetryDropped;
                previousTelemetrySendFailures = telemetrySendFailures;
                EvaluateTelemetryRuntimeModePolicy(telemetryQueueDepth, telemetryDroppedDelta, telemetrySendFailuresDelta);
                RuntimeMode runtimeMode = GetRuntimeMode();
                bool telemetrySuspended = IsTelemetryOutboundSuspended();
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
                    ("telemetry_observed", telemetryObserved),
                    ("telemetry_enqueued", telemetryEnqueued),
                    ("telemetry_sent", telemetrySent),
                    ("telemetry_dropped", telemetryDropped),
                    ("telemetry_send_failures", telemetrySendFailures),
                    ("telemetry_outbound_suppressed", telemetryOutboundSuppressed),
                    ("telemetry_queue_depth", telemetryQueueDepth),
                    ("runtime_mode", runtimeMode.ToString()),
                    ("telemetry_outbound_suspended", telemetrySuspended),
                    ("eps", eventsPerSecond.ToString("F2")));
                Logger.Counter("queue_depth", queueDepth, ("state", LifecycleState.ToString()));
            }
        }

        private async Task ProcessTelemetryAsync(ChannelReader<AgentTelemetry> reader, CancellationToken token)
        {
            try
            {
                using var emitter = new TelemetryWindowEmitter(
                    reader,
                    EmitTelemetryWindowAsync,
                    onDequeued: dequeued => DecrementDepth(ref _telemetryQueueDepth, dequeued),
                    interval: TimeSpan.FromSeconds(1),
                    maxBatchSize: 64);
                await emitter.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Telemetry loop terminated unexpectedly.");
            }
        }

        private async Task EmitTelemetryWindowAsync(IReadOnlyList<AgentTelemetry> batch, CancellationToken token)
        {
            if (batch.Count == 0)
            {
                return;
            }

            if (IsTelemetryOutboundSuspended())
            {
                Interlocked.Add(ref _telemetryOutboundSuppressed, batch.Count);
                return;
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
                Interlocked.Exchange(ref _telemetryObserved, 0);
                Interlocked.Exchange(ref _telemetrySent, 0);
                Interlocked.Exchange(ref _telemetryDropped, 0);
                Interlocked.Exchange(ref _telemetrySendFailures, 0);
                Interlocked.Exchange(ref _telemetryOutboundSuppressed, 0);
                Interlocked.Exchange(ref _telemetryQueueDepth, 0);
                Interlocked.Exchange(ref _renderFrameCounter, 0);
                Interlocked.Exchange(ref _renderFrameStreamWriteFailedLogged, 0);
                Interlocked.Exchange(ref _runtimeMode, (int)RuntimeMode.Normal);
                Interlocked.Exchange(ref _telemetryHealthyWindows, 0);
                Interlocked.Exchange(ref _telemetryOutboundSuspended, 0);
                _injectedThisWindow = 0;
                _injectionWindowStartedTimestamp = Stopwatch.GetTimestamp();
                _visualSmokeStartTimestamp = 0;
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
                    _idleCaptureTask = CaptureIdleSamplesAsync(_events.Writer, _lifecycleCts.Token);
                    _killSwitchTask = _killSwitch.MonitorAsync(_lifecycleCts.Token, OnKillSwitchTriggered);
                    _telemetryTask = ProcessTelemetryAsync(_telemetry.Reader, _lifecycleCts.Token);
                    _heartbeatTask = _heartbeat.RunAsync(_lifecycleCts.Token);
                    _renderTask = RunRenderLoopAsync(_lifecycleCts.Token);
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
                    _idleCaptureTask = null;
                    _killSwitchTask = null;
                    _telemetryTask = null;
                    _heartbeatTask = null;
                    _renderTask = null;
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
            Task? idleCaptureTask;
            Task? killSwitchTask;
            Task? telemetryTask;
            Task? heartbeatTask;
            Task? renderTask;
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
                idleCaptureTask = _idleCaptureTask;
                killSwitchTask = _killSwitchTask;
                telemetryTask = _telemetryTask;
                heartbeatTask = _heartbeatTask;
                renderTask = _renderTask;
                lifecycleCts = _lifecycleCts;
                telemetry = _telemetry;
                _events = null;
                _telemetry = null;
                _processingTask = null;
                _metricsTask = null;
                _idleCaptureTask = null;
                _killSwitchTask = null;
                _telemetryTask = null;
                _heartbeatTask = null;
                _renderTask = null;
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
            await SafeAwaitTask(idleCaptureTask, "idle capture task").ConfigureAwait(false);
            await SafeAwaitTask(killSwitchTask, "kill switch task").ConfigureAwait(false);
            SendHeartbeatTransitionNoWait("stopping");
            await SafeAwaitTask(telemetryTask, "telemetry task").ConfigureAwait(false);
            await SafeAwaitTask(heartbeatTask, "heartbeat task").ConfigureAwait(false);
            await SafeAwaitTask(renderTask, "render task").ConfigureAwait(false);
            await _portaClient.StopAsync(CancellationToken.None).ConfigureAwait(false);

            _sessionStore.TrySave(_controlEngine.SaveSession());
            if (_visualSmokeTestEnabled && _visualSmokeSnapshotExporter != null)
            {
                Logger.Info(_visualSmokeSnapshotExporter.BuildSummary());
            }
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

        private void EvaluateTelemetryRuntimeModePolicy(int telemetryQueueDepth, long telemetryDroppedDelta, long telemetrySendFailuresDelta)
        {
            bool severePressure =
                telemetryQueueDepth >= TelemetryModeQueueDepthSafe
                || telemetryDroppedDelta >= TelemetryModeFailureDeltaSafe
                || telemetrySendFailuresDelta >= TelemetryModeFailureDeltaSafe;

            if (severePressure)
            {
                Interlocked.Exchange(ref _telemetryHealthyWindows, 0);
                SetTelemetryOutboundSuspended(true, "slo_breach_severe");
                TransitionRuntimeMode(RuntimeMode.Safe, "telemetry_pressure_severe");
                return;
            }

            bool elevatedPressure =
                telemetryQueueDepth >= TelemetryModeQueueDepthDegraded
                || telemetryDroppedDelta >= TelemetryModeFailureDeltaDegraded
                || telemetrySendFailuresDelta >= TelemetryModeFailureDeltaDegraded;

            if (elevatedPressure)
            {
                Interlocked.Exchange(ref _telemetryHealthyWindows, 0);
                SetTelemetryOutboundSuspended(true, "slo_breach_elevated");
                ForceDegradeMode("telemetry_pressure");
                return;
            }

            int healthyWindows = Interlocked.Increment(ref _telemetryHealthyWindows);
            RuntimeMode mode = GetRuntimeMode();
            if (mode == RuntimeMode.Safe && healthyWindows >= TelemetryHealthyWindowsSafeToDegraded)
            {
                TransitionRuntimeMode(RuntimeMode.Degraded, "telemetry_recovery_to_degraded");
                Interlocked.Exchange(ref _telemetryHealthyWindows, 0);
                return;
            }

            if (mode == RuntimeMode.Degraded && healthyWindows >= TelemetryHealthyWindowsToNormal)
            {
                TransitionRuntimeMode(RuntimeMode.Normal, "telemetry_recovery_to_normal");
                SetTelemetryOutboundSuspended(false, "slo_recovered");
                Interlocked.Exchange(ref _telemetryHealthyWindows, 0);
            }
        }

        private void ForceDegradeMode(string reason)
        {
            TransitionRuntimeMode(RuntimeMode.Degraded, reason);
        }

        private RuntimeMode GetRuntimeMode()
        {
            return (RuntimeMode)Volatile.Read(ref _runtimeMode);
        }

        private bool IsTelemetryOutboundSuspended()
        {
            return Volatile.Read(ref _telemetryOutboundSuspended) == 1;
        }

        private void SetTelemetryOutboundSuspended(bool suspended, string reason)
        {
            int next = suspended ? 1 : 0;
            int previous = Interlocked.Exchange(ref _telemetryOutboundSuspended, next);
            if (previous == next)
            {
                return;
            }

            string state = suspended ? "suspended" : "active";
            Logger.Event("telemetry_outbound_state", ("state", state), ("reason", reason));
            SendHeartbeatTransitionNoWait($"telemetry_outbound:{state} reason:{reason}");
        }

        private void TransitionRuntimeMode(RuntimeMode nextMode, string reason)
        {
            while (true)
            {
                int currentRaw = Volatile.Read(ref _runtimeMode);
                if (currentRaw == (int)nextMode)
                {
                    return;
                }

                RuntimeMode currentMode = (RuntimeMode)currentRaw;
                bool allowed = (currentMode, nextMode) switch
                {
                    (RuntimeMode.Normal, RuntimeMode.Degraded) => true,
                    (RuntimeMode.Normal, RuntimeMode.Safe) => true,
                    (RuntimeMode.Degraded, RuntimeMode.Safe) => true,
                    (RuntimeMode.Safe, RuntimeMode.Degraded) => true,
                    (RuntimeMode.Degraded, RuntimeMode.Normal) => true,
                    _ => false
                };

                if (!allowed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _runtimeMode, (int)nextMode, currentRaw) == currentRaw)
                {
                    Logger.Event(
                        "runtime_mode_transition",
                        ("from", currentMode.ToString()),
                        ("to", nextMode.ToString()),
                        ("reason", reason));
                    SendHeartbeatTransitionNoWait($"runtime_mode:{nextMode.ToString().ToLowerInvariant()} reason:{reason}");
                    return;
                }
            }
        }

        private static ActuationCommand ApplyDegradedCommandPolicy(ActuationCommand command)
        {
            return new ActuationCommand
            {
                ContractVersion = command.ContractVersion,
                StimulationIntensity = Math.Clamp(command.StimulationIntensity, 0.0, DegradedMaxStimulationIntensity),
                SceneDynamics = Math.Clamp(command.SceneDynamics, 0.0, DegradedMaxSceneDynamics),
                VisualTemperatureShift = Math.Clamp(command.VisualTemperatureShift, -DegradedMaxVisualShift, DegradedMaxVisualShift),
                PacingModifier = Math.Clamp(command.PacingModifier, -DegradedMaxPacingModifier, DegradedMaxPacingModifier),
                AuthorityLevel = AuthorityLevel.Safe,
                Note = string.IsNullOrWhiteSpace(command.Note) ? "runtime_mode_degraded" : $"{command.Note}|runtime_mode_degraded"
            };
        }

        private ActuationCommand ApplyRuntimeModeToCommand(ActuationCommand command)
        {
            RuntimeMode mode = GetRuntimeMode();
            return mode switch
            {
                RuntimeMode.Safe => ActuationCommand.Safe("runtime_mode_safe"),
                RuntimeMode.Degraded => ApplyDegradedCommandPolicy(command),
                _ => command
            };
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

            if (GetRuntimeMode() != RuntimeMode.Normal && Math.Abs(boundedDelta) > DegradedMaxAbsWheelDelta)
            {
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

        private async Task RunRenderLoopAsync(CancellationToken token)
        {
            long frameInterval = Math.Max(1L, Stopwatch.Frequency / RenderLoopHz);
            long nextFrameTimestamp = Stopwatch.GetTimestamp();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (now < nextFrameTimestamp)
                    {
                        int delayMs = (int)Math.Max(1, ((nextFrameTimestamp - now) * 1000) / Stopwatch.Frequency);
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                        continue;
                    }

                    PortaCommonEnvironment.RenderParameters current =
                        _visualInterpolator.AdvanceToTimestamp(now, Stopwatch.Frequency);
                    PortaCommonEnvironment.RenderSurfaceFrame frame =
                        PortaCommonEnvironment.RenderSurfaceComposer.Compose(current, now);
                    _renderSurfaceSink.Draw(frame);
                    if (_visualSmokeTestEnabled &&
                        _visualSmokeSnapshotExporter != null &&
                        Interlocked.Increment(ref _renderFrameCounter) % 30 == 0)
                    {
                        _visualSmokeSnapshotExporter.TryAppend(frame);
                    }
                    else
                    {
                        Interlocked.Increment(ref _renderFrameCounter);
                    }

                    if (_renderFrameStreamEnabled &&
                        _renderFrameStreamExporter != null &&
                        _renderFrameCounter % 3 == 0)
                    {
                        bool ok = _renderFrameStreamExporter.TryAppend(frame);
                        if (!ok && Interlocked.Exchange(ref _renderFrameStreamWriteFailedLogged, 1) == 0)
                        {
                            Logger.Warn("Render frame stream export failed. Set PORTA_RENDER_FRAME_STREAM_PATH to a writable location.");
                        }
                    }

                    nextFrameTimestamp += frameInterval;
                    if (nextFrameTimestamp < now - frameInterval)
                    {
                        nextFrameTimestamp = now + frameInterval;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _processingErrors);
                Logger.Error(ex, "Render loop terminated unexpectedly.");
            }
        }

        private ActuationCommand BuildVisualSmokeRenderCommand(long tickTimestamp)
        {
            if (_visualSmokeStartTimestamp == 0)
            {
                _visualSmokeStartTimestamp = tickTimestamp;
            }

            double elapsedSeconds = Math.Max(0.0, (tickTimestamp - _visualSmokeStartTimestamp) / (double)Stopwatch.Frequency);
            if (elapsedSeconds < 15.0)
            {
                double control = 0.65 + (0.20 * Math.Sin(elapsedSeconds * 0.7));
                return ActuationCommand.FromControlSignal(control, AuthorityLevel.Normal, "visual_smoke_normal");
            }

            if (elapsedSeconds < 30.0)
            {
                double control = 0.30 + (0.10 * Math.Sin(elapsedSeconds * 0.5));
                return ActuationCommand.FromControlSignal(control, AuthorityLevel.Reduced, "visual_smoke_reduced");
            }

            return ActuationCommand.Safe("visual_smoke_safe");
        }

        private static IRenderSurfaceSink CreateRenderSurfaceSink(bool enabled)
        {
            if (!enabled)
            {
                return NullRenderSurfaceSink.Instance;
            }

            try
            {
                return new WinFormsRenderSurfaceSink();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to create WinForms render sink. Falling back to null sink.");
                return NullRenderSurfaceSink.Instance;
            }
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
            return BuildControlSnapshot(0.0);
        }

        private ControlParameters BuildControlSnapshot(double heartbeatFrequencyHz)
        {
            RuntimeMode mode = GetRuntimeMode();
            return new ControlParameters
            {
                SuppressionEnabled = IsScrollSuppressionEnabled,
                ActuationEnabled = IsActuationEnabled,
                RuntimeMode = mode.ToString().ToLowerInvariant(),
                HeartbeatFrequencyHz = heartbeatFrequencyHz,
                TelemetryOutboundSuspended = IsTelemetryOutboundSuspended(),
                FixedDelayMs = _delayController.FixedDelayMs,
                TargetProcess = _focusTracker.TargetProcessName
            };
        }

        private void EnqueueTelemetry(AgentTelemetry telemetry)
        {
            Interlocked.Increment(ref _telemetryObserved);

            if (IsTelemetryOutboundSuspended())
            {
                Interlocked.Increment(ref _telemetryOutboundSuppressed);
                return;
            }

            var channel = _telemetry;
            if (channel == null)
            {
                return;
            }

            if (channel.Writer.TryWrite(telemetry))
            {
                Interlocked.Increment(ref _telemetryEnqueued);
                IncrementDepth(ref _telemetryQueueDepth);
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

        private static int ReadDepth(ref int depth)
        {
            return Volatile.Read(ref depth);
        }

        private static void IncrementDepth(ref int depth)
        {
            Interlocked.Increment(ref depth);
        }

        private static void DecrementDepth(ref int depth, int amount = 1)
        {
            if (amount <= 0)
            {
                return;
            }

            while (true)
            {
                int current = Volatile.Read(ref depth);
                int next = current - amount;
                if (next < 0)
                {
                    next = 0;
                }

                if (Interlocked.CompareExchange(ref depth, next, current) == current)
                {
                    return;
                }
            }
        }

        public void Start() => StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        public void Stop() => StopAsync().GetAwaiter().GetResult();
        public void Dispose()
        {
            try
            {
                Stop();
            }
            finally
            {
                _renderSurfaceSink.Dispose();
            }
        }
    }
}
