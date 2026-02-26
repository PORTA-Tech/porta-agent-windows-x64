using System.Reflection;
using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Core;

public sealed class AgentCoreController : IAgentCoreController
{
    private const string ConsentFileName = "consent-state-v2.json";
    private const string ServiceLogFileName = "service-core.log";
    private const string DefaultSchemaVersion = "1.0.0";
    private const string DefaultPolicyVersion = "1.0.0";

    private readonly object _gate = new();
    private readonly IBrainStateStore _stateStore;
    private readonly string _consentPath = BuildConsentPath();
    private readonly string _logPath = BuildLogPath();
    private bool _paused;
    private bool _consentActive;
    private DateTimeOffset? _pauseUntilUtc;
    private DateTimeOffset? _lastEventTimestampUtc;
    private string? _lastError;
    private bool _backendConnected;
    private DateTimeOffset _lastDiagnosticsWriteUtc = DateTimeOffset.MinValue;
    private AgentBrainSnapshot _latestSnapshot = new(
        TimestampUtc: DateTimeOffset.UtcNow,
        TypingBurstIntensity: 0,
        ScrollVelocityIndex: 0,
        FocusSwitchRate: 0,
        FocusStrength: 0,
        OverloadLikelihood: 0,
        FatigueProbability: 0,
        CognitiveStability: 0,
        Confidence: 0,
        LastAction: "starting",
        Contributors: ["startup"]);
    private readonly string _agentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

    public AgentCoreController()
        : this(new SqliteBrainStateStore())
    {
    }

    public AgentCoreController(IBrainStateStore stateStore)
    {
        _stateStore = stateStore;
        _consentActive = LoadConsentState();
        _latestSnapshot = _stateStore.ReadLatestSnapshot() ?? _latestSnapshot;
    }

    public AgentServiceStatusSnapshot GetStatus()
    {
        lock (_gate)
        {
            TryAutoResumeLocked();
            AgentStatus status = _lastError is null
                ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
                : AgentStatus.Degraded;

            return new AgentServiceStatusSnapshot(
                Status: status,
                ServiceRunning: true,
                Paused: _paused,
                ConsentActive: _consentActive,
                BackendConnected: _backendConnected,
                FocusStrength: _latestSnapshot.FocusStrength,
                OverloadLikelihood: _latestSnapshot.OverloadLikelihood,
                LastAction: _latestSnapshot.LastAction,
                AgentVersion: _agentVersion,
                SchemaVersion: DefaultSchemaVersion,
                PolicyVersion: DefaultPolicyVersion,
                LastEventTimestampUtc: _lastEventTimestampUtc,
                LastError: _lastError,
                LastUpdatedUtc: DateTimeOffset.UtcNow);
        }
    }

    public AgentControlState GetControlState()
    {
        lock (_gate)
        {
            TryAutoResumeLocked();
            return new AgentControlState(_paused, _consentActive, _pauseUntilUtc);
        }
    }

