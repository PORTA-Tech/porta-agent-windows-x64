using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Core;

public sealed class AgentCoreController : IAgentCoreController
{
    private const string ConsentFileName = "consent-state-v2.json";
    private const string SettingsFileName = "effective-settings-v1.json";
    private const string ConsentVersion = "1.0.0";
    private static readonly TimeSpan BaselineRecalibrationDuration = TimeSpan.FromMinutes(15);
    private const string ConsentDisclosureText =
        "PORTA collects only aggregated interaction metrics: keystroke timing intervals, mouse acceleration, scroll velocity, focus switch frequency, idle fragmentation, and notification reaction latency. "
        + "PORTA never collects typed text, clipboard, screenshots, files, messages, or network content. "
        + "Local retention window is 90 days by default. "
        + "User controls include pause, revoke consent, export data, and delete local data.";
    private const string ServiceLogFileName = "service-core.log";
    private const string DefaultSchemaVersion = "1.0.0";
    private const string DefaultPolicyVersion = "1.0.0";
    private static readonly string ConsentDisclosureHash = ComputeSha256Hex(ConsentDisclosureText);

    private readonly object _gate = new();
    private readonly IBrainStateStore _stateStore;
    private readonly string _consentPath = BuildConsentPath();
    private readonly string _settingsPath = BuildSettingsPath();
    private readonly string _logPath = BuildLogPath();
    private bool _paused;
    private bool _consentActive;
    private DateTimeOffset? _pauseUntilUtc;
    private DateTimeOffset? _lastEventTimestampUtc;
    private string? _lastError;
    private bool _backendConnected;
    private DateTimeOffset? _lastBackendOkUtc;
    private DateTimeOffset _lastDiagnosticsWriteUtc = DateTimeOffset.MinValue;
    private AgentBrainSnapshot _latestSnapshot = new(
        TimestampUtc: DateTimeOffset.UtcNow,
        TypingBurstIntensity: 0,
        ScrollVelocityIndex: 0,
        FocusSwitchRate: 0,
        SmoothedTypingBurstIntensity: 0,
        SmoothedScrollVelocityIndex: 0,
        SmoothedFocusSwitchRate: 0,
        FocusStrength: 0,
        OverloadLikelihood: 0,
        FatigueProbability: 0,
        CognitiveStability: 0,
        DominantStateValue: 0,
        DominantStateName: "focus",
        Confidence: 0,
        LastAction: "starting",
        ActivationWindowTriggered: false,
        CooldownTriggered: false,
        ActivationWindowOpen: false,
        Contributors: ["startup"]);
    private readonly string _agentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
    private ConsentStateRecord _consentState = CreateDefaultConsentState();
    private SettingsStateRecord _settingsState = CreateDefaultSettingsState();

    public AgentCoreController()
        : this(new SqliteBrainStateStore())
    {
    }

    public AgentCoreController(IBrainStateStore stateStore)
    {
        _stateStore = stateStore;
        AgentConsentRecord? consentFromStore = _stateStore.ReadLatestConsent();
        _consentState = consentFromStore != null
            ? new ConsentStateRecord(
                Active: consentFromStore.Active,
                Version: consentFromStore.Version,
                TimestampUtc: consentFromStore.TimestampUtc,
                DisclosureHash: consentFromStore.DisclosureHash,
                UpdatedAtUtc: consentFromStore.TimestampUtc)
            : LoadConsentState();
        _consentActive = _consentState.Active;
        AgentSettingsRecord? settingsFromStore = _stateStore.ReadLatestSettings();
        _settingsState = settingsFromStore != null
            ? new SettingsStateRecord(
                RegulationIntensity: NormalizeIntensity(settingsFromStore.RegulationIntensity, "Balanced"),
                PrivacyLevel: NormalizePrivacy(settingsFromStore.PrivacyLevel, "Standard"),
                NotificationReductionEnabled: settingsFromStore.NotificationReductionEnabled,
                FocusModeEnabled: settingsFromStore.FocusModeEnabled,
                AnimationReductionEnabled: settingsFromStore.AnimationReductionEnabled,
                TemporaryAppRestrictionEnabled: settingsFromStore.TemporaryAppRestrictionEnabled,
                BaselineRecalibrationStartedUtc: settingsFromStore.BaselineRecalibrationStartedUtc,
                BaselineRecalibrationUntilUtc: settingsFromStore.BaselineRecalibrationUntilUtc,
                UpdatedAtUtc: settingsFromStore.TimestampUtc)
            : LoadSettingsState();

        if (consentFromStore == null)
        {
            WriteConsentStateToStoreLocked(_consentState, DateTimeOffset.UtcNow);
        }

        if (settingsFromStore == null)
        {
            WriteSettingsStateToStoreLocked(_settingsState, DateTimeOffset.UtcNow);
        }

        _latestSnapshot = _stateStore.ReadLatestSnapshot() ?? _latestSnapshot;
    }

