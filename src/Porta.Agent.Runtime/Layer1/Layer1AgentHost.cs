using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Forms;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Runtime.Layer2;
using Porta.Agent.Runtime.Layer3;
using Porta.Agent.Runtime.Layer4;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Input;
using Porta.Agent.Runtime.Transport;
using Porta.Agent.Runtime.Win32;

namespace Porta.Agent.Runtime.Layer1;

public sealed class Layer1AgentHost : IDisposable
{
    private const int WindowSeconds = 10;
    private const string RequiredPolicy = "whitelist_v1";
    private const string RequiredSchemaVersion = "1.0.0";
    private static readonly Regex SemverRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);
    private readonly object _gate = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly ForegroundHook _foregroundHook = new();
    private readonly PortaClient _client = new();
    private readonly Heartbeat _heartbeat;
    private readonly Layer1ConsentStore _consentStore = new(Environment.GetEnvironmentVariable("PORTA_CONSENT_STATE_PATH"));
    private readonly Layer1TrayController _tray = new();
    private readonly Layer1ControlPlaneStream _controlPlaneStream = new();
    private readonly SystemCpuSampler _systemCpu = new();
    private readonly NetworkSampler _networkSampler = new();
    private readonly DiskIoSampler _diskIoSampler = new();
    private readonly Layer2NormalizationEngine _normalizationEngine;
    private readonly Layer3InferenceEngine _inferenceEngine;
    private readonly Layer4StabilityEngine _stabilityEngine;
    private readonly bool _isInteractiveSession = Environment.UserInteractive;
    private readonly string _machineId = BuildMachineId();
    private readonly string _agentId = Environment.MachineName;
    private readonly string _signatureKeyId;
    private readonly byte[] _signatureKey;
    private Channel<AgentTelemetry>? _telemetry;
    private Channel<Layer2SignalSample>? _signalSamples;
    private CancellationTokenSource? _cts;
    private Task? _collectorTask;
    private Task? _normalizationTask;
    private Task? _inferenceTask;
    private Task? _telemetryTask;
    private Task? _heartbeatTask;
    private Task? _controlPlaneTask;
    private int _running;
    private int _errorCount;
    private int _paused;
    private Layer1ConsentState _consentState;
    private long _keystrokesInWindow;
    private long _backspacesInWindow;
    private long _appSwitchesInWindow;
    private double _mouseDistanceInWindow;
    private NativeMethods.Point _lastCursorPos;
    private bool _hasLastCursorPos;
    private DateTimeOffset? _lastSignalAtUtc;
    private string _lastSummary = "No signals emitted yet.";
    private readonly bool _consentStateCorrupted;
    private readonly Queue<Layer1TrayPrimitiveView> _recentPrimitives = new();
    private readonly Queue<Layer1TrayInterventionView> _recentInterventions = new();
    private readonly Dictionary<string, double> _riskProbabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stress"] = 0,
        ["overload"] = 0,
        ["focus"] = 0,
        ["fatigue"] = 0
    };
    private double _riskSigma;
    private DateTimeOffset? _pauseUntilUtc;
    private string _lastCommittedState = "stable";
    private bool _trayOverloadActive;
    private bool _trayDeepWorkActive;
    private readonly Layer1LocalLogStore _localLogStore;
    private readonly string _expectedPolicyVersion = Environment.GetEnvironmentVariable("PORTA_EXPECTED_POLICY_VERSION") ?? "1.0.0";
    private long _agentEventsTotal;
    private long _interventionsAppliedTotal;
    private long _interventionsFailedTotal;
    private long _correlationTraceCompleteTotal;
    private long _correlationTraceIncompleteTotal;
    private long _agentEventsSnapshot;
    private long _interventionsAppliedSnapshot;
    private long _interventionsFailedSnapshot;
    private double _captureToPrimitiveLatencyMs;
    private double _primitiveToInferenceLatencyMs;
    private double _policyEvaluationLatencyMs;
    private double _auditLogWriteLatencyMs;
    private DateTimeOffset _latestPrimitiveAtUtc;
    private DateTimeOffset _lastMetricsEmitUtc = DateTimeOffset.UtcNow;

    public Layer1AgentHost()
    {
        _heartbeat = new Heartbeat(_client, hz => BuildControlSnapshot(hz), BuildAgentStatusSnapshot);
        _consentState = _consentStore.Load(out bool stateFileMissing, out bool stateCorrupted);
        _consentStateCorrupted = stateCorrupted;
        if (stateFileMissing)
        {
            _consentState = Layer1ConsentState.NotInstalled;
        }
        if (stateCorrupted)
        {
            _consentState = Layer1ConsentState.InstalledNotConsented;
        }

        _signatureKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{_machineId}|{Environment.UserDomainName}|PORTA_LAYER1"));
        _signatureKeyId = Convert.ToHexString(SHA256.HashData(_signatureKey)).Substring(0, 16);
        _localLogStore = new Layer1LocalLogStore(_machineId);
        _normalizationEngine = new Layer2NormalizationEngine(
            Layer2NormalizationConfig.FromEnvironment(),
            samplePeriodSeconds: WindowSeconds);
        _inferenceEngine = new Layer3InferenceEngine(new Layer3InferenceConfig());
        _stabilityEngine = new Layer4StabilityEngine(new Layer4StabilityConfig());
        _keyboardHook.KeyChanged += OnKeyChanged;
        _foregroundHook.ForegroundChanged += (_, _) =>
        {
            if (!IsCollectionDisabled())
            {
                Interlocked.Increment(ref _appSwitchesInWindow);
            }
        };
        _tray.PauseRequested += PauseByUser;
        _tray.ResumeRequested += ResumeByUser;
        _tray.RevokeRequested += RevokeConsent;
        _tray.ExitRequested += () => _cts?.Cancel();
        _tray.ExportJsonRequested += () => _localLogStore.ExportJson();
        _tray.ExportCsvRequested += () => _localLogStore.ExportCsv();
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (_isInteractiveSession && !_tray.Start())
            {
                Logger.Warn("Tray startup failed; continuing in headless mode.");
            }
            else if (!_isInteractiveSession)
            {
                Logger.Warn("Non-interactive session detected; tray disabled.");
            }

            _telemetry = Channel.CreateBounded<AgentTelemetry>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _signalSamples = Channel.CreateBounded<Layer2SignalSample>(new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

            if (_consentStateCorrupted)
            {
                HandleFailClosed("corrupted_state");
                throw new InvalidOperationException("Consent state is corrupted.");
            }

            ValidateRuntimeConfiguration();
            ResolveConsentOnStart();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            await _client.StartAsync(_cts.Token).ConfigureAwait(false);
            EnsureIdentityNotAnonymous();

            if (!IsCollectionDisabled())
            {
                EnsureHooksInstalled();
            }

            _collectorTask = CollectLoopAsync(_cts.Token);
            _normalizationTask = NormalizeLoopAsync(_cts.Token);
            _inferenceTask = InferenceLoopAsync(_cts.Token);
            _telemetryTask = TelemetryLoopAsync(_cts.Token);
            _heartbeatTask = _heartbeat.RunAsync(_cts.Token);
            _controlPlaneTask = ControlPlaneLoopAsync(_cts.Token);
            UpdateTray(IsCollectionDisabled() ? "Paused" : "Active");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            Logger.Error(ex, "Layer1 start failed.");
            HandleFailClosed("start_failed");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
        {
            return;
        }

        _cts?.Cancel();
        UninstallHooks();
        _telemetry?.Writer.TryComplete();
        _signalSamples?.Writer.TryComplete();
        await SafeAwait(_collectorTask).ConfigureAwait(false);
        await SafeAwait(_normalizationTask).ConfigureAwait(false);
        await SafeAwait(_inferenceTask).ConfigureAwait(false);
        await SafeAwait(_telemetryTask).ConfigureAwait(false);
        await SafeAwait(_heartbeatTask).ConfigureAwait(false);
        await SafeAwait(_controlPlaneTask).ConfigureAwait(false);
        await _client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _tray.Dispose();
        _localLogStore.Dispose();
        _cts?.Dispose();
        _cts = null;
        _collectorTask = null;
        _normalizationTask = null;
        _inferenceTask = null;
        _telemetryTask = null;
        _heartbeatTask = null;
        _controlPlaneTask = null;
        _telemetry = null;
        _signalSamples = null;
    }

    private void ResolveConsentOnStart()
    {
        if (_consentState == Layer1ConsentState.NotInstalled)
        {
            _consentState = Layer1ConsentState.InstalledNotConsented;
            _consentStore.Save(_consentState);
        }

        if (_consentState == Layer1ConsentState.ConsentedActive || _consentState == Layer1ConsentState.PausedByUser)
        {
            if (_consentState == Layer1ConsentState.PausedByUser)
            {
                Interlocked.Exchange(ref _paused, 1);
            }
            return;
        }

        if (!_isInteractiveSession)
        {
            HandleFailClosed("consent_prompt_unavailable_noninteractive");
            Logger.Warn("Consent prompt unavailable in non-interactive session; running fail-closed.");
            return;
        }

        DialogResult result = MessageBox.Show(
            $"{Layer1Disclosure.Text}\r\n\r\nSelect Yes to consent and activate Layer 1.",
            "PORTA Consent",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            _consentState = Layer1ConsentState.ConsentedActive;
            _consentStore.Save(_consentState);
            _auditLogWriteLatencyMs = _localLogStore.Log("agent.consent_granted", new
            {
                consent_state = _consentState.ToString().ToLowerInvariant(),
                disclosure = Layer1Disclosure.Text,
                accepted_at = DateTimeOffset.UtcNow
            });
            EmitEvent("agent.consent_granted", "consent", 1, "count", "explicit_accept", "public");
            return;
        }

        HandleFailClosed("consent_missing");
    }

    private void ValidateRuntimeConfiguration()
    {
        string policy = (Environment.GetEnvironmentVariable("PORTA_LAYER1_SIGNAL_POLICY") ?? RequiredPolicy).Trim().ToLowerInvariant();
        if (!string.Equals(policy, RequiredPolicy, StringComparison.Ordinal))
        {
            HandleFailClosed("policy_misconfiguration");
            throw new InvalidOperationException($"Invalid Layer1 policy '{policy}'. Expected '{RequiredPolicy}'.");
        }

        string schemaVersion = (Environment.GetEnvironmentVariable("PORTA_SIGNAL_SCHEMA_VERSION") ?? RequiredSchemaVersion).Trim();
        if (!SemverRegex.IsMatch(schemaVersion) || !string.Equals(schemaVersion, RequiredSchemaVersion, StringComparison.Ordinal))
        {
            HandleFailClosed("version_mismatch");
            throw new InvalidOperationException($"Schema version mismatch '{schemaVersion}'. Expected '{RequiredSchemaVersion}'.");
        }
    }

    private void EnsureIdentityNotAnonymous()
    {
        if (string.IsNullOrWhiteSpace(_agentId) || string.IsNullOrWhiteSpace(_machineId))
        {
            HandleFailClosed("anonymous_identity");
            throw new InvalidOperationException("Anonymous emission is not allowed.");
        }
    }

    private async Task CollectLoopAsync(CancellationToken token)
    {
        TimeSpan sampleInterval = TimeSpan.FromSeconds(1);
        DateTimeOffset windowStart = DateTimeOffset.UtcNow;
        TimeSpan window = TimeSpan.FromSeconds(WindowSeconds);
        TimeSpan processCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        DateTimeOffset cpuWindowStart = DateTimeOffset.UtcNow;
        NativeMethods.GetCursorPos(out _lastCursorPos);
        _hasLastCursorPos = true;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(sampleInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            TryAutoResumePaused();
            if (IsCollectionDisabled())
            {
                continue;
            }

            try
            {
                if (NativeMethods.GetCursorPos(out NativeMethods.Point currentPos))
                {
                    lock (_gate)
                    {
                        if (_hasLastCursorPos)
                        {
                            double dx = currentPos.X - _lastCursorPos.X;
                            double dy = currentPos.Y - _lastCursorPos.Y;
                            _mouseDistanceInWindow += Math.Sqrt((dx * dx) + (dy * dy));
                        }
                        _lastCursorPos = currentPos;
                        _hasLastCursorPos = true;
                    }
                }

                if ((DateTimeOffset.UtcNow - windowStart) < window)
                {
                    continue;
                }

                double elapsedWindowSeconds = Math.Max(1.0, (DateTimeOffset.UtcNow - windowStart).TotalSeconds);
                windowStart = DateTimeOffset.UtcNow;
                string correlationId = CreateCorrelationId();

                long keyCount = Interlocked.Exchange(ref _keystrokesInWindow, 0);
                long backspaceCount = Interlocked.Exchange(ref _backspacesInWindow, 0);
                long appSwitches = Interlocked.Exchange(ref _appSwitchesInWindow, 0);
                double mouseDistance;
                lock (_gate)
                {
                    mouseDistance = _mouseDistanceInWindow;
                    _mouseDistanceInWindow = 0;
                }

                PublishSignal(correlationId, "interaction.keystroke_rate", keyCount / elapsedWindowSeconds, elapsedWindowSeconds, "count_per_second", "keyboard_hook_aggregate");
                PublishSignal(correlationId, "interaction.backspace_rate", backspaceCount / elapsedWindowSeconds, elapsedWindowSeconds, "count_per_second", "keyboard_hook_aggregate");
                PublishSignal(correlationId, "interaction.mouse_movement_distance", mouseDistance, elapsedWindowSeconds, "pixels", "cursor_position_delta_aggregate");
                PublishSignal(correlationId, "interaction.app_switch_frequency", appSwitches / elapsedWindowSeconds, elapsedWindowSeconds, "count_per_second", "foreground_event_count");
                PublishSignal(correlationId, "interaction.idle_duration", GetIdleDurationSeconds(), elapsedWindowSeconds, "seconds", "last_input_info");
                PublishSignal(correlationId, "system.cpu_utilization", _systemCpu.ReadPercent(), elapsedWindowSeconds, "percent", "get_system_times");
                PublishSignal(correlationId, "system.memory_utilization", ReadMemoryPercent(), elapsedWindowSeconds, "percent", "global_memory_status");
                PublishSignal(correlationId, "system.disk_io_rate", _diskIoSampler.ReadBytesPerSecond(elapsedWindowSeconds), elapsedWindowSeconds, "bytes_per_second", "drive_free_space_delta");
                PublishSignal(correlationId, "system.network_throughput", _networkSampler.ReadBytesPerSecond(elapsedWindowSeconds), elapsedWindowSeconds, "bytes_per_second", "network_interface_stats");
                PublishSignal(correlationId, "system.process_count", Process.GetProcesses().Length, elapsedWindowSeconds, "count", "process_snapshot");
                PublishSignal(correlationId, "system.service_health_status", 1, elapsedWindowSeconds, "up_down", "host_lifecycle");

                TimeSpan processCpuEnd = Process.GetCurrentProcess().TotalProcessorTime;
                DateTimeOffset cpuWindowEnd = DateTimeOffset.UtcNow;
                double cpuElapsedSeconds = Math.Max(0.1, (cpuWindowEnd - cpuWindowStart).TotalSeconds);
                double agentCpuPercent = ((processCpuEnd - processCpuStart).TotalSeconds / (cpuElapsedSeconds * Environment.ProcessorCount)) * 100.0;
                processCpuStart = processCpuEnd;
                cpuWindowStart = cpuWindowEnd;

                Process proc = Process.GetCurrentProcess();
                PublishSignal(correlationId, "agent.cpu_usage", Math.Clamp(agentCpuPercent, 0, 100), elapsedWindowSeconds, "percent", "process_cpu_delta");
                PublishSignal(correlationId, "agent.memory_usage", proc.WorkingSet64 / (1024.0 * 1024.0), elapsedWindowSeconds, "megabytes", "working_set");
                PublishSignal(correlationId, "agent.event_queue_depth", ReadQueueDepth(), elapsedWindowSeconds, "count", "telemetry_queue_depth");
                PublishSignal(correlationId, "agent.error_count", Volatile.Read(ref _errorCount), elapsedWindowSeconds, "count", "runtime_counter");

                _lastSummary =
                    $"Window: {WindowSeconds}s\r\n"
                    + $"Keystroke rate: {(keyCount / elapsedWindowSeconds):F2}/s\r\n"
                    + $"Backspace rate: {(backspaceCount / elapsedWindowSeconds):F2}/s\r\n"
                    + $"Mouse distance: {mouseDistance:F0} px\r\n"
                    + $"App switches: {(appSwitches / elapsedWindowSeconds):F2}/s\r\n"
                    + $"Idle duration: {GetIdleDurationSeconds():F1}s";
                EmitObservabilityMetricsIfDue(DateTimeOffset.UtcNow);
                UpdateTray("Active");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                Logger.Error(ex, "Layer1 collection cycle failed.");
                EmitEvent("agent.error", "error", 1, "count", "runtime_exception", "restricted");
                UpdateTray("Error");
            }
        }
    }

    private async Task TelemetryLoopAsync(CancellationToken token)
    {
        Channel<AgentTelemetry>? channel = _telemetry;
        if (channel == null)
        {
            return;
        }

        using var emitter = new TelemetryWindowEmitter(
            channel.Reader,
            async (batch, ct) =>
            {
                bool sent = await _client.SendTelemetryBatchAsync(batch, ct).ConfigureAwait(false);
                if (!sent)
                {
                    Interlocked.Increment(ref _errorCount);
                }
            },
            onDequeued: _ => { },
            interval: TimeSpan.FromSeconds(1),
            maxBatchSize: 64);
        await emitter.RunAsync(token).ConfigureAwait(false);
    }

    private async Task NormalizeLoopAsync(CancellationToken token)
    {
        Channel<Layer2SignalSample>? signalChannel = _signalSamples;
        if (signalChannel == null)
        {
            return;
        }

        try
        {
            await foreach (Layer2SignalSample sample in signalChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                Layer2PrimitiveResult result = _normalizationEngine.Transform(sample);
                if (!result.Success)
                {
                    EmitEvent("agent.error", "error", 1, "count", result.ErrorReason, "restricted");
                    continue;
                }

                EmitPrimitive(sample, result);
                _inferenceEngine.RecordPrimitive(new Layer3PrimitiveInput(
                    PrimitiveId: result.PrimitiveId,
                    CorrelationId: result.CorrelationId,
                    MachineId: sample.MachineId,
                    Name: result.Name,
                    Value: result.Value,
                    Unit: result.Unit,
                    ZScore: result.ZScore,
                    Ewma: result.Ewma,
                    Confidence: result.Confidence,
                    ComputedAtUtc: result.ComputedAtUtc));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            Logger.Error(ex, "Layer2 normalization loop failed.");
            EmitEvent("agent.error", "error", 1, "count", "normalization_loop_failed", "restricted");
        }
    }

    private async Task InferenceLoopAsync(CancellationToken token)
    {
        TimeSpan cadence = TimeSpan.FromSeconds(10);
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

            if (IsCollectionDisabled())
            {
                continue;
            }

            DateTimeOffset evalAtUtc = DateTimeOffset.UtcNow;
            Layer3StateResult result = _inferenceEngine.Evaluate(evalAtUtc);
            if (!result.Success)
            {
                EmitEvent("agent.error", "error", 1, "count", result.ErrorReason, "restricted", correlationId: result.CorrelationId);
                continue;
            }

            _primitiveToInferenceLatencyMs = _latestPrimitiveAtUtc == DateTimeOffset.MinValue
                ? 0
                : Math.Max(0, (evalAtUtc - _latestPrimitiveAtUtc).TotalMilliseconds);
            DateTimeOffset policyStartUtc = DateTimeOffset.UtcNow;
            Layer4StateResult stabilized = _stabilityEngine.Stabilize(result);
            _policyEvaluationLatencyMs = Math.Max(0, (DateTimeOffset.UtcNow - policyStartUtc).TotalMilliseconds);
            EmitState(stabilized, result);
        }
    }

    private async Task ControlPlaneLoopAsync(CancellationToken token)
    {
        string? wsUrl = Environment.GetEnvironmentVariable("PORTA_CONTROL_PLANE_WS_URL");
        if (string.IsNullOrWhiteSpace(wsUrl))
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _controlPlaneStream.RunAsync(wsUrl, OnInterventionReceived, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                Logger.Error(ex, "Control-plane stream failed.");
                EmitEvent("agent.error", "error", 1, "count", "control_plane_stream_failed", "restricted");
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
        }
    }

    private void OnKeyChanged(bool isKeyDown, bool isBackspace, bool isInjected)
    {
        if (!isKeyDown || isInjected || IsCollectionDisabled())
        {
            return;
        }

        Interlocked.Increment(ref _keystrokesInWindow);
        if (isBackspace)
        {
            Interlocked.Increment(ref _backspacesInWindow);
        }
    }

    private void PauseByUser(int minutes)
    {
        int clampedMinutes = Math.Clamp(minutes, 1, 60);
        Interlocked.Exchange(ref _paused, 1);
        _consentState = Layer1ConsentState.PausedByUser;
        _pauseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(clampedMinutes);
        _consentStore.Save(_consentState);
        UninstallHooks();
        FlushAggregationBuffers();
        EmitEvent("agent.paused", "lifecycle", clampedMinutes, "minutes", "user_pause", "public");
        UpdateTray("Paused");
    }

    private void ResumeByUser()
    {
        if (_consentState == Layer1ConsentState.Revoked || _consentState == Layer1ConsentState.InstalledNotConsented)
        {
            return;
        }

        try
        {
            EnsureHooksInstalled();
            Interlocked.Exchange(ref _paused, 0);
            _consentState = Layer1ConsentState.ConsentedActive;
            _pauseUntilUtc = null;
            _consentStore.Save(_consentState);
            EmitEvent("agent.resumed", "lifecycle", 1, "count", "user_resume", "public");
            UpdateTray("Active");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            Logger.Error(ex, "Resume failed.");
            HandleFailClosed("resume_failed");
        }
    }

    private void RevokeConsent()
    {
        HandleFailClosed("consent_revoked", revoked: true);
    }

    private bool IsCollectionDisabled()
    {
        if (_consentState != Layer1ConsentState.ConsentedActive)
        {
            return true;
        }

        return Volatile.Read(ref _paused) == 1;
    }

    private AgentStatus BuildAgentStatusSnapshot()
    {
        if (Volatile.Read(ref _running) == 0)
        {
            return AgentStatus.Stopped;
        }

        return IsCollectionDisabled() ? AgentStatus.Degraded : AgentStatus.Running;
    }

    private ControlParameters BuildControlSnapshot(double heartbeatFrequencyHz)
    {
        return new ControlParameters
        {
            SuppressionEnabled = false,
            ActuationEnabled = false,
            RuntimeMode = IsCollectionDisabled() ? "degraded" : "normal",
            HeartbeatFrequencyHz = heartbeatFrequencyHz,
            TelemetryOutboundSuspended = false,
            FixedDelayMs = 0,
            TargetProcess = null
        };
    }

    private void FlushAggregationBuffers()
    {
        Interlocked.Exchange(ref _keystrokesInWindow, 0);
        Interlocked.Exchange(ref _backspacesInWindow, 0);
        Interlocked.Exchange(ref _appSwitchesInWindow, 0);
        lock (_gate)
        {
            _mouseDistanceInWindow = 0;
        }
    }

    private void PublishSignal(string correlationId, string metricName, double value, double windowDurationSeconds, string unit, string collectionMethod)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            EmitEvent("agent.error", "error", 1, "count", "numeric_instability_input_nan_or_inf", "restricted", correlationId: correlationId);
            return;
        }

        Interlocked.Increment(ref _agentEventsTotal);
        var sample = new Layer2SignalSample(
            MachineId: _machineId,
            CorrelationId: correlationId,
            MetricName: metricName,
            Value: value,
            Unit: unit,
            WindowDurationSeconds: windowDurationSeconds,
            CollectionMethod: collectionMethod,
            PrivacyClass: "restricted",
            TimestampUtc: DateTimeOffset.UtcNow);
        _signalSamples?.Writer.TryWrite(sample);
    }

    private void EmitPrimitive(Layer2SignalSample source, Layer2PrimitiveResult result)
    {
        DateTimeOffset now = result.ComputedAtUtc;
        string signaturePayload = BuildSignaturePayload(
            "agent.primitive",
            result.Name,
            result.Value,
            result.Unit,
            source.CollectionMethod,
            source.PrivacyClass,
            source.WindowDurationSeconds,
            now);
        string signature = Sign(signaturePayload);

        AgentTelemetry entry = new()
        {
            AgentId = _agentId,
            MachineId = _machineId,
            CorrelationId = result.CorrelationId,
            TimestampUtc = now,
            EventType = "agent.primitive",
            SchemaVersion = RequiredSchemaVersion,
            Kind = "layer2",
            MetricName = result.Name,
            Name = result.Name,
            PrimitiveId = result.PrimitiveId,
            Value = result.Value,
            Unit = result.Unit,
            BaselineMean = result.BaselineMean,
            BaselineStd = result.BaselineStd,
            ZScore = result.ZScore,
            Ewma = result.Ewma,
            WindowDurationSeconds = source.WindowDurationSeconds,
            WindowSizeSeconds = result.WindowSizeSeconds,
            Confidence = result.Confidence,
            ComputedAtUtc = result.ComputedAtUtc,
            CollectionMethod = source.CollectionMethod,
            PrivacyClass = source.PrivacyClass,
            ConsentState = _consentState.ToString().ToLowerInvariant(),
            SignatureAlgorithm = "hmac-sha256",
            SignatureKeyId = _signatureKeyId,
            Signature = signature,
            CaptureToPrimitiveLatencyMs = Math.Max(0, (result.ComputedAtUtc - source.TimestampUtc).TotalMilliseconds),
            CorrelationTraceComplete = !string.IsNullOrWhiteSpace(result.CorrelationId),
            Note = "signed"
        };

        _lastSignalAtUtc = entry.TimestampUtc;
        _telemetry?.Writer.TryWrite(entry);
        _captureToPrimitiveLatencyMs = entry.CaptureToPrimitiveLatencyMs ?? 0;
        _latestPrimitiveAtUtc = result.ComputedAtUtc;
        AppendPrimitiveForTray(result);
        _auditLogWriteLatencyMs = _localLogStore.Log("agent.primitive", new
        {
            correlation_id = result.CorrelationId,
            primitive_id = result.PrimitiveId,
            name = result.Name,
            value = result.Value,
            unit = result.Unit,
            z_score = result.ZScore,
            ewma = result.Ewma,
            confidence = result.Confidence,
            computed_at = result.ComputedAtUtc
        });
        if (string.IsNullOrWhiteSpace(result.CorrelationId))
        {
            Interlocked.Increment(ref _correlationTraceIncompleteTotal);
        }
        else
        {
            Interlocked.Increment(ref _correlationTraceCompleteTotal);
        }
    }

    private void EmitState(Layer4StateResult stabilized, Layer3StateResult layer3)
    {
        DateTimeOffset now = stabilized.EvaluatedAtUtc;
        string signaturePayload = BuildSignaturePayload(
            "agent.state",
            stabilized.CommittedState,
            stabilized.Probability,
            "probability",
            "deterministic_layer4",
            "restricted",
            windowDurationSeconds: 10,
            now);
        string signature = Sign(signaturePayload);

        AgentTelemetry entry = new()
        {
            AgentId = _agentId,
            MachineId = _machineId,
            CorrelationId = stabilized.CorrelationId,
            TimestampUtc = now,
            EventType = "agent.state",
            SchemaVersion = RequiredSchemaVersion,
            Kind = "layer4",
            StateId = layer3.StateId,
            StateName = stabilized.CommittedState,
            Probability = stabilized.Probability,
            Confidence = stabilized.Confidence,
            GlobalRiskScore = layer3.GlobalRiskScore,
            RiskDelta = layer3.RiskDelta,
            TrendDirection = layer3.TrendDirection,
            ModelVersion = $"{layer3.ModelVersion}|stability-v1.0.0",
            PolicyId = "local-state-policy-v1",
            PolicyVersion = _expectedPolicyVersion,
            SignatureVerified = true,
            InterventionStatus = stabilized.TransitionReason,
            DistributionJson = stabilized.DistributionJson,
            ContributingPrimitivesJson = stabilized.ContributingPrimitivesJson,
            ExplanationJson = stabilized.ExplanationJson,
            ComputedAtUtc = stabilized.EvaluatedAtUtc,
            CollectionMethod = "deterministic_stability_state_machine",
            PrivacyClass = "restricted",
            ConsentState = _consentState.ToString().ToLowerInvariant(),
            SignatureAlgorithm = "hmac-sha256",
            SignatureKeyId = _signatureKeyId,
            Signature = signature,
            PrimitiveToInferenceLatencyMs = _primitiveToInferenceLatencyMs,
            PolicyEvaluationLatencyMs = _policyEvaluationLatencyMs,
            AuditLogWriteLatencyMs = _auditLogWriteLatencyMs,
            CorrelationTraceComplete = !string.IsNullOrWhiteSpace(stabilized.CorrelationId),
            Note = "signed"
        };

        _telemetry?.Writer.TryWrite(entry);
        UpdateRiskForTray(stabilized, layer3);
        _auditLogWriteLatencyMs = _localLogStore.Log("agent.state", new
        {
            correlation_id = stabilized.CorrelationId,
            state_id = layer3.StateId,
            state_name = stabilized.CommittedState,
            probability = stabilized.Probability,
            confidence = stabilized.Confidence,
            evaluated_at = stabilized.EvaluatedAtUtc
        });
    }

    private void EmitEvent(
        string eventType,
        string metricName,
        double value,
        string unit,
        string collectionMethod,
        string privacyClass,
        double windowDurationSeconds = WindowSeconds,
        string? correlationId = null)
    {
        if (IsCollectionDisabled() && eventType == "signal.sample")
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string signaturePayload = BuildSignaturePayload(eventType, metricName, value, unit, collectionMethod, privacyClass, windowDurationSeconds, now);
        string signature = Sign(signaturePayload);

        AgentTelemetry entry = new()
        {
            AgentId = _agentId,
            MachineId = _machineId,
            CorrelationId = correlationId,
            TimestampUtc = now,
            EventType = eventType,
            SchemaVersion = RequiredSchemaVersion,
            Kind = "layer1",
            MetricName = metricName,
            Value = value,
            WindowDurationSeconds = windowDurationSeconds,
            Unit = unit,
            CollectionMethod = collectionMethod,
            PrivacyClass = privacyClass,
            ConsentState = _consentState.ToString().ToLowerInvariant(),
            SignatureAlgorithm = "hmac-sha256",
            SignatureKeyId = _signatureKeyId,
            Signature = signature,
            Note = "signed"
        };

        _lastSignalAtUtc = entry.TimestampUtc;
        Channel<AgentTelemetry>? channel = _telemetry;
        if (channel == null)
        {
            return;
        }

        channel.Writer.TryWrite(entry);
        _auditLogWriteLatencyMs = _localLogStore.Log(eventType, new
        {
            correlation_id = correlationId,
            metric_name = metricName,
            value,
            unit,
            collection_method = collectionMethod,
            privacy_class = privacyClass,
            timestamp = now
        });
    }

    private string BuildSignaturePayload(
        string eventType,
        string metricName,
        double value,
        string unit,
        string collectionMethod,
        string privacyClass,
        double windowDurationSeconds,
        DateTimeOffset timestampUtc)
    {
        return string.Join("|",
            _machineId,
            _agentId,
            eventType,
            metricName,
            value.ToString("F6"),
            windowDurationSeconds.ToString("F2"),
            unit,
            collectionMethod,
            privacyClass,
            timestampUtc.ToUnixTimeMilliseconds());
    }

    private string Sign(string payload)
    {
        byte[] data = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(_signatureKey);
        byte[] sig = hmac.ComputeHash(data);
        return Convert.ToHexString(sig);
    }

    private int ReadQueueDepth()
    {
        Channel<AgentTelemetry>? channel = _telemetry;
        if (channel == null)
        {
            return 0;
        }

        return channel.Reader.CanCount ? channel.Reader.Count : 0;
    }

    private void UpdateTray(string status)
    {
        bool canPause = _consentState == Layer1ConsentState.ConsentedActive && Volatile.Read(ref _paused) == 0;
        int? pauseRemainingSeconds = null;
        if (_pauseUntilUtc.HasValue)
        {
            pauseRemainingSeconds = (int)Math.Max(0, (_pauseUntilUtc.Value - DateTimeOffset.UtcNow).TotalSeconds);
        }

        Layer1TraySnapshot snapshot;
        lock (_gate)
        {
            snapshot = new Layer1TraySnapshot(
                Status: status,
                ConsentState: _consentState,
                LastSignalAtUtc: _lastSignalAtUtc,
                Summary: _lastSummary,
                CanPause: canPause,
                PauseRemainingSeconds: pauseRemainingSeconds,
                RiskProbabilities: new Dictionary<string, double>(_riskProbabilities, StringComparer.OrdinalIgnoreCase),
                RiskSigma: _riskSigma,
                RecentPrimitives: _recentPrimitives.ToArray(),
                RecentInterventions: _recentInterventions.ToArray(),
                OverloadActive: _trayOverloadActive,
                DeepWorkActive: _trayDeepWorkActive);
        }

        _tray.Update(snapshot);
    }

    private static double ReadMemoryPercent()
    {
        NativeMethods.MemoryStatusEx memoryStatus = new();
        if (!NativeMethods.GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.TotalPhys == 0)
        {
            return 0;
        }

        ulong used = memoryStatus.TotalPhys - memoryStatus.AvailPhys;
        return (used * 100.0) / memoryStatus.TotalPhys;
    }

    private static double GetIdleDurationSeconds()
    {
        NativeMethods.LastInputInfo info = new()
        {
            CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return 0;
        }

        uint tickCount = unchecked((uint)Environment.TickCount64);
        uint elapsedMs = unchecked(tickCount - info.DwTime);
        return elapsedMs / 1000.0;
    }

    private static string BuildMachineId()
    {
        string seed = $"{Environment.MachineName}|{Environment.UserDomainName}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }

    private void EnsureHooksInstalled()
    {
        if (!_keyboardHook.Install())
        {
            throw new InvalidOperationException("Keyboard hook install failed.");
        }
        if (!_foregroundHook.Install())
        {
            throw new InvalidOperationException("Foreground hook install failed.");
        }
    }

    private void UninstallHooks()
    {
        _keyboardHook.Uninstall();
        _foregroundHook.Uninstall();
    }

    private void HandleFailClosed(string reason, bool revoked = false)
    {
        Interlocked.Exchange(ref _paused, 1);
        _consentState = revoked ? Layer1ConsentState.Revoked : Layer1ConsentState.InstalledNotConsented;
        _pauseUntilUtc = null;
        _consentStore.Save(_consentState);
        UninstallHooks();
        FlushAggregationBuffers();
        EmitEvent("agent.error", "error", 1, "count", reason, "restricted");
        UpdateTray("Error");
    }

    private static async Task SafeAwait(Task? task)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private sealed class SystemCpuSampler
    {
        private ulong _lastIdle;
        private ulong _lastKernel;
        private ulong _lastUser;
        private bool _initialized;

        public double ReadPercent()
        {
            if (!NativeMethods.GetSystemTimes(out NativeMethods.FileTime idle, out NativeMethods.FileTime kernel, out NativeMethods.FileTime user))
            {
                return 0;
            }

            ulong idleNow = ((ulong)idle.DwHighDateTime << 32) | idle.DwLowDateTime;
            ulong kernelNow = ((ulong)kernel.DwHighDateTime << 32) | kernel.DwLowDateTime;
            ulong userNow = ((ulong)user.DwHighDateTime << 32) | user.DwLowDateTime;

            if (!_initialized)
            {
                _initialized = true;
                _lastIdle = idleNow;
                _lastKernel = kernelNow;
                _lastUser = userNow;
                return 0;
            }

            ulong idleDelta = idleNow - _lastIdle;
            ulong kernelDelta = kernelNow - _lastKernel;
            ulong userDelta = userNow - _lastUser;
            ulong total = kernelDelta + userDelta;
            _lastIdle = idleNow;
            _lastKernel = kernelNow;
            _lastUser = userNow;

            if (total == 0)
            {
                return 0;
            }

            return Math.Clamp((1.0 - (idleDelta / (double)total)) * 100.0, 0.0, 100.0);
        }
    }

    private sealed class NetworkSampler
    {
        private long _lastBytes;
        private bool _initialized;

        public double ReadBytesPerSecond(double windowSeconds)
        {
            long total = 0;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    total += stats.BytesReceived;
                    total += stats.BytesSent;
                }
                catch
                {
                }
            }

            if (!_initialized)
            {
                _initialized = true;
                _lastBytes = total;
                return 0;
            }

            long delta = Math.Max(0, total - _lastBytes);
            _lastBytes = total;
            return delta / Math.Max(1.0, windowSeconds);
        }
    }

    private sealed class DiskIoSampler
    {
        private long _lastDeltaAccumulator;
        private bool _initialized;

        public double ReadBytesPerSecond(double windowSeconds)
        {
            long freeBytes = 0;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    freeBytes += (long)drive.AvailableFreeSpace;
                }
                catch
                {
                }
            }

            if (!_initialized)
            {
                _initialized = true;
                _lastDeltaAccumulator = freeBytes;
                return 0;
            }

            long delta = Math.Abs(freeBytes - _lastDeltaAccumulator);
            _lastDeltaAccumulator = freeBytes;
            return delta / Math.Max(1.0, windowSeconds);
        }
    }

    private void AppendPrimitiveForTray(Layer2PrimitiveResult result)
    {
        lock (_gate)
        {
            _recentPrimitives.Enqueue(new Layer1TrayPrimitiveView(
                CorrelationId: result.CorrelationId,
                Name: result.Name,
                Value: result.Value,
                Unit: result.Unit,
                Confidence: result.Confidence,
                TimestampUtc: result.ComputedAtUtc));
            while (_recentPrimitives.Count > 5)
            {
                _recentPrimitives.Dequeue();
            }
        }
    }

    private void UpdateRiskForTray(Layer4StateResult stabilized, Layer3StateResult layer3)
    {
        try
        {
            Dictionary<string, double>? distribution = JsonSerializer.Deserialize<Dictionary<string, double>>(stabilized.DistributionJson);
            if (distribution == null)
            {
                return;
            }

            lock (_gate)
            {
                _riskProbabilities["stress"] = ReadDistribution(distribution, "critical");
                _riskProbabilities["overload"] = ReadDistribution(distribution, "degrading");
                _riskProbabilities["focus"] = ReadDistribution(distribution, "stable");
                _riskProbabilities["fatigue"] = ReadDistribution(distribution, "elevated_risk");
                _riskSigma = Math.Clamp(1.0 - stabilized.Confidence, 0, 1);
                (_trayOverloadActive, _trayDeepWorkActive) = ResolveTrayVisualFlags(stabilized, _riskProbabilities);
            }

            AppendInterventionForTray(stabilized, layer3);
            UpdateTray(IsCollectionDisabled() ? "Paused" : "Active");
        }
        catch
        {
        }
    }

    private void AppendInterventionForTray(Layer4StateResult stabilized, Layer3StateResult layer3)
    {
        string actionType = stabilized.CommittedState switch
        {
            "critical" => "break_suggestion",
            "degrading" => "focus_mode",
            "elevated_risk" => "notifications_suppressed",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(actionType))
        {
            _lastCommittedState = stabilized.CommittedState;
            return;
        }

        if (string.Equals(_lastCommittedState, stabilized.CommittedState, StringComparison.Ordinal))
        {
            return;
        }

        _lastCommittedState = stabilized.CommittedState;
        string reason = $"State={stabilized.CommittedState}, probability={stabilized.Probability:F2}, risk={layer3.GlobalRiskScore:F2}";
        Layer1TrayInterventionView view = new(
            CorrelationId: string.IsNullOrWhiteSpace(stabilized.CorrelationId) ? CreateCorrelationId() : stabilized.CorrelationId,
            ActionType: actionType,
            StartTimeUtc: stabilized.EvaluatedAtUtc,
            EndTimeUtc: stabilized.EvaluatedAtUtc.AddMinutes(5),
            Confidence: stabilized.Confidence,
            PolicyId: "local-state-policy-v1",
            PolicyVersion: _expectedPolicyVersion,
            SignatureVerified: true,
            Reason: reason);
        lock (_gate)
        {
            _recentInterventions.Enqueue(view);
            while (_recentInterventions.Count > 5)
            {
                _recentInterventions.Dequeue();
            }
        }
        Interlocked.Increment(ref _interventionsAppliedTotal);
    }

    private static (bool OverloadActive, bool DeepWorkActive) ResolveTrayVisualFlags(
        Layer4StateResult stabilized,
        IReadOnlyDictionary<string, double> riskProbabilities)
    {
        // Priority-aligned state derivation for tray visuals from pipeline outputs:
        // overload (protective) always wins over deep work.
        double overloadRisk = ReadDistribution(riskProbabilities, "overload");
        double focusRisk = ReadDistribution(riskProbabilities, "focus");
        double fatigueRisk = ReadDistribution(riskProbabilities, "fatigue");

        bool overloadActive = string.Equals(stabilized.CommittedState, "critical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stabilized.CommittedState, "degrading", StringComparison.OrdinalIgnoreCase)
            || overloadRisk >= 0.50d;

        bool deepWorkActive = !overloadActive
            && string.Equals(stabilized.CommittedState, "stable", StringComparison.OrdinalIgnoreCase)
            && focusRisk >= 0.65d
            && overloadRisk <= 0.45d
            && fatigueRisk <= 0.50d;

        return (overloadActive, deepWorkActive);
    }

    private void TryAutoResumePaused()
    {
        if (_consentState != Layer1ConsentState.PausedByUser || !_pauseUntilUtc.HasValue)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _pauseUntilUtc.Value)
        {
            UpdateTray("Paused");
            return;
        }

        ResumeByUser();
    }

    private void EmitObservabilityMetricsIfDue(DateTimeOffset nowUtc)
    {
        if ((nowUtc - _lastMetricsEmitUtc) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        string correlationId = CreateCorrelationId();
        long eventsTotal = Interlocked.Read(ref _agentEventsTotal);
        long interventionsApplied = Interlocked.Read(ref _interventionsAppliedTotal);
        long interventionsFailed = Interlocked.Read(ref _interventionsFailedTotal);
        long complete = Interlocked.Read(ref _correlationTraceCompleteTotal);
        long incomplete = Interlocked.Read(ref _correlationTraceIncompleteTotal);
        long eventsPerMinute = Math.Max(0, eventsTotal - _agentEventsSnapshot);
        long appliedPerMinute = Math.Max(0, interventionsApplied - _interventionsAppliedSnapshot);
        long failedPerMinute = Math.Max(0, interventionsFailed - _interventionsFailedSnapshot);
        _agentEventsSnapshot = eventsTotal;
        _interventionsAppliedSnapshot = interventionsApplied;
        _interventionsFailedSnapshot = interventionsFailed;
        bool traceComplete = incomplete == 0 && complete > 0;

        EmitEvent("agent.metric", "agent_events_total", eventsPerMinute, "count_per_min", "counter_delta", "restricted", 60, correlationId);
        EmitEvent("agent.metric", "interventions_applied_total", appliedPerMinute, "count_per_min", "counter_delta", "restricted", 60, correlationId);
        EmitEvent("agent.metric", "interventions_failed_total", failedPerMinute, "count_per_min", "counter_delta", "restricted", 60, correlationId);
        EmitEvent("agent.metric", "state_inference_latency_ms", _primitiveToInferenceLatencyMs, "milliseconds", "latency_probe", "restricted", 60, correlationId);
        EmitEvent("agent.metric", "policy_evaluation_latency_ms", _policyEvaluationLatencyMs, "milliseconds", "latency_probe", "restricted", 60, correlationId);
        EmitEvent("agent.metric", "audit_log_write_latency_ms", _auditLogWriteLatencyMs, "milliseconds", "latency_probe", "restricted", 60, correlationId);
        EmitEvent("agent.metric", "correlation_trace_complete", traceComplete ? 1 : 0, "bool_1_0", "trace_health", "restricted", 60, correlationId);
        _lastMetricsEmitUtc = nowUtc;
    }

    private static double ReadDistribution(IReadOnlyDictionary<string, double> distribution, string key)
    {
        return distribution.TryGetValue(key, out double value) ? Math.Clamp(value, 0, 1) : 0;
    }

    private static string CreateCorrelationId()
    {
        Span<byte> bytes = stackalloc byte[16];
        ulong unixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;
        RandomNumberGenerator.Fill(bytes[6..]);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes).ToString();
    }

    private void OnInterventionReceived(Layer1TrayInterventionView intervention)
    {
        if (string.IsNullOrWhiteSpace(intervention.CorrelationId))
        {
            intervention = intervention with { CorrelationId = CreateCorrelationId() };
        }

        if (!intervention.SignatureVerified)
        {
            Interlocked.Increment(ref _interventionsFailedTotal);
            EmitEvent("agent.error", "intervention", 1, "count", "signature_verification_failed", "restricted", correlationId: intervention.CorrelationId);
            return;
        }

        if (!string.Equals(intervention.PolicyVersion, _expectedPolicyVersion, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _interventionsFailedTotal);
            EmitEvent("agent.error", "intervention", 1, "count", "policy_version_mismatch", "restricted", correlationId: intervention.CorrelationId);
            return;
        }

        lock (_gate)
        {
            _recentInterventions.Enqueue(intervention);
            while (_recentInterventions.Count > 5)
            {
                _recentInterventions.Dequeue();
            }
        }
        Interlocked.Increment(ref _interventionsAppliedTotal);

        _auditLogWriteLatencyMs = _localLogStore.Log("intervention.received", new
        {
            correlation_id = intervention.CorrelationId,
            action_type = intervention.ActionType,
            start_time = intervention.StartTimeUtc,
            end_time = intervention.EndTimeUtc,
            confidence = intervention.Confidence,
            policy_id = intervention.PolicyId,
            policy_version = intervention.PolicyVersion,
            signature_verified = intervention.SignatureVerified,
            reason = intervention.Reason
        });
        UpdateTray(IsCollectionDisabled() ? "Paused" : "Active");
    }
}
