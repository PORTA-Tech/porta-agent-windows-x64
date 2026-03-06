using System.Diagnostics;
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
    private const string TrayLogFileName = "tray.log";
    private const string UiLogFileName = "control-center.log";
    private const string DefaultSchemaVersion = "1.0.0";
    private const string DefaultPolicyVersion = "1.0.0";
    private static readonly string ConsentDisclosureHash = ComputeSha256Hex(ConsentDisclosureText);

    private readonly object _gate = new();
    private readonly IBrainStateStore _stateStore;
    private readonly string _consentPath = BuildConsentPath();
    private readonly string _settingsPath = BuildSettingsPath();
    private readonly string _logPath = BuildLogPath();
    private readonly string _trayLogPath = BuildTrayLogPath();
    private readonly string _uiLogPath = BuildUiLogPath();
    private bool _paused;
    private bool _consentActive;
    private DateTimeOffset? _pauseUntilUtc;
    private DateTimeOffset? _lastEventTimestampUtc;
    private string? _lastError;
    private bool _localApiAuthReady = true;
    private string? _localApiAuthError;
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
        : this(new BucketFinalizerStateStore(new SqliteBrainStateStore()))
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
            AgentNotificationSuppressionStatus suppressionStatus = _stateStore.ReadNotificationSuppressionStatus();
            AgentBrightnessActionStatus brightnessStatus = _stateStore.ReadBrightnessActionStatus();
            string? effectiveLastError = ResolveEffectiveLastError(_lastError, _latestSnapshot.StateFlagsJson);
            AgentStatus status = effectiveLastError is null
                ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
                : AgentStatus.Degraded;

            return new AgentServiceStatusSnapshot(
                Status: status,
                ServiceRunning: true,
                Paused: _paused,
                PauseUntilUtc: _pauseUntilUtc,
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
                LastError: effectiveLastError,
                LocalApiAuthReady: _localApiAuthReady,
                LocalApiAuthError: _localApiAuthError,
                NotificationSuppressionActive: suppressionStatus.Active,
                NotificationSuppressionReady: suppressionStatus.Ready,
                NotificationSuppressionLastError: suppressionStatus.LastError,
                NotificationSuppressionBackoffUntilUtc: suppressionStatus.BackoffUntilUtc,
                BrightnessSupported: brightnessStatus.Supported,
                BrightnessAdjusted: brightnessStatus.Active,
                BrightnessLastError: brightnessStatus.LastError,
                BrightnessBackoffUntilUtc: brightnessStatus.BackoffUntilUtc,
                LastUpdatedUtc: DateTimeOffset.UtcNow);
        }
    }

    public void SetLocalApiAuthState(bool ready, string? errorCode)
    {
        lock (_gate)
        {
            _localApiAuthReady = ready;
            _localApiAuthError = ready ? null : string.IsNullOrWhiteSpace(errorCode) ? "TOKEN_READ_FAILED" : errorCode;
        }
    }

    public AgentControlState GetControlState()
    {
        lock (_gate)
        {
            TryAutoResumeLocked();
            UpdateRecalibrationStateLocked();
            return new AgentControlState(
                Paused: _paused,
                ConsentActive: _consentActive,
                PauseUntilUtc: _pauseUntilUtc,
                RegulationIntensity: _settingsState.RegulationIntensity,
                NotificationReductionEnabled: _settingsState.NotificationReductionEnabled,
                FocusModeEnabled: _settingsState.FocusModeEnabled,
                AnimationReductionEnabled: _settingsState.AnimationReductionEnabled,
                TemporaryAppRestrictionEnabled: _settingsState.TemporaryAppRestrictionEnabled);
        }
    }

    public AgentBrainSnapshot GetCurrentSnapshot()
    {
        lock (_gate)
        {
            return _latestSnapshot;
        }
    }

    public AgentLatestStateDetails? GetLatestStateDetails()
    {
        lock (_gate)
        {
            return _stateStore.ReadLatestStateDetails();
        }
    }

    public AgentInputSummary GetInputsSummary(int windowSeconds = 60)
    {
        int window = Math.Clamp(windowSeconds, 5, 3600);
        DateTimeOffset toUtc = DateTimeOffset.UtcNow;
        DateTimeOffset fromUtc = toUtc.AddSeconds(-window);

        // Hard-gate reporting while paused/no-consent so UI and release checks
        // reflect "collection stopped" immediately.
        if (_paused || !_consentActive)
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

    public IReadOnlyList<AgentControlEvent> GetEventsSinceId(long sinceIdExclusive, int limit = 200)
    {
        int boundedLimit = Math.Clamp(limit, 1, 1000);
        long safeSinceId = Math.Max(0, sinceIdExclusive);
        return _stateStore.ReadEventsSinceId(safeSinceId, boundedLimit);
    }

    public AgentTimelineData GetTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds)
    {
        int bucket = Math.Clamp(bucketSeconds, 1, 3600);
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
            long logBytes = TryGetFileSize(_logPath) + TryGetFileSize(_trayLogPath) + TryGetFileSize(_uiLogPath);
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
            TryDeleteFile(_trayLogPath);
            TryDeleteFile(_uiLogPath);
            TryDeleteFile(_consentPath);
            TryDeleteFile(_settingsPath);
            TryDeleteDirectory(BuildExportRootPath());

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

    public void TriggerTestIntervention(string action)
    {
        lock (_gate)
        {
            TryAutoResumeLocked();
            UpdateRecalibrationStateLocked();

            if (!_consentActive)
            {
                throw new InvalidOperationException("consent_required");
            }

            if (_paused)
            {
                throw new InvalidOperationException("paused");
            }

            string normalizedAction = NormalizeTestAction(action);
            if (!IsTestActionAllowedBySettings(normalizedAction, _settingsState))
            {
                throw new InvalidOperationException("category_disabled");
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            _latestSnapshot = BuildTestSnapshot(_latestSnapshot, normalizedAction, nowUtc);
            _stateStore.WriteSnapshot(_latestSnapshot);

            (string triggerState, double triggerValue, string escalation, bool reversible, int? durationSeconds) =
                ResolveTestInterventionMetadata(normalizedAction);
            string reason = $"diagnostics_test_trigger action={normalizedAction}, trigger={triggerState}={triggerValue:F2}, confidence=0.99";
            string whatChanged = ResolveEventWhatChanged(normalizedAction, lifecycleState: "activated");
            string howToUndo = ResolveEventHowToUndo(normalizedAction, reversible, lifecycleState: "activated");

            _stateStore.WriteControlEvent(new AgentControlEvent(
                TimestampUtc: nowUtc,
                Action: normalizedAction,
                TriggerState: triggerState,
                TriggerValue: triggerValue,
                Confidence: 0.99,
                DurationSeconds: durationSeconds,
                EscalationLevel: escalation,
                Reversible: reversible,
                Reason: reason,
                Contributors: ["diagnostics", "manual_test"],
                InterventionId: Guid.NewGuid().ToString("N"),
                PolicyId: ResolveTestPolicyId(normalizedAction),
                Domain: ResolveTestDomain(normalizedAction),
                PlannedEndTimestampUtc: durationSeconds.HasValue ? nowUtc.AddSeconds(Math.Max(durationSeconds.Value, 0)) : null,
                ActualEndTimestampUtc: null,
                PreviousSystemStateJson: "{\"action\":\"none\"}",
                WhatChanged: whatChanged,
                HowToUndo: howToUndo,
                ReversalApplied: false,
                CooldownEndTimestampUtc: null,
                LifecycleState: "activated"));

            WriteSystemChangeLocked(
                changeType: "regulation_test",
                previousValue: new { action = "none" },
                newValue: new
                {
                    action = normalizedAction,
                    escalation,
                    reversible,
                    source = "diagnostics"
                },
                undoInfo: reversible
                    ? "disable category in settings/interventions"
                    : "none",
                reason: reason);

            _lastEventTimestampUtc = nowUtc;
            _lastError = null;
            WriteLogLocked(reason);
            WriteDiagnosticsLocked(force: true);
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
                ["ui"] = ResolveUiVersion(),
                ["tray"] = ResolveTrayVersion(),
                ["schema"] = DefaultSchemaVersion,
                ["policy"] = DefaultPolicyVersion
            };

            IReadOnlyList<string> logPaths =
            [
                _logPath,
                _trayLogPath,
                _uiLogPath
            ];

            IReadOnlyList<string> configPaths =
            [
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                BuildSettingsPath(),
                BuildLocalApiRuntimeSettingsPath(),
                BuildLocalApiEndpointPath()
            ];

            IReadOnlyDictionary<string, string> supplementalJsonFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["diagnostics-summary.json"] = BuildDiagnosticsSummaryJson(),
                ["consent-record.json"] = BuildConsentRecordJson(),
                ["settings-effective.json"] = BuildSettingsRecordJson()
            };

            return _stateStore.ExportBundle(
                logFilePaths: logPaths,
                consentPath: _consentPath,
                settingsPath: _settingsPath,
                versions: versions,
                configPaths: configPaths,
                supplementalJsonFiles: supplementalJsonFiles);
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
            Contributors: ["controller"],
            InterventionId: Guid.NewGuid().ToString("N"),
            PolicyId: "control-event",
            Domain: "control",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{\"action\":\"none\"}",
            WhatChanged: ResolveEventWhatChanged(action, lifecycleState: "activated"),
            HowToUndo: ResolveEventHowToUndo(action, reversible, lifecycleState: "activated"),
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "activated"));
    }

    private static string ResolveEventWhatChanged(string action, string lifecycleState)
    {
        if (string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase))
        {
            return "Intervention ended and automatic restoration was attempted.";
        }

        return action switch
        {
            "focus_mode_enabled" => "Focus mode controls were enabled for overload relief.",
            "focus_assist_enabled" => "Focus Assist was enabled to protect deep focus.",
            "fatigue_relief_enabled" => "Fatigue relief controls were enabled (dimming + animation reduction).",
            "temporary_app_restriction_enabled" => "Temporary app restriction was activated for restrict-level control.",
            "break_suggestion" => "Break suggestion was presented in the user interface.",
            "focus_lock_in_suggestion" => "Focus lock-in suggestion was presented in the user interface.",
            "stretch_break_suggestion" => "Stretch break suggestion was presented in the user interface.",
            "inform_overload_risk" => "Overload risk was logged (inform-only).",
            "inform_focus_protection" => "Focus protection was logged (inform-only).",
            "inform_fatigue_prevention" => "Fatigue prevention was logged (inform-only).",
            _ => $"Action {action} was applied."
        };
    }

    private static string ResolveEventHowToUndo(string action, bool reversible, string lifecycleState)
    {
        if (string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase))
        {
            return "No action required. PORTA already attempted automatic reversal.";
        }

        return action switch
        {
            "break_suggestion" or "focus_lock_in_suggestion" or "stretch_break_suggestion"
                => "Dismiss the suggestion overlay or let it auto-expire.",
            "inform_overload_risk" or "inform_focus_protection" or "inform_fatigue_prevention"
                => "No undo needed. Inform-only event.",
            _ when reversible
                => "Use Undo Action in Interventions or disable the category in Settings.",
            _ => "No direct undo path is available."
        };
    }

    private static bool IsTestActionAllowedBySettings(string action, SettingsStateRecord settings)
    {
        return action switch
        {
            "focus_mode_enabled" => settings.FocusModeEnabled,
            "focus_assist_enabled" => settings.FocusModeEnabled,
            "fatigue_relief_enabled" => settings.AnimationReductionEnabled,
            "temporary_app_restriction_enabled" => settings.TemporaryAppRestrictionEnabled,
            _ => true
        };
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

    private static string NormalizeTestAction(string action)
    {
        string normalized = action.Trim().ToLowerInvariant();
        return normalized switch
        {
            "inform_overload_risk" => normalized,
            "inform_focus_protection" => normalized,
            "inform_fatigue_prevention" => normalized,
            "break_suggestion" => normalized,
            "focus_lock_in_suggestion" => normalized,
            "stretch_break_suggestion" => normalized,
            "focus_mode_enabled" => normalized,
            "focus_assist_enabled" => normalized,
            "fatigue_relief_enabled" => normalized,
            "temporary_app_restriction_enabled" => normalized,
            _ => throw new InvalidOperationException("unsupported_test_action")
        };
    }

    private static (string TriggerState, double TriggerValue, string Escalation, bool Reversible, int? DurationSeconds)
        ResolveTestInterventionMetadata(string action)
    {
        return action switch
        {
            "temporary_app_restriction_enabled" => ("overload", 0.95, "Restrict", true, 180),
            "focus_mode_enabled" => ("overload", 0.86, "Nudge", true, 600),
            "focus_assist_enabled" => ("focus", 0.86, "Nudge", true, 600),
            "fatigue_relief_enabled" => ("fatigue", 0.86, "Nudge", true, 600),
            "break_suggestion" => ("fatigue", 0.82, "Suggest", false, 300),
            "focus_lock_in_suggestion" => ("focus", 0.83, "Suggest", false, 300),
            "stretch_break_suggestion" => ("fatigue", 0.84, "Suggest", false, 300),
            "inform_overload_risk" => ("overload", 0.72, "Inform", false, 60),
            "inform_focus_protection" => ("focus", 0.72, "Inform", false, 60),
            "inform_fatigue_prevention" => ("fatigue", 0.72, "Inform", false, 60),
            _ => ("control", 1.0, "Inform", false, null)
        };
    }

    private static string ResolveTestPolicyId(string action)
    {
        return action switch
        {
            "break_suggestion" => "fatigue-prevention-v1",
            "focus_lock_in_suggestion" => "focus-protection-v1",
            "focus_assist_enabled" => "focus-protection-v1",
            "inform_focus_protection" => "focus-protection-v1",
            "stretch_break_suggestion" => "fatigue-prevention-v1",
            "fatigue_relief_enabled" => "fatigue-prevention-v1",
            "inform_fatigue_prevention" => "fatigue-prevention-v1",
            "focus_mode_enabled" => "overload-relief-v1",
            "temporary_app_restriction_enabled" => "overload-relief-v1",
            "inform_overload_risk" => "overload-relief-v1",
            _ => "diagnostics-test"
        };
    }

    private static string ResolveTestDomain(string action)
    {
        return action switch
        {
            "break_suggestion" => "fatigue",
            "focus_lock_in_suggestion" => "focus",
            "focus_assist_enabled" => "focus",
            "inform_focus_protection" => "focus",
            "stretch_break_suggestion" => "fatigue",
            "fatigue_relief_enabled" => "fatigue",
            "inform_fatigue_prevention" => "fatigue",
            "focus_mode_enabled" => "overload",
            "temporary_app_restriction_enabled" => "overload",
            "inform_overload_risk" => "overload",
            _ => "control"
        };
    }

    private static AgentBrainSnapshot BuildTestSnapshot(AgentBrainSnapshot baseline, string action, DateTimeOffset nowUtc)
    {
        (string _, double _, string escalation, bool _, int? durationSeconds) = ResolveTestInterventionMetadata(action);

        double overload = action switch
        {
            "temporary_app_restriction_enabled" => 0.95,
            "focus_mode_enabled" => 0.86,
            "inform_overload_risk" => 0.72,
            "focus_assist_enabled" => 0.38,
            "focus_lock_in_suggestion" => 0.33,
            "inform_focus_protection" => 0.30,
            "fatigue_relief_enabled" => 0.34,
            "stretch_break_suggestion" => 0.36,
            "inform_fatigue_prevention" => 0.40,
            _ => Math.Max(baseline.OverloadLikelihood, 0.45)
        };

        double fatigue = action switch
        {
            "break_suggestion" => 0.82,
            "fatigue_relief_enabled" => 0.88,
            "stretch_break_suggestion" => 0.86,
            "inform_fatigue_prevention" => 0.78,
            _ => Math.Max(baseline.FatigueProbability, 0.33)
        };
        double focus = action switch
        {
            "focus_mode_enabled" => 0.64,
            "focus_assist_enabled" => 0.88,
            "focus_lock_in_suggestion" => 0.84,
            "inform_focus_protection" => 0.78,
            "fatigue_relief_enabled" => 0.36,
            "stretch_break_suggestion" => 0.34,
            "inform_fatigue_prevention" => 0.40,
            "break_suggestion" => 0.41,
            "temporary_app_restriction_enabled" => 0.38,
            _ => Math.Max(baseline.FocusStrength, 0.48)
        };
        double stability = Math.Clamp(1d - overload, 0.05, 0.95);

        string dominantStateName;
        double dominantStateValue;
        if (focus >= overload && focus >= fatigue)
        {
            dominantStateName = "focus";
            dominantStateValue = focus;
        }
        else if (overload >= fatigue)
        {
            dominantStateName = "overload";
            dominantStateValue = overload;
        }
        else
        {
            dominantStateName = "fatigue";
            dominantStateValue = fatigue;
        }

        return baseline with
        {
            TimestampUtc = nowUtc,
            FocusStrength = focus,
            OverloadLikelihood = overload,
            FatigueProbability = fatigue,
            CognitiveStability = stability,
            DominantStateName = dominantStateName,
            DominantStateValue = dominantStateValue,
            Confidence = 0.99,
            LastAction = action,
            ActivationWindowTriggered = true,
            CooldownTriggered = false,
            ActivationWindowOpen = true,
            Contributors = ["diagnostics_override", "manual_test"],
            PolicyId = ResolveTestPolicyId(action),
            Domain = ResolveTestDomain(action),
            EscalationLevel = escalation,
            PlannedEndTimestampUtc = durationSeconds.HasValue
                ? nowUtc.AddSeconds(Math.Max(durationSeconds.Value, 0))
                : null,
            ActualEndTimestampUtc = null,
            CooldownEndTimestampUtc = null
        };
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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

    private static string BuildTrayLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", TrayLogFileName);
    }

    private static string BuildUiLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", UiLogFileName);
    }

    private static string BuildExportRootPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "exports");
    }

    private static string BuildLocalApiRuntimeSettingsPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "security", "local-api.runtime.json");
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

    private string BuildDiagnosticsSummaryJson()
    {
        AgentNotificationSuppressionStatus suppressionStatus = _stateStore.ReadNotificationSuppressionStatus();
        string? effectiveLastError = ResolveEffectiveLastError(_lastError, _latestSnapshot.StateFlagsJson);
        AgentStatus status = effectiveLastError is null
            ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
            : AgentStatus.Degraded;

        var payload = new
        {
            generated_at_utc = DateTimeOffset.UtcNow,
            service_running = true,
            status = status.ToString(),
            paused = _paused,
            consent_active = _consentActive,
            backend_connected = _backendConnected,
            last_event_timestamp_utc = _lastEventTimestampUtc,
            last_backend_ok_utc = _lastBackendOkUtc,
            last_error = effectiveLastError,
            local_api_auth_ready = _localApiAuthReady,
            local_api_auth_error = _localApiAuthError,
            notification_suppression = new
            {
                active = suppressionStatus.Active,
                ready = suppressionStatus.Ready,
                action_state = suppressionStatus.ActionState,
                last_error = suppressionStatus.LastError,
                backoff_until_utc = suppressionStatus.BackoffUntilUtc,
                enable_cooldown_until_utc = suppressionStatus.EnableCooldownUntilUtc,
                min_on_until_utc = suppressionStatus.MinOnUntilUtc,
                restore_pending = suppressionStatus.RestorePending
            },
            last_action = _latestSnapshot.LastAction,
            focus_strength = _latestSnapshot.FocusStrength,
            overload_likelihood = _latestSnapshot.OverloadLikelihood,
            fatigue_probability = _latestSnapshot.FatigueProbability,
            confidence = _latestSnapshot.Confidence,
            versions = new
            {
                agent = _agentVersion,
                ui = ResolveUiVersion(),
                tray = ResolveTrayVersion(),
                schema = DefaultSchemaVersion,
                policy = DefaultPolicyVersion
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? ResolveEffectiveLastError(string? controllerLastError, string stateFlagsJson)
    {
        if (!string.IsNullOrWhiteSpace(controllerLastError))
        {
            return controllerLastError;
        }

        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("notification_suppression_error", out JsonElement errorNode))
            {
                return null;
            }

            string? error = errorNode.ValueKind == JsonValueKind.String
                ? errorNode.GetString()
                : errorNode.ToString();
            return string.IsNullOrWhiteSpace(error) ? null : error;
        }
        catch
        {
            return null;
        }
    }

    private string BuildConsentRecordJson()
    {
        var payload = new
        {
            active = _consentState.Active,
            version = _consentState.Version,
            timestamp_utc = _consentState.TimestampUtc,
            disclosure_hash = _consentState.DisclosureHash,
            updated_at_utc = _consentState.UpdatedAtUtc
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildSettingsRecordJson()
    {
        var payload = new
        {
            regulation_intensity = _settingsState.RegulationIntensity,
            privacy_level = _settingsState.PrivacyLevel,
            notification_reduction_enabled = _settingsState.NotificationReductionEnabled,
            focus_mode_enabled = _settingsState.FocusModeEnabled,
            animation_reduction_enabled = _settingsState.AnimationReductionEnabled,
            temporary_app_restriction_enabled = _settingsState.TemporaryAppRestrictionEnabled,
            baseline_recalibration_started_utc = _settingsState.BaselineRecalibrationStartedUtc,
            baseline_recalibration_until_utc = _settingsState.BaselineRecalibrationUntilUtc,
            updated_at_utc = _settingsState.UpdatedAtUtc
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ResolveUiVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Porta.ControlCenter.exe");
            if (!File.Exists(path))
            {
                return "unknown";
            }

            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? "unknown" : info.FileVersion;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ResolveTrayVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "AriTray.exe");
            if (!File.Exists(path))
            {
                return "unknown";
            }

            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? "unknown" : info.FileVersion;
        }
        catch
        {
            return "unknown";
        }
    }
}