    public AgentServiceStatusSnapshot GetStatus()
    {
        lock (_gate)
        {
            TryAutoResumeLocked();
            UpdateRecalibrationStateLocked();
            AgentStatus status = _lastError is null
                ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
                : AgentStatus.Degraded;

            return new AgentServiceStatusSnapshot(
                Status: status,
                ServiceRunning: true,
                Paused: _paused,
                ConsentActive: _consentActive,
                ConsentVersion: _consentState.Version,
                ConsentTimestampUtc: _consentState.TimestampUtc,
                ConsentDisclosureHash: _consentState.DisclosureHash,
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

    public AgentBrainSnapshot GetCurrentSnapshot()
    {
        lock (_gate)
        {
            return _latestSnapshot;
        }
    }

    public AgentInputSummary GetInputsSummary(int windowSeconds = 60)
    {
        int window = Math.Clamp(windowSeconds, 5, 3600);
        DateTimeOffset toUtc = DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = toUtc.AddSeconds(-window);
        IReadOnlyList<AgentBrainSnapshot> rows = _stateStore.ReadTimeline(fromUtc, toUtc, bucketSeconds: 1);
        int sampleCount = rows.Count;
        if (sampleCount == 0)
        {
            return new AgentInputSummary(
                FromUtc: fromUtc,
                ToUtc: toUtc,
                WindowSeconds: window,
                SampleCount: 0,
                TypingBurstIntensity: 0,
                ScrollVelocityIndex: 0,
                FocusSwitchRate: 0,
                IdleFragmentationIndex: 0,
                NotificationLatencyIndex: 0);
        }

        double typing = rows.Average(row => row.TypingBurstIntensity);
        double scroll = rows.Average(row => row.ScrollVelocityIndex);
        double focusSwitch = rows.Average(row => row.FocusSwitchRate);

        return new AgentInputSummary(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            WindowSeconds: window,
            SampleCount: sampleCount,
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: scroll,
            FocusSwitchRate: focusSwitch,
            IdleFragmentationIndex: focusSwitch,
            NotificationLatencyIndex: 0);
    }

    public IReadOnlyList<AgentControlEvent> GetRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null)
    {
        int boundedLimit = Math.Clamp(limit, 1, 500);
        return _stateStore.ReadRecentEvents(boundedLimit, sinceUtc);
    }

    public AgentTimelineData GetTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = 5)
    {
        int bucket = Math.Clamp(bucketSeconds, 1, 60);
        DateTimeOffset from = fromUtc;
        DateTimeOffset to = toUtc;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        IReadOnlyList<AgentBrainSnapshot> snapshots = _stateStore.ReadTimeline(from, to, bucket);
        IReadOnlyList<AgentControlEvent> interventions = _stateStore.ReadEvents(from, to);
        return new AgentTimelineData(
            FromUtc: from,
            ToUtc: to,
            BucketSeconds: bucket,
            Snapshots: snapshots,
            Interventions: interventions);
    }

    public AgentSettingsState GetEffectiveSettings()
    {
        lock (_gate)
        {
            UpdateRecalibrationStateLocked();
            return ToPublicSettingsState(_settingsState);
        }
    }

    public AgentDataRetentionInfo GetDataRetention()
    {
        lock (_gate)
        {
            AgentStorageInfo storeInfo = _stateStore.ReadStorageInfo();
            long logBytes = TryGetFileSize(_logPath);
            long consentBytes = TryGetFileSize(_consentPath);
            long totalBytes = storeInfo.DatabaseBytes + logBytes + consentBytes;
            return new AgentDataRetentionInfo(
                RetentionDays: storeInfo.RetentionDays,
                DatabaseBytes: storeInfo.DatabaseBytes,
                LogBytes: logBytes,
                ConsentBytes: consentBytes,
                TotalBytes: totalBytes,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
        }
    }

    public void Pause(string? reason = null, DateTimeOffset? untilUtc = null)
    {
        lock (_gate)
        {
            bool previousPaused = _paused;
            DateTimeOffset? previousUntil = _pauseUntilUtc;
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
            WriteSystemChangeLocked(
                changeType: "pause_state",
                previousValue: new { paused = previousPaused, pauseUntilUtc = previousUntil },
                newValue: new { paused = _paused, pauseUntilUtc = _pauseUntilUtc },
                undoInfo: "call /v1/control/resume",
                reason: reason ?? "unspecified");
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            bool previousPaused = _paused;
            DateTimeOffset? previousUntil = _pauseUntilUtc;
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
            WriteSystemChangeLocked(
                changeType: "pause_state",
                previousValue: new { paused = previousPaused, pauseUntilUtc = previousUntil },
                newValue: new { paused = _paused, pauseUntilUtc = _pauseUntilUtc },
                undoInfo: "call /v1/control/pause",
                reason: "manual_resume");
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void GrantConsent()
    {
        lock (_gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            bool previousConsent = _consentActive;
            _consentActive = true;
            _consentState = CreateConsentState(active: true, timestampUtc: nowUtc);
            _paused = false;
            _pauseUntilUtc = null;
            _lastError = null;
            SaveConsentState(_consentState);
            WriteConsentStateToStoreLocked(_consentState, nowUtc);
            _lastEventTimestampUtc = nowUtc;
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
            WriteSystemChangeLocked(
                changeType: "consent",
                previousValue: new { active = previousConsent },
                newValue: new { active = _consentActive, version = _consentState.Version, timestampUtc = _consentState.TimestampUtc },
                undoInfo: "call /v1/consent/revoke",
                reason: "user_consent_granted");
            WriteDiagnosticsLocked(force: true);
        }
    }

    public void RevokeConsent()
    {
        lock (_gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            bool previousConsent = _consentActive;
            _consentActive = false;
            _consentState = CreateConsentState(active: false, timestampUtc: nowUtc);
            _paused = true;
            _pauseUntilUtc = null;
            SaveConsentState(_consentState);
            WriteConsentStateToStoreLocked(_consentState, nowUtc);
            _lastEventTimestampUtc = nowUtc;
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
            WriteSystemChangeLocked(
                changeType: "consent",
                previousValue: new { active = previousConsent },
                newValue: new { active = _consentActive, version = _consentState.Version, timestampUtc = _consentState.TimestampUtc },
                undoInfo: "call /v1/consent/grant",
                reason: "user_consent_revoked");
            WriteDiagnosticsLocked(force: true);
        }
    }

    public AgentSettingsState UpdateSettings(AgentSettingsUpdate update)
    {
        lock (_gate)
        {
            UpdateRecalibrationStateLocked();
            SettingsStateRecord previous = _settingsState;

            string intensity = NormalizeIntensity(update.RegulationIntensity, _settingsState.RegulationIntensity);
            string privacy = NormalizePrivacy(update.PrivacyLevel, _settingsState.PrivacyLevel);

            _settingsState = _settingsState with
            {
                RegulationIntensity = intensity,
                PrivacyLevel = privacy,
                NotificationReductionEnabled = update.NotificationReductionEnabled ?? _settingsState.NotificationReductionEnabled,
                FocusModeEnabled = update.FocusModeEnabled ?? _settingsState.FocusModeEnabled,
                AnimationReductionEnabled = update.AnimationReductionEnabled ?? _settingsState.AnimationReductionEnabled,
                TemporaryAppRestrictionEnabled = update.TemporaryAppRestrictionEnabled ?? _settingsState.TemporaryAppRestrictionEnabled,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSettingsState(_settingsState);
            WriteSettingsStateToStoreLocked(_settingsState, _settingsState.UpdatedAtUtc);
            _lastEventTimestampUtc = DateTimeOffset.UtcNow;
            _latestSnapshot = _latestSnapshot with { LastAction = "settings_updated" };
            WriteLogLocked("settings_updated");
            WriteControlEventLocked(
                action: "settings_updated",
                reason: "user_settings_update",
                triggerState: "control",
                triggerValue: 1,
                confidence: 1,
                escalationLevel: "Inform",
                reversible: true);
            WriteSystemChangeLocked(
                changeType: "settings",
                previousValue: new
                {
                    previous.RegulationIntensity,
                    previous.PrivacyLevel,
                    previous.NotificationReductionEnabled,
                    previous.FocusModeEnabled,
                    previous.AnimationReductionEnabled,
                    previous.TemporaryAppRestrictionEnabled
                },
                newValue: new
                {
                    _settingsState.RegulationIntensity,
                    _settingsState.PrivacyLevel,
                    _settingsState.NotificationReductionEnabled,
                    _settingsState.FocusModeEnabled,
                    _settingsState.AnimationReductionEnabled,
                    _settingsState.TemporaryAppRestrictionEnabled
                },
                undoInfo: "call /v1/settings/update with previous values",
                reason: "user_settings_update");
            WriteDiagnosticsLocked(force: true);
            return ToPublicSettingsState(_settingsState);
        }
    }

    public AgentRecalibrationResult RecalibrateBaseline()
    {
        lock (_gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset untilUtc = nowUtc.Add(BaselineRecalibrationDuration);
            DateTimeOffset? previousStart = _settingsState.BaselineRecalibrationStartedUtc;
            DateTimeOffset? previousUntil = _settingsState.BaselineRecalibrationUntilUtc;
            _settingsState = _settingsState with
            {
                BaselineRecalibrationStartedUtc = nowUtc,
                BaselineRecalibrationUntilUtc = untilUtc,
                UpdatedAtUtc = nowUtc
            };
            SaveSettingsState(_settingsState);
            WriteSettingsStateToStoreLocked(_settingsState, nowUtc);
            _lastEventTimestampUtc = nowUtc;
            _latestSnapshot = _latestSnapshot with { LastAction = "baseline_recalibration_started" };
            WriteLogLocked("baseline_recalibration_started");
            WriteControlEventLocked(
                action: "baseline_recalibration_started",
                reason: "user_recalibrate_baseline",
                triggerState: "control",
                triggerValue: 1,
                confidence: 1,
                escalationLevel: "Inform",
                reversible: true);
            WriteSystemChangeLocked(
                changeType: "baseline_recalibration",
                previousValue: new { startedUtc = previousStart, untilUtc = previousUntil },
                newValue: new { startedUtc = nowUtc, untilUtc },
                undoInfo: "wait for timer or update settings",
                reason: "user_recalibrate_baseline");
            WriteDiagnosticsLocked(force: true);
            return new AgentRecalibrationResult(
                Active: true,
                StartedUtc: nowUtc,
                UntilUtc: untilUtc);
        }
    }

    public void DeleteLocalData()
    {
        lock (_gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            _stateStore.DeleteLocalData();
            TryDeleteFile(_logPath);
            TryDeleteFile(_consentPath);
            TryDeleteFile(_settingsPath);

            _consentState = CreateDefaultConsentState();
            _consentActive = false;
            _settingsState = CreateDefaultSettingsState();
            _paused = true;
            _pauseUntilUtc = null;
            _lastError = null;
            _lastEventTimestampUtc = nowUtc;
            _latestSnapshot = _latestSnapshot with { LastAction = "local_data_deleted" };
            WriteConsentStateToStoreLocked(_consentState, nowUtc);
            WriteSettingsStateToStoreLocked(_settingsState, nowUtc);
            WriteSystemChangeLocked(
                changeType: "local_data",
                previousValue: new { state = "present" },
                newValue: new { state = "deleted_and_reset" },
                undoInfo: "none",
                reason: "user_delete_local_data");
            WriteLogLocked("local_data_deleted");
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
            if (backendConnected)
            {
                _lastBackendOkUtc = snapshot.TimestampUtc;
            }
            _lastEventTimestampUtc = snapshot.TimestampUtc;
            WriteDiagnosticsLocked(force: false);
        }
    }

    public string ExportBundle()
    {
        lock (_gate)
        {
            WriteLogLocked("export_bundle");
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["agent"] = _agentVersion,
                ["schema"] = DefaultSchemaVersion,
                ["policy"] = DefaultPolicyVersion
            };

            IReadOnlyList<string> configPaths =
            [
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                BuildSettingsPath(),
                BuildLocalApiSecureSettingsPath(),
                BuildLocalApiEndpointPath()
            ];

            return _stateStore.ExportBundle(
                logFilePath: _logPath,
                consentPath: _consentPath,
                settingsPath: _settingsPath,
                versions: versions,
                configPaths: configPaths);
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
            LastEventTimestampUtc: _lastEventTimestampUtc,
            LastBackendOkUtc: _lastBackendOkUtc));

        _lastDiagnosticsWriteUtc = nowUtc;
    }

    private ConsentStateRecord LoadConsentState()
    {
        try
        {
            if (!File.Exists(_consentPath))
            {
                return CreateDefaultConsentState();
            }

            string raw = File.ReadAllText(_consentPath).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return CreateDefaultConsentState();
            }

            if (bool.TryParse(raw, out bool legacyActive))
            {
                return CreateLegacyConsentState(legacyActive);
            }

            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CreateDefaultConsentState();
            }

            if (TryGetBoolean(root, "active", out bool active))
            {
                return CreateLoadedConsentState(
                    active,
                    TryGetString(root, "version"),
                    TryGetDateTimeOffset(root, "timestampUtc"),
                    TryGetString(root, "disclosureHash"),
                    TryGetDateTimeOffset(root, "updatedAtUtc"));
            }

            string? legacyState = TryGetString(root, "state") ?? TryGetString(root, "State");
            if (!string.IsNullOrWhiteSpace(legacyState))
            {
                bool activeFromState = string.Equals(legacyState, "ConsentedActive", StringComparison.OrdinalIgnoreCase);
                return CreateLoadedConsentState(
                    activeFromState,
                    version: ConsentVersion,
                    timestampUtc: TryGetDateTimeOffset(root, "timestampUtc"),
                    disclosureHash: ConsentDisclosureHash,
                    updatedAtUtc: TryGetDateTimeOffset(root, "updatedAtUtc"));
            }

            return CreateDefaultConsentState();
        }
        catch
        {
            return CreateDefaultConsentState();
        }
    }

    private void SaveConsentState(ConsentStateRecord state)
    {
        string? directory = Path.GetDirectoryName(_consentPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(new
        {
            active = state.Active,
            version = state.Version,
            timestampUtc = state.TimestampUtc,
            disclosureHash = state.DisclosureHash,
            updatedAtUtc = state.UpdatedAtUtc
        }, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_consentPath, json);
    }

    private SettingsStateRecord LoadSettingsState()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CreateDefaultSettingsState();
            }

            string raw = File.ReadAllText(_settingsPath).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return CreateDefaultSettingsState();
            }

