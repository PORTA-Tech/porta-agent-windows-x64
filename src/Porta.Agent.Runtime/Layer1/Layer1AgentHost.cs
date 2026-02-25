using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private readonly Layer1ConsentStore _consentStore = new(Environment.GetEnvironmentVariable("PORTA_CONSENT_STATE_PATH"));
    private readonly Layer1TrayController _tray = new();
    private readonly SystemCpuSampler _systemCpu = new();
    private readonly NetworkSampler _networkSampler = new();
    private readonly DiskIoSampler _diskIoSampler = new();
    private readonly Layer2NormalizationEngine _normalizationEngine;
    private readonly Layer3InferenceEngine _inferenceEngine;
    private readonly Layer4StabilityEngine _stabilityEngine;
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
    private int _running;
    private int _errorCount;
    private int _paused;
    private Layer1ConsentState _consentState;
    private long _keystrokesInWindow;
    private long _appSwitchesInWindow;
    private double _mouseDistanceInWindow;
    private NativeMethods.Point _lastCursorPos;
    private bool _hasLastCursorPos;
    private DateTimeOffset? _lastSignalAtUtc;
    private string _lastSummary = "No signals emitted yet.";
    private readonly bool _consentStateCorrupted;

    public Layer1AgentHost()
    {
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
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (!Environment.UserInteractive)
            {
                throw new InvalidOperationException("Tray visibility is required; non-interactive session is blocked.");
            }

            if (!_tray.Start())
            {
                throw new InvalidOperationException("Tray startup failed.");
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
        await _client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _tray.Dispose();
        _cts?.Dispose();
        _cts = null;
        _collectorTask = null;
        _normalizationTask = null;
        _inferenceTask = null;
        _telemetryTask = null;
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

        DialogResult result = MessageBox.Show(
            $"{Layer1Disclosure.Text}\r\n\r\nSelect Yes to consent and activate Layer 1.",
            "PORTA Consent",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            _consentState = Layer1ConsentState.ConsentedActive;
            _consentStore.Save(_consentState);
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

                long keyCount = Interlocked.Exchange(ref _keystrokesInWindow, 0);
                long appSwitches = Interlocked.Exchange(ref _appSwitchesInWindow, 0);
                double mouseDistance;
                lock (_gate)
                {
                    mouseDistance = _mouseDistanceInWindow;
                    _mouseDistanceInWindow = 0;
                }

                PublishSignal("interaction.keystroke_rate", keyCount / elapsedWindowSeconds, elapsedWindowSeconds, "count_per_second", "keyboard_hook_aggregate");
                PublishSignal("interaction.mouse_movement_distance", mouseDistance, elapsedWindowSeconds, "pixels", "cursor_position_delta_aggregate");
                PublishSignal("interaction.app_switch_frequency", appSwitches / elapsedWindowSeconds, elapsedWindowSeconds, "count_per_second", "foreground_event_count");
                PublishSignal("interaction.idle_duration", GetIdleDurationSeconds(), elapsedWindowSeconds, "seconds", "last_input_info");
                PublishSignal("system.cpu_utilization", _systemCpu.ReadPercent(), elapsedWindowSeconds, "percent", "get_system_times");
                PublishSignal("system.memory_utilization", ReadMemoryPercent(), elapsedWindowSeconds, "percent", "global_memory_status");
                PublishSignal("system.disk_io_rate", _diskIoSampler.ReadBytesPerSecond(elapsedWindowSeconds), elapsedWindowSeconds, "bytes_per_second", "drive_free_space_delta");
                PublishSignal("system.network_throughput", _networkSampler.ReadBytesPerSecond(elapsedWindowSeconds), elapsedWindowSeconds, "bytes_per_second", "network_interface_stats");
                PublishSignal("system.process_count", Process.GetProcesses().Length, elapsedWindowSeconds, "count", "process_snapshot");
                PublishSignal("system.service_health_status", 1, elapsedWindowSeconds, "up_down", "host_lifecycle");

                TimeSpan processCpuEnd = Process.GetCurrentProcess().TotalProcessorTime;
                DateTimeOffset cpuWindowEnd = DateTimeOffset.UtcNow;
                double cpuElapsedSeconds = Math.Max(0.1, (cpuWindowEnd - cpuWindowStart).TotalSeconds);
                double agentCpuPercent = ((processCpuEnd - processCpuStart).TotalSeconds / (cpuElapsedSeconds * Environment.ProcessorCount)) * 100.0;
                processCpuStart = processCpuEnd;
                cpuWindowStart = cpuWindowEnd;

                Process proc = Process.GetCurrentProcess();
                PublishSignal("agent.cpu_usage", Math.Clamp(agentCpuPercent, 0, 100), elapsedWindowSeconds, "percent", "process_cpu_delta");
                PublishSignal("agent.memory_usage", proc.WorkingSet64 / (1024.0 * 1024.0), elapsedWindowSeconds, "megabytes", "working_set");
                PublishSignal("agent.event_queue_depth", ReadQueueDepth(), elapsedWindowSeconds, "count", "telemetry_queue_depth");
                PublishSignal("agent.error_count", Volatile.Read(ref _errorCount), elapsedWindowSeconds, "count", "runtime_counter");

                _lastSummary =
                    $"Window: {WindowSeconds}s\r\n"
                    + $"Keystroke rate: {(keyCount / elapsedWindowSeconds):F2}/s\r\n"
                    + $"Mouse distance: {mouseDistance:F0} px\r\n"
                    + $"App switches: {(appSwitches / elapsedWindowSeconds):F2}/s\r\n"
                    + $"Idle duration: {GetIdleDurationSeconds():F1}s";
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

            Layer3StateResult result = _inferenceEngine.Evaluate(DateTimeOffset.UtcNow);
            if (!result.Success)
            {
                EmitEvent("agent.error", "error", 1, "count", result.ErrorReason, "restricted");
                continue;
            }

            Layer4StateResult stabilized = _stabilityEngine.Stabilize(result);
            EmitState(stabilized, result);
        }
    }

    private void OnKeyChanged(int _, bool isKeyDown, bool isInjected)
    {
        if (!isKeyDown || isInjected || IsCollectionDisabled())
        {
            return;
        }

        Interlocked.Increment(ref _keystrokesInWindow);
    }

    private void PauseByUser()
    {
        Interlocked.Exchange(ref _paused, 1);
        _consentState = Layer1ConsentState.PausedByUser;
        _consentStore.Save(_consentState);
        UninstallHooks();
        FlushAggregationBuffers();
        EmitEvent("agent.paused", "lifecycle", 1, "count", "user_pause", "public");
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

    private void FlushAggregationBuffers()
    {
        Interlocked.Exchange(ref _keystrokesInWindow, 0);
        Interlocked.Exchange(ref _appSwitchesInWindow, 0);
        lock (_gate)
        {
            _mouseDistanceInWindow = 0;
        }
    }

    private void PublishSignal(string metricName, double value, double windowDurationSeconds, string unit, string collectionMethod)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            EmitEvent("agent.error", "error", 1, "count", "numeric_instability_input_nan_or_inf", "restricted");
            return;
        }

        var sample = new Layer2SignalSample(
            MachineId: _machineId,
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
            Note = "signed"
        };

        _lastSignalAtUtc = entry.TimestampUtc;
        _telemetry?.Writer.TryWrite(entry);
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
            ModelVersion = "deterministic-v1.0.0|stability-v1.0.0",
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
            Note = "signed"
        };

        _telemetry?.Writer.TryWrite(entry);
    }

    private void EmitEvent(
        string eventType,
        string metricName,
        double value,
        string unit,
        string collectionMethod,
        string privacyClass,
        double windowDurationSeconds = WindowSeconds)
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
        _tray.Update(status, _consentState, _lastSignalAtUtc, _lastSummary, canPause);
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
}