    public void Pause(string? reason = null, DateTimeOffset? untilUtc = null)
    {
        lock (_gate)
        {
            _paused = true;
            _pauseUntilUtc = untilUtc;
            _lastEventTimestampUtc = DateTimeOffset.UtcNow;
            _latestSnapshot = _latestSnapshot with { LastAction = "pause" };
            _lastError = null;
            WriteLogLocked($"pause reason={reason ?? "unspecified"} until={untilUtc?.ToString("O") ?? "none"}");
            WriteControlEventLocked(
                action: "pause",
                reason: reason ?? "unspecified",
                triggerState: "control",
                triggerValue: 1,
                confidence: 1,
                escalationLevel: "Inform",
                reversible: true);
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _pauseUntilUtc = null;
            _lastEventTimestampUtc = DateTimeOffset.UtcNow;
            _latestSnapshot = _latestSnapshot with { LastAction = "resume" };
            WriteLogLocked("resume");
            WriteControlEventLocked(
                action: "resume",
                reason: "manual_resume",
                triggerState: "control",
                triggerValue: 1,
                confidence: 1,
                escalationLevel: "Inform",
                reversible: true);
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void GrantConsent()
    {
        lock (_gate)
        {
            _consentActive = true;
            _paused = false;
            _pauseUntilUtc = null;
            _lastError = null;
            SaveConsentState(_consentActive);
            _lastEventTimestampUtc = DateTimeOffset.UtcNow;
            _latestSnapshot = _latestSnapshot with { LastAction = "consent_granted" };
            WriteLogLocked("consent_granted");
            WriteControlEventLocked(
                action: "consent_granted",
                reason: "user_consent_granted",
                triggerState: "consent",
                triggerValue: 1,
                confidence: 1,
                escalationLevel: "Inform",
                reversible: true);
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void RevokeConsent()
    {
        lock (_gate)
        {
            _consentActive = false;
            _paused = true;
            _pauseUntilUtc = null;
            SaveConsentState(_consentActive);
            _lastEventTimestampUtc = DateTimeOffset.UtcNow;
            _latestSnapshot = _latestSnapshot with { LastAction = "consent_revoked" };
            WriteLogLocked("consent_revoked");
            WriteControlEventLocked(
                action: "consent_revoked",
                reason: "user_consent_revoked",
                triggerState: "consent",
                triggerValue: 0,
                confidence: 1,
                escalationLevel: "Inform",
                reversible: true);
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void MarkError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
            _lastEventTimestampUtc = DateTimeOffset.UtcNow;
            WriteLogLocked($"error {message}");
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void MarkHealthyHeartbeat()
    {
        lock (_gate)
        {
            TryAutoResumeLocked();
            _lastError = null;
            if (_consentActive)
            {
                _lastEventTimestampUtc = DateTimeOffset.UtcNow;
            }

            WriteDiagnosticsLocked(force: false);
        }
    }

    public void ApplyBrainSnapshot(AgentBrainSnapshot snapshot, bool backendConnected)
    {
        lock (_gate)
        {
            _latestSnapshot = snapshot;
            _backendConnected = backendConnected;
            _lastEventTimestampUtc = snapshot.TimestampUtc;
            WriteDiagnosticsLocked(force: false);
        }
    }

    public string ExportBundle()
    {
        lock (_gate)
        {
            WriteLogLocked("export_bundle");
            return _stateStore.ExportBundle(_logPath, _consentPath);
        }
    }

    private void TryAutoResumeLocked()
    {
        if (_paused && _pauseUntilUtc.HasValue && DateTimeOffset.UtcNow >= _pauseUntilUtc.Value)
        {
            _paused = false;
            _pauseUntilUtc = null;
            _latestSnapshot = _latestSnapshot with { LastAction = "auto_resume" };
            WriteLogLocked("auto_resume");
            WriteControlEventLocked(
                action: "auto_resume",
                reason: "pause_timer_elapsed",
                triggerState: "control",
                triggerValue: 1,
                confidence: 1,
                escalationLevel: "Inform",
                reversible: true);
        }
    }

    private void WriteControlEventLocked(
        string action,
        string reason,
        string triggerState,
        double triggerValue,
        double confidence,
        string escalationLevel,
        bool reversible)
    {
        _stateStore.WriteControlEvent(new AgentControlEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            Action: action,
            TriggerState: triggerState,
            TriggerValue: triggerValue,
            Confidence: confidence,
            DurationSeconds: null,
            EscalationLevel: escalationLevel,
            Reversible: reversible,
            Reason: reason,
            Contributors: ["controller"]));
    }

    private void WriteDiagnosticsLocked(bool force)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!force && (nowUtc - _lastDiagnosticsWriteUtc) < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _stateStore.WriteDiagnostics(new AgentDiagnosticsSnapshot(
            TimestampUtc: nowUtc,
            BackendConnected: _backendConnected,
            ConsentActive: _consentActive,
            Paused: _paused,
            LastError: _lastError,
            PolicyVersion: DefaultPolicyVersion,
            SchemaVersion: DefaultSchemaVersion,
            AgentVersion: _agentVersion,
            LastEventTimestampUtc: _lastEventTimestampUtc));

        _lastDiagnosticsWriteUtc = nowUtc;
    }

    private bool LoadConsentState()
    {
        try
        {
            if (!File.Exists(_consentPath))
            {
                return false;
            }

            string raw = File.ReadAllText(_consentPath).Trim();
            return bool.TryParse(raw, out bool active) && active;
        }
        catch
        {
            return false;
        }
    }

    private void SaveConsentState(bool active)
    {
        string? directory = Path.GetDirectoryName(_consentPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_consentPath, active ? "true" : "false");
    }

    private void WriteLogLocked(string message)
    {
        string? directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string line = $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}";
        File.AppendAllText(_logPath, line);
    }

    private static string BuildConsentPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", ConsentFileName);
    }

    private static string BuildLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", ServiceLogFileName);
    }
}