            SettingsStateRecord? loaded = JsonSerializer.Deserialize<SettingsStateRecord>(raw);
            if (loaded == null)
            {
                return CreateDefaultSettingsState();
            }

            string intensity = NormalizeIntensity(loaded.RegulationIntensity, fallback: "Balanced");
            string privacy = NormalizePrivacy(loaded.PrivacyLevel, fallback: "Standard");
            return loaded with
            {
                RegulationIntensity = intensity,
                PrivacyLevel = privacy
            };
        }
        catch
        {
            return CreateDefaultSettingsState();
        }
    }

    private void SaveSettingsState(SettingsStateRecord state)
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_settingsPath, json);
    }

    private void WriteConsentStateToStoreLocked(ConsentStateRecord state, DateTimeOffset nowUtc)
    {
        _stateStore.WriteConsent(new AgentConsentRecord(
            TimestampUtc: state.TimestampUtc ?? nowUtc,
            Active: state.Active,
            Version: state.Version,
            DisclosureHash: state.DisclosureHash));
    }

    private void WriteSettingsStateToStoreLocked(SettingsStateRecord state, DateTimeOffset nowUtc)
    {
        _stateStore.WriteSettings(new AgentSettingsRecord(
            TimestampUtc: state.UpdatedAtUtc == DateTimeOffset.MinValue ? nowUtc : state.UpdatedAtUtc,
            PrivacyLevel: state.PrivacyLevel,
            RegulationIntensity: state.RegulationIntensity,
            DisabledCategories: BuildDisabledCategories(state),
            NotificationReductionEnabled: state.NotificationReductionEnabled,
            FocusModeEnabled: state.FocusModeEnabled,
            AnimationReductionEnabled: state.AnimationReductionEnabled,
            TemporaryAppRestrictionEnabled: state.TemporaryAppRestrictionEnabled,
            BaselineRecalibrationStartedUtc: state.BaselineRecalibrationStartedUtc,
            BaselineRecalibrationUntilUtc: state.BaselineRecalibrationUntilUtc));
    }

    private void WriteSystemChangeLocked(string changeType, object previousValue, object newValue, string undoInfo, string reason)
    {
        _stateStore.WriteSystemChange(new AgentSystemChange(
            TimestampUtc: DateTimeOffset.UtcNow,
            ChangeType: changeType,
            PreviousValueJson: JsonSerializer.Serialize(previousValue),
            NewValueJson: JsonSerializer.Serialize(newValue),
            UndoInfo: undoInfo,
            Reason: reason));
    }

    private void UpdateRecalibrationStateLocked()
    {
        if (!_settingsState.BaselineRecalibrationUntilUtc.HasValue)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _settingsState.BaselineRecalibrationUntilUtc.Value)
        {
            return;
        }

        _settingsState = _settingsState with
        {
            BaselineRecalibrationStartedUtc = null,
            BaselineRecalibrationUntilUtc = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        SaveSettingsState(_settingsState);
        WriteSettingsStateToStoreLocked(_settingsState, _settingsState.UpdatedAtUtc);
    }

    private static string NormalizeIntensity(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "gentle" => "Gentle",
            "balanced" => "Balanced",
            "strong" => "Strong",
            _ => fallback
        };
    }

    private static string NormalizePrivacy(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "minimal" => "Minimal",
            "standard" => "Standard",
            "detailed" => "Detailed",
            _ => fallback
        };
    }

    private static IReadOnlyList<string> BuildDisabledCategories(SettingsStateRecord state)
    {
        var disabled = new List<string>(capacity: 4);
        if (!state.NotificationReductionEnabled)
        {
            disabled.Add("notification_reduction");
        }

        if (!state.FocusModeEnabled)
        {
            disabled.Add("focus_mode");
        }

        if (!state.AnimationReductionEnabled)
        {
            disabled.Add("animation_reduction");
        }

        if (!state.TemporaryAppRestrictionEnabled)
        {
            disabled.Add("temporary_app_restriction");
        }

        return disabled;
    }

    private static AgentSettingsState ToPublicSettingsState(SettingsStateRecord state)
    {
        bool recalibrationActive = state.BaselineRecalibrationUntilUtc.HasValue
            && DateTimeOffset.UtcNow < state.BaselineRecalibrationUntilUtc.Value;

        return new AgentSettingsState(
            RegulationIntensity: state.RegulationIntensity,
            PrivacyLevel: state.PrivacyLevel,
            NotificationReductionEnabled: state.NotificationReductionEnabled,
            FocusModeEnabled: state.FocusModeEnabled,
            AnimationReductionEnabled: state.AnimationReductionEnabled,
            TemporaryAppRestrictionEnabled: state.TemporaryAppRestrictionEnabled,
            BaselineRecalibrationActive: recalibrationActive,
            BaselineRecalibrationStartedUtc: recalibrationActive ? state.BaselineRecalibrationStartedUtc : null,
            BaselineRecalibrationUntilUtc: recalibrationActive ? state.BaselineRecalibrationUntilUtc : null,
            UpdatedAtUtc: state.UpdatedAtUtc);
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

    private static ConsentStateRecord CreateDefaultConsentState()
    {
        return new ConsentStateRecord(
            Active: false,
            Version: ConsentVersion,
            TimestampUtc: null,
            DisclosureHash: ConsentDisclosureHash,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private ConsentStateRecord CreateLegacyConsentState(bool active)
    {
        DateTimeOffset timestampUtc = DateTimeOffset.UtcNow;
        try
        {
            timestampUtc = File.GetLastWriteTimeUtc(_consentPath);
        }
        catch
        {
        }

        return new ConsentStateRecord(
            Active: active,
            Version: ConsentVersion,
            TimestampUtc: timestampUtc,
            DisclosureHash: ConsentDisclosureHash,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static ConsentStateRecord CreateConsentState(bool active, DateTimeOffset timestampUtc)
    {
        return new ConsentStateRecord(
            Active: active,
            Version: ConsentVersion,
            TimestampUtc: timestampUtc,
            DisclosureHash: ConsentDisclosureHash,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static ConsentStateRecord CreateLoadedConsentState(
        bool active,
        string? version,
        DateTimeOffset? timestampUtc,
        string? disclosureHash,
        DateTimeOffset? updatedAtUtc)
    {
        string normalizedVersion = string.IsNullOrWhiteSpace(version) ? ConsentVersion : version.Trim();
        string normalizedHash = string.IsNullOrWhiteSpace(disclosureHash) ? ConsentDisclosureHash : disclosureHash.Trim();
        DateTimeOffset? normalizedTimestamp = timestampUtc;
        DateTimeOffset normalizedUpdated = updatedAtUtc ?? normalizedTimestamp ?? DateTimeOffset.UtcNow;

        return new ConsentStateRecord(
            Active: active,
            Version: normalizedVersion,
            TimestampUtc: normalizedTimestamp,
            DisclosureHash: normalizedHash,
            UpdatedAtUtc: normalizedUpdated);
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out bool parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(element.GetString(), out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static string ComputeSha256Hex(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static string BuildConsentPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", ConsentFileName);
    }

    private static string BuildSettingsPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", SettingsFileName);
    }

    private static string BuildLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", ServiceLogFileName);
    }

    private static string BuildLocalApiSecureSettingsPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "security", "local-api.secure.json");
    }

    private static string BuildLocalApiEndpointPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "local-api.endpoint.json");
    }

    private static SettingsStateRecord CreateDefaultSettingsState()
    {
        return new SettingsStateRecord(
            RegulationIntensity: "Balanced",
            PrivacyLevel: "Standard",
            NotificationReductionEnabled: true,
            FocusModeEnabled: true,
            AnimationReductionEnabled: false,
            TemporaryAppRestrictionEnabled: false,
            BaselineRecalibrationStartedUtc: null,
            BaselineRecalibrationUntilUtc: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private sealed record ConsentStateRecord(
        bool Active,
        string Version,
        DateTimeOffset? TimestampUtc,
        string DisclosureHash,
        DateTimeOffset UpdatedAtUtc);

    private sealed record SettingsStateRecord(
        string RegulationIntensity,
        string PrivacyLevel,
        bool NotificationReductionEnabled,
        bool FocusModeEnabled,
        bool AnimationReductionEnabled,
        bool TemporaryAppRestrictionEnabled,
        DateTimeOffset? BaselineRecalibrationStartedUtc,
        DateTimeOffset? BaselineRecalibrationUntilUtc,
        DateTimeOffset UpdatedAtUtc);
}
