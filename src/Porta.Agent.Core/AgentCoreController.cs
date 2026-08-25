using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Porta.Agent.Common;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.UserAgent;

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
    private const int MaxSignalIdLength = 128;
    private const int MaxFocusHashLength = 128;
    private const string CaptureContextUnknown = UserAgentCaptureContexts.Unknown;
    private const string CaptureContextUserSession = UserAgentCaptureContexts.UserSession;
    private const string CaptureContextServiceSession = UserAgentCaptureContexts.ServiceSession;
    private const string CaptureFailureReasonUnknown = "CAPTURE_CONTEXT_UNKNOWN";
    private const string CaptureFailureReasonServiceSession = "CAPTURE_CONTEXT_SERVICE_SESSION";
    private const string CaptureFailureReasonHeartbeatStale = "USER_AGENT_HEARTBEAT_STALE";
    private const string CaptureFailureReasonSessionSwitched = "USER_AGENT_SESSION_SWITCHED";
    private const string CaptureFailureReasonInputSelfTestFailed = "USER_AGENT_INPUT_SELF_TEST_FAILED";
    private const string CaptureSelfTestStateFailed = "failed";
    private const string OwnershipWarningHeartbeatMissing = "USER_AGENT_HEARTBEAT_MISSING";
    private const string OwnershipWarningAgentInstanceMismatch = "USER_AGENT_INSTANCE_MISMATCH";
    private const string OwnershipWarningSessionMismatch = "USER_AGENT_SESSION_MISMATCH";
    private const string InputSignalsSourceHeartbeatMissing = "input_signals_source_heartbeat_missing";
    private const string InputSignalsSourceInvalidContext = "input_signals_source_invalid_context";
    private const string InputSignalsSourceAgentMismatch = "input_signals_source_agent_mismatch";
    private const string InputSignalsSourceSessionMismatch = "input_signals_source_session_mismatch";
    private static readonly TimeSpan SignalHealthActiveWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CaptureContextFreshnessWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnershipReconciliationGraceWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SameProcessHeartbeatGraceWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StatusReadLockTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LatestStateReadLockTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RuntimeStateStaleAfter = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LiveSessionStateFreshnessWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LiveSessionUserActiveWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LiveSessionIdleThreshold = TimeSpan.FromSeconds(5);
    private static readonly AgentNotificationSuppressionStatus DefaultNotificationSuppressionStatus = new(
        Active: false,
        Ready: true,
        ActionState: "inactive",
        LastError: null,
        BackoffUntilUtc: null,
        EnableCooldownUntilUtc: null,
        MinOnUntilUtc: null,
        RestorePending: false);
    private static readonly AgentBrightnessActionStatus DefaultBrightnessActionStatus = new(
        Supported: true,
        ActionReady: true,
        Active: false,
        RampDirection: "idle",
        PreviousLevel: -1,
        TargetLevel: -1,
        CurrentLevel: -1,
        LastError: null,
        BackoffUntilUtc: null,
        CooldownUntilUtc: null);
    private static readonly string ConsentDisclosureHash = ComputeSha256Hex(ConsentDisclosureText);
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex FocusHashPattern = new(@"^[a-fA-F0-9-_.:]+$", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly IBrainStateStore _stateStore;
    private readonly IInputSignalsIngress? _inputSignalsIngress;
    private readonly IUserAgentSessionContextStore _userAgentSessionContextStore;
    private readonly IRuntimePolicyProvider _runtimePolicyProvider;
    private readonly string _consentPath = BuildConsentPath();
    private readonly string _settingsPath = BuildSettingsPath();
    private readonly string _logPath = BuildLogPath();
    private readonly string _trayLogPath = BuildTrayLogPath();
    private readonly string _uiLogPath = BuildUiLogPath();
    private bool _paused;
    private bool _consentActive;
    private DateTimeOffset? _pauseUntilUtc;
    private DateTimeOffset? _lastEventTimestampUtc;
    private string _captureContext = CaptureContextUnknown;
    private readonly string _backendInstanceId = BuildBackendInstanceId();
    private DateTimeOffset? _lastKeyboardSignalUtc;
    private DateTimeOffset? _lastMouseSignalUtc;
    private DateTimeOffset? _lastInputEventUtc;
    private string? _lastInputAgentInstanceId;
    private string? _lastInputSessionId;
    private long? _lastAcceptedHeartbeatSequence;
    private DateTimeOffset? _lastAcceptedHeartbeatUpdatedAtUtc;
    private string? _lastAcceptedHeartbeatAgentInstanceId;
    private string? _lastAcceptedHeartbeatSessionId;
    private string? _lastError;
    private volatile bool _localApiAuthReady = true;
    private volatile string? _localApiAuthError;
    private bool _backendConnected;
    private string? _watchdogLaunchFailureCode;
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
    private bool _latestSnapshotBackedByStore;
    private readonly string _agentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
    private ConsentStateRecord _consentState = CreateDefaultConsentState();
    private SettingsStateRecord _settingsState = CreateDefaultSettingsState();
    private AgentServiceStatusSnapshot? _lastStatusSnapshot;
    private AgentLiveSessionState _lastLiveSessionState = new(
        UpdatedAtUtc: null,
        SessionStartedAtUtc: null,
        LastInputEventUtc: null,
        AgentInstanceId: null,
        SessionId: null,
        KeyboardActive: false,
        MouseActive: false,
        UserActive: false,
        Idle: false,
        IdleSeconds: 0d,
        FocusAppHash: null,
        FocusStartedAtUtc: null,
        Freshness: "stale",
        DegradedReason: "live_session_missing");

    // Phase 1b v2 — label moment tracker: derives "activity resumed", "away segment just ended",
    // and "pressure crossed the threshold" moments for the label-prompt governor. All fields are
    // guarded by _gate and can only ever influence WHETHER a one-tap question is asked — never
    // nudges, pressure, or actuation.
    private DateTimeOffset? _labelPrevInputEventUtc;          // last input event we have processed
    private DateTimeOffset? _labelActivityResumeUtc;          // start of the current active span
    private DateTimeOffset? _labelAwayReturnUtc;              // when the last away segment ended (null = consumed/none)
    private double _labelLastAwayMinutes;                     // length of that away segment
    private double _labelPriorActiveMinutesAtAwayReturn;      // active span length before that away
    private double _labelLastPressureSample = -1;             // previous pressure sample (for crossing detection)
    private DateTimeOffset? _labelOverloadCrossingUtc;        // latched pressure-threshold crossing (null = consumed/none)
    private string _labelLastPromptKind = string.Empty;       // trigger context of the last shown prompt,
    private double _labelLastPromptSecondsSinceResume = -1;   //   stamped onto the answering label
    private DateTimeOffset? _labelLastPromptAtUtc;
    private AgentLatestStateDetails? _lastLatestStateDetails;
    private AgentNotificationSuppressionStatus _lastNotificationSuppressionStatus = DefaultNotificationSuppressionStatus;
    private AgentBrightnessActionStatus _lastBrightnessActionStatus = DefaultBrightnessActionStatus;
    private bool _lastDbMigrationFailed;
    private DateTimeOffset? _runtimeStateLastSuccessfulRefreshUtc;
    private DateTimeOffset? _runtimeStateLastAttemptUtc;
    private string? _runtimeStateRefreshError;
    private AgentCurrentSnapshotProjection? _lastCurrentSnapshotProjection;
    private long _runtimeStateRefreshSequence;
    private long _lastAppliedRuntimeStateRefreshSequence;

    public AgentCoreController()
        : this(
            new BucketFinalizerStateStore(new SqliteBrainStateStore()),
            new UserAgentSessionContextStore(),
            new FileRuntimePolicyProvider())
    {
    }

    public AgentCoreController(
        IBrainStateStore stateStore,
        IUserAgentSessionContextStore? userAgentSessionContextStore = null,
        IRuntimePolicyProvider? runtimePolicyProvider = null)
    {
        _stateStore = stateStore;
        _inputSignalsIngress = stateStore as IInputSignalsIngress;
        _userAgentSessionContextStore = userAgentSessionContextStore ?? new UserAgentSessionContextStore();
        _runtimePolicyProvider = runtimePolicyProvider ?? new FileRuntimePolicyProvider();
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

        RuntimeStateCacheRefreshResult initialRuntimeStateRefresh = ReadRuntimeStateCacheFromStore(
            DateTimeOffset.UtcNow,
            System.Threading.Interlocked.Increment(ref _runtimeStateRefreshSequence));
        ApplyRuntimeStateCacheRefreshLocked(initialRuntimeStateRefresh);
        _lastStatusSnapshot = CreateFallbackStatusSnapshot(DateTimeOffset.UtcNow);
        _lastCurrentSnapshotProjection = new AgentCurrentSnapshotProjection(_latestSnapshot, _lastLatestStateDetails, _lastStatusSnapshot);
    }

    public AgentServiceStatusSnapshot GetStatus()
    {
        if (System.Threading.Monitor.TryEnter(_gate, StatusReadLockTimeout))
        {
            try
            {
                return BuildAndCacheStatusSnapshotLocked();
            }
            finally
            {
                System.Threading.Monitor.Exit(_gate);
            }
        }

        return GetCachedStatusSnapshot();
    }

    public void SetWatchdogLaunchFailureCode(string? code)
    {
        lock (_gate)
        {
            _watchdogLaunchFailureCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        }
    }

    public void SetLocalApiAuthState(bool ready, string? errorCode)
    {
        _localApiAuthReady = ready;
        _localApiAuthError = ready
            ? null
            : string.IsNullOrWhiteSpace(errorCode)
                ? "TOKEN_READ_FAILED"
                : errorCode.Trim();
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
        if (System.Threading.Monitor.TryEnter(_gate, LatestStateReadLockTimeout))
        {
            try
            {
                return _latestSnapshot;
            }
            finally
            {
                System.Threading.Monitor.Exit(_gate);
            }
        }

        return _latestSnapshot;
    }

    public AgentLatestStateDetails? GetLatestStateDetails()
    {
        if (System.Threading.Monitor.TryEnter(_gate, LatestStateReadLockTimeout))
        {
            try
            {
                return _lastLatestStateDetails;
            }
            finally
            {
                System.Threading.Monitor.Exit(_gate);
            }
        }

        return _lastLatestStateDetails;
    }

    public AgentDistractionSummary GetCurrentDistractionSummary()
    {
        return _stateStore.ReadCurrentDistractionSummary();
    }

    public AgentCurrentSnapshotProjection GetCurrentSnapshotProjection()
    {
        if (System.Threading.Monitor.TryEnter(_gate, StatusReadLockTimeout))
        {
            try
            {
                AgentServiceStatusSnapshot status = BuildAndCacheStatusSnapshotLocked();
                return BuildCurrentSnapshotProjectionLocked(status);
            }
            finally
            {
                System.Threading.Monitor.Exit(_gate);
            }
        }

        return GetCachedCurrentSnapshotProjection();
    }

    public void RefreshRuntimeStateCache()
    {
        RuntimeStateCacheRefreshResult refresh = ReadRuntimeStateCacheFromStore(
            DateTimeOffset.UtcNow,
            System.Threading.Interlocked.Increment(ref _runtimeStateRefreshSequence));

        lock (_gate)
        {
            ApplyRuntimeStateCacheRefreshLocked(refresh);
        }
    }

    public AgentLiveSessionState GetLiveSessionState()
    {
        lock (_gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            bool hasUpdate = _lastLiveSessionState.UpdatedAtUtc.HasValue;
            bool stale = !hasUpdate
                || (nowUtc - _lastLiveSessionState.UpdatedAtUtc!.Value.ToUniversalTime()) > LiveSessionStateFreshnessWindow;
            DateTimeOffset? lastInputEventUtc = _lastLiveSessionState.LastInputEventUtc?.ToUniversalTime();
            bool userActive = lastInputEventUtc.HasValue
                && (nowUtc - lastInputEventUtc.Value) <= LiveSessionUserActiveWindow;
            bool idle = _lastLiveSessionState.IdleSeconds >= LiveSessionIdleThreshold.TotalSeconds;
            DateTimeOffset? focusStartedAtUtc = _lastLiveSessionState.FocusStartedAtUtc?.ToUniversalTime();
            if (focusStartedAtUtc.HasValue)
            {
                if (idle
                    || string.IsNullOrWhiteSpace(_lastLiveSessionState.FocusAppHash)
                    || focusStartedAtUtc.Value > nowUtc)
                {
                    focusStartedAtUtc = null;
                }
            }

            return _lastLiveSessionState with
            {
                UserActive = userActive,
                Idle = idle,
                FocusStartedAtUtc = focusStartedAtUtc,
                Freshness = stale ? "stale" : "fresh",
                DegradedReason = stale ? "live_session_stale" : null
            };
        }
    }

    private AgentServiceStatusSnapshot BuildAndCacheStatusSnapshotLocked()
    {
        TryAutoResumeLocked();
        UpdateRecalibrationStateLocked();
        AgentNotificationSuppressionStatus suppressionStatus = _lastNotificationSuppressionStatus;
        AgentBrightnessActionStatus brightnessStatus = _lastBrightnessActionStatus;
        AgentLatestStateDetails? latestState = _lastLatestStateDetails;

        string stateFlagsJson = latestState?.StateFlagsJson ?? _latestSnapshot.StateFlagsJson;
        bool overloadActive = latestState?.OverloadActive
            ?? (TryReadBoolFromStateFlags(stateFlagsJson, "overload_active", out bool parsedOverloadActive)
                && parsedOverloadActive);
        bool deepWorkActive = TryReadBoolFromStateFlags(stateFlagsJson, "deep_work_active", out bool parsedDeepWorkActive)
            && parsedDeepWorkActive;
        double pressureLevel = ReadDoubleFromStateFlags(stateFlagsJson, "pressure_level", _latestSnapshot.OverloadLikelihood);
        double pressureConfidence = ReadDoubleFromStateFlags(stateFlagsJson, "model_confidence", _latestSnapshot.Confidence);
        long deepWorkStartedAtUtcMs = ReadInt64FromStateFlags(stateFlagsJson, "deep_work_started_at_utc_ms");
        double fallbackSustainedAttentionSeconds = latestState?.SustainedAttentionDurationSec ?? _latestSnapshot.SustainedAttentionDurationSec;
        int deepWorkStreakSeconds = ComputeDeepWorkStreakSeconds(
            deepWorkActive,
            deepWorkStartedAtUtcMs,
            fallbackSustainedAttentionSeconds);
        PresentedVisualMode visualMode = PresentedVisualModeMapper.Map(overloadActive, deepWorkActive);
        string? latestContributorsJson = latestState?.ContributorsJson;
        string? latestWhyJson = _lastStatusSnapshot?.LatestWhyJson;
        bool baselineReady = latestState?.BaselineReady ?? false;
        bool coldStart = latestState?.ColdStart ?? false;
        string calibrationStage = latestState?.CalibrationStage
            ?? (baselineReady ? CalibrationTruth.StageCalibrated : (coldStart ? CalibrationTruth.StageCold : CalibrationTruth.StageProvisional));
        bool systemActionsEnabled = latestState?.SystemActionsEnabled ?? CalibrationTruth.AllowsSystemActions(calibrationStage);
        string trustTier = latestState?.TrustTier ?? CalibrationTruth.ResolveTrustTier(
            calibrationStage,
            latestState?.Confidence ?? _latestSnapshot.Confidence,
            latestState?.CurrentQualityScore ?? 0d,
            latestState?.PartialData == true);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        UserAgentSessionContextSnapshot? observedSessionContext = _userAgentSessionContextStore.ReadCurrent();
        string captureContext = ResolveCaptureContextLocked(nowUtc, out UserAgentSessionContextSnapshot? sessionContext);
        ReconcileStaleLastAcceptedOwnershipLocked(nowUtc, sessionContext);
        bool keyboardSignalActive = IsSignalActive(_lastKeyboardSignalUtc, nowUtc);
        bool mouseSignalActive = IsSignalActive(_lastMouseSignalUtc, nowUtc);
        string? userAgentInstanceId = NormalizeIdentifier(sessionContext?.AgentInstanceId);
        int? userAgentProcessId = sessionContext?.ProcessId > 0 ? sessionContext.ProcessId : null;
        string? userAgentSessionId = sessionContext is not null
            ? BuildSessionOwnerId(sessionContext.SessionId)
            : null;
        DateTimeOffset? userAgentHeartbeatUpdatedAtUtc = sessionContext?.UpdatedAtUtc;
        DateTimeOffset? userAgentObservedHeartbeatUpdatedAtUtc = observedSessionContext?.UpdatedAtUtc;
        string? userAgentObservedCaptureContext = NormalizeObservedCaptureContext(observedSessionContext?.CaptureContext);
        string? userAgentObservedSessionId = observedSessionContext is not null
            ? BuildSessionOwnerId(observedSessionContext.SessionId)
            : null;
        string? userAgentStartupSource = NormalizeIdentifier(observedSessionContext?.StartupSource);
        string? userAgentLaunchReason = NormalizeIdentifier(observedSessionContext?.LaunchReason);
        int? userAgentHeartbeatWriteFailureCount = NormalizeHeartbeatWriteFailureCount(observedSessionContext?.HeartbeatWriteFailureCount);
        string? captureSelfTestState = NormalizeCaptureSelfTestState(observedSessionContext?.CaptureSelfTestState);
        string? captureSelfTestErrorCode = NormalizeIdentifier(observedSessionContext?.CaptureSelfTestErrorCode);
        int? captureSelfTestRemediationCount = NormalizeHeartbeatWriteFailureCount(observedSessionContext?.CaptureSelfTestRemediationCount);
        string? userAgentCaptureFailureCode = NormalizeIdentifier(observedSessionContext?.CaptureFailureCode);
        string? ownershipWarning = ResolveOwnershipWarning(sessionContext);
        string? captureFailureReason = ResolveCaptureFailureReasonLocked(
            captureContext,
            observedSessionContext,
            ownershipWarning,
            captureSelfTestState);
        string? effectiveLastError = ResolveEffectiveLastError(
            _lastError,
            _latestSnapshot.StateFlagsJson,
            captureSelfTestState,
            captureSelfTestErrorCode);
        bool dbMigrationFailedFromStore = _lastDbMigrationFailed;
        bool dbMigrationFailed = IsDbMigrationFailure(effectiveLastError) || dbMigrationFailedFromStore;
        AgentStatus status = effectiveLastError is null
            ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
            : AgentStatus.Degraded;
        string policyVersion = ResolvePolicyVersion();
        (string runtimeStateFreshness, DateTimeOffset? runtimeStateLastSuccessfulRefreshUtc, DateTimeOffset? runtimeStateLastAttemptUtc, string? runtimeStateRefreshError) =
            GetRuntimeStateFreshnessMetadataLocked(nowUtc);

        AgentServiceStatusSnapshot snapshot = new(
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
            PolicyVersion: policyVersion,
            LastEventTimestampUtc: _lastEventTimestampUtc,
            BackendInstanceId: _backendInstanceId,
            BackendProcessId: Environment.ProcessId,
            CaptureContext: captureContext,
            UserAgentHeartbeatUpdatedAtUtc: userAgentHeartbeatUpdatedAtUtc,
            UserAgentInstanceId: userAgentInstanceId,
            UserAgentProcessId: userAgentProcessId,
            UserAgentSessionId: userAgentSessionId,
            KeyboardSignalActive: keyboardSignalActive,
            MouseSignalActive: mouseSignalActive,
            LastInputEventUtc: _lastInputEventUtc,
            LastInputAgentInstanceId: _lastInputAgentInstanceId,
            LastInputSessionId: _lastInputSessionId,
            OwnershipWarning: ownershipWarning,
            CaptureFailureReason: captureFailureReason,
            LastError: effectiveLastError,
            LocalApiAuthReady: _localApiAuthReady,
            LocalApiAuthError: _localApiAuthError,
            NotificationSuppressionActive: suppressionStatus.Active,
            NotificationSuppressionReady: suppressionStatus.Ready,
            NotificationSuppressionLastError: suppressionStatus.LastError,
            NotificationSuppressionError: suppressionStatus.LastError,
            NotificationSuppressionBackoffUntilUtc: suppressionStatus.BackoffUntilUtc,
            NotificationBackoffUntilUtc: suppressionStatus.BackoffUntilUtc,
            NotificationsSuppressed: suppressionStatus.Active,
            BrightnessActionReady: brightnessStatus.ActionReady,
            BrightnessActionActive: brightnessStatus.Active,
            BrightnessAdjusted: brightnessStatus.Active,
            BrightnessSupported: brightnessStatus.Supported,
            BrightnessCurrentLevel: brightnessStatus.CurrentLevel,
            BrightnessTargetLevel: brightnessStatus.TargetLevel,
            BrightnessLastError: brightnessStatus.LastError,
            BrightnessError: brightnessStatus.LastError,
            BrightnessBackoffUntilUtc: brightnessStatus.BackoffUntilUtc,
            DbMigrationFailed: dbMigrationFailed,
            BaselineReady: baselineReady,
            ColdStart: coldStart,
            CalibrationStage: calibrationStage,
            SystemActionsEnabled: systemActionsEnabled,
            TrustTier: trustTier,
            OverloadActive: overloadActive,
            DeepWorkActive: deepWorkActive,
            DeepWorkStreakSeconds: deepWorkStreakSeconds,
            VisualMode: PresentedVisualModeMapper.ToToken(visualMode),
            LatestContributorsJson: latestContributorsJson,
            LatestWhyJson: latestWhyJson,
            LastUpdatedUtc: nowUtc,
            UserAgentObservedHeartbeatUpdatedAtUtc: userAgentObservedHeartbeatUpdatedAtUtc,
            UserAgentObservedCaptureContext: userAgentObservedCaptureContext,
            UserAgentObservedSessionId: userAgentObservedSessionId,
            UserAgentStartupSource: userAgentStartupSource,
            UserAgentLaunchReason: userAgentLaunchReason,
            UserAgentHeartbeatWriteFailureCount: userAgentHeartbeatWriteFailureCount,
            CaptureSelfTestState: captureSelfTestState,
            CaptureSelfTestErrorCode: captureSelfTestErrorCode,
            CaptureSelfTestRemediationCount: captureSelfTestRemediationCount,
            RuntimeStateFreshness: runtimeStateFreshness,
            RuntimeStateLastSuccessfulRefreshUtc: runtimeStateLastSuccessfulRefreshUtc,
            RuntimeStateLastAttemptUtc: runtimeStateLastAttemptUtc,
            RuntimeStateRefreshError: runtimeStateRefreshError,
            PressureLevel: pressureLevel,
            PressureConfidence: pressureConfidence,
            UserAgentCaptureFailureCode: userAgentCaptureFailureCode,
            WatchdogLaunchFailureCode: _watchdogLaunchFailureCode,
            UserAgentSelfTestState: captureSelfTestState,
            UserAgentSelfTestErrorCode: captureSelfTestErrorCode);

        _lastStatusSnapshot = snapshot;
        BuildCurrentSnapshotProjectionLocked(snapshot);
        return snapshot;
    }

    private AgentServiceStatusSnapshot GetCachedStatusSnapshot()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (_lastStatusSnapshot is AgentServiceStatusSnapshot cached)
        {
            AgentStatus status = _lastError is null
                ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
                : AgentStatus.Degraded;
            string calibrationStage = _lastLatestStateDetails?.CalibrationStage
                ?? (_lastLatestStateDetails?.BaselineReady == true
                    ? CalibrationTruth.StageCalibrated
                    : (_lastLatestStateDetails?.ColdStart == true ? CalibrationTruth.StageCold : CalibrationTruth.StageProvisional));
            string trustTier = _lastLatestStateDetails?.TrustTier
                ?? CalibrationTruth.ResolveTrustTier(
                    calibrationStage,
                    _lastLatestStateDetails?.Confidence ?? _latestSnapshot.Confidence,
                    _lastLatestStateDetails?.CurrentQualityScore ?? 0d,
                    _lastLatestStateDetails?.PartialData == true);
            bool overloadActive = _lastLatestStateDetails?.OverloadActive
                ?? (TryReadBoolFromStateFlags(_latestSnapshot.StateFlagsJson, "overload_active", out bool parsedOverloadActive)
                    && parsedOverloadActive);
            bool deepWorkActive = TryReadBoolFromStateFlags(_latestSnapshot.StateFlagsJson, "deep_work_active", out bool parsedDeepWorkActive)
                && parsedDeepWorkActive;
            (string runtimeStateFreshness, DateTimeOffset? runtimeStateLastSuccessfulRefreshUtc, DateTimeOffset? runtimeStateLastAttemptUtc, string? runtimeStateRefreshError) =
                GetRuntimeStateFreshnessMetadataLocked(nowUtc);
            return cached with
            {
                Status = status,
                Paused = _paused,
                PauseUntilUtc = _pauseUntilUtc,
                ConsentActive = _consentActive,
                ConsentVersion = _consentState.Version,
                ConsentTimestampUtc = _consentState.TimestampUtc,
                ConsentDisclosureHash = _consentState.DisclosureHash,
                BackendConnected = _backendConnected,
                FocusStrength = _latestSnapshot.FocusStrength,
                OverloadLikelihood = _latestSnapshot.OverloadLikelihood,
                LastAction = _latestSnapshot.LastAction,
                LastEventTimestampUtc = _lastEventTimestampUtc,
                LastError = _lastError,
                LocalApiAuthReady = _localApiAuthReady,
                LocalApiAuthError = _localApiAuthError,
                NotificationSuppressionActive = _lastNotificationSuppressionStatus.Active,
                NotificationSuppressionReady = _lastNotificationSuppressionStatus.Ready,
                NotificationSuppressionLastError = _lastNotificationSuppressionStatus.LastError,
                NotificationSuppressionError = _lastNotificationSuppressionStatus.LastError,
                NotificationSuppressionBackoffUntilUtc = _lastNotificationSuppressionStatus.BackoffUntilUtc,
                NotificationBackoffUntilUtc = _lastNotificationSuppressionStatus.BackoffUntilUtc,
                NotificationsSuppressed = _lastNotificationSuppressionStatus.Active,
                BrightnessActionReady = _lastBrightnessActionStatus.ActionReady,
                BrightnessActionActive = _lastBrightnessActionStatus.Active,
                BrightnessAdjusted = _lastBrightnessActionStatus.Active,
                BrightnessSupported = _lastBrightnessActionStatus.Supported,
                BrightnessCurrentLevel = _lastBrightnessActionStatus.CurrentLevel,
                BrightnessTargetLevel = _lastBrightnessActionStatus.TargetLevel,
                BrightnessLastError = _lastBrightnessActionStatus.LastError,
                BrightnessError = _lastBrightnessActionStatus.LastError,
                BrightnessBackoffUntilUtc = _lastBrightnessActionStatus.BackoffUntilUtc,
                DbMigrationFailed = _lastDbMigrationFailed,
                BaselineReady = _lastLatestStateDetails?.BaselineReady ?? cached.BaselineReady,
                ColdStart = _lastLatestStateDetails?.ColdStart ?? cached.ColdStart,
                CalibrationStage = calibrationStage,
                SystemActionsEnabled = _lastLatestStateDetails?.SystemActionsEnabled ?? CalibrationTruth.AllowsSystemActions(calibrationStage),
                TrustTier = trustTier,
                OverloadActive = overloadActive,
                DeepWorkActive = deepWorkActive,
                LatestContributorsJson = _lastLatestStateDetails?.ContributorsJson ?? cached.LatestContributorsJson,
                RuntimeStateFreshness = runtimeStateFreshness,
                RuntimeStateLastSuccessfulRefreshUtc = runtimeStateLastSuccessfulRefreshUtc,
                RuntimeStateLastAttemptUtc = runtimeStateLastAttemptUtc,
                RuntimeStateRefreshError = runtimeStateRefreshError,
                LastUpdatedUtc = nowUtc
            };
        }

        return CreateFallbackStatusSnapshot(nowUtc);
    }

    private AgentCurrentSnapshotProjection BuildCurrentSnapshotProjectionLocked(AgentServiceStatusSnapshot status)
    {
        AgentCurrentSnapshotProjection projection = new(_latestSnapshot, _lastLatestStateDetails, status);
        _lastCurrentSnapshotProjection = projection;
        return projection;
    }

    private AgentCurrentSnapshotProjection GetCachedCurrentSnapshotProjection()
    {
        if (_lastCurrentSnapshotProjection is AgentCurrentSnapshotProjection cached)
        {
            return cached;
        }

        return new AgentCurrentSnapshotProjection(_latestSnapshot, _lastLatestStateDetails, GetCachedStatusSnapshot());
    }

    private AgentServiceStatusSnapshot CreateFallbackStatusSnapshot(DateTimeOffset nowUtc)
    {
        AgentStatus status = _lastError is null
            ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
            : AgentStatus.Degraded;
        string policyVersion = _lastStatusSnapshot?.PolicyVersion ?? ResolvePolicyVersion();
        string calibrationStage = _lastLatestStateDetails?.CalibrationStage
            ?? (_lastLatestStateDetails?.BaselineReady == true
                ? CalibrationTruth.StageCalibrated
                : (_lastLatestStateDetails?.ColdStart == true ? CalibrationTruth.StageCold : CalibrationTruth.StageProvisional));
        string trustTier = _lastLatestStateDetails?.TrustTier
            ?? CalibrationTruth.ResolveTrustTier(
                calibrationStage,
                _lastLatestStateDetails?.Confidence ?? _latestSnapshot.Confidence,
                _lastLatestStateDetails?.CurrentQualityScore ?? 0d,
                _lastLatestStateDetails?.PartialData == true);
        (string runtimeStateFreshness, DateTimeOffset? runtimeStateLastSuccessfulRefreshUtc, DateTimeOffset? runtimeStateLastAttemptUtc, string? runtimeStateRefreshError) =
            GetRuntimeStateFreshnessMetadataLocked(nowUtc);

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
            PolicyVersion: policyVersion,
            LastEventTimestampUtc: _lastEventTimestampUtc,
            BackendInstanceId: _backendInstanceId,
            BackendProcessId: Environment.ProcessId,
            CaptureContext: _lastStatusSnapshot?.CaptureContext ?? _captureContext,
            UserAgentHeartbeatUpdatedAtUtc: _lastStatusSnapshot?.UserAgentHeartbeatUpdatedAtUtc,
            UserAgentInstanceId: _lastStatusSnapshot?.UserAgentInstanceId,
            UserAgentProcessId: _lastStatusSnapshot?.UserAgentProcessId,
            UserAgentSessionId: _lastStatusSnapshot?.UserAgentSessionId,
            KeyboardSignalActive: _lastStatusSnapshot?.KeyboardSignalActive ?? false,
            MouseSignalActive: _lastStatusSnapshot?.MouseSignalActive ?? false,
            LastInputEventUtc: _lastInputEventUtc,
            LastInputAgentInstanceId: _lastInputAgentInstanceId,
            LastInputSessionId: _lastInputSessionId,
            OwnershipWarning: _lastStatusSnapshot?.OwnershipWarning,
            CaptureFailureReason: _lastStatusSnapshot?.CaptureFailureReason,
            LastError: _lastError,
            LocalApiAuthReady: _localApiAuthReady,
            LocalApiAuthError: _localApiAuthError,
            NotificationSuppressionActive: _lastNotificationSuppressionStatus.Active,
            NotificationSuppressionReady: _lastNotificationSuppressionStatus.Ready,
            NotificationSuppressionLastError: _lastNotificationSuppressionStatus.LastError,
            NotificationSuppressionError: _lastNotificationSuppressionStatus.LastError,
            NotificationSuppressionBackoffUntilUtc: _lastNotificationSuppressionStatus.BackoffUntilUtc,
            NotificationBackoffUntilUtc: _lastNotificationSuppressionStatus.BackoffUntilUtc,
            NotificationsSuppressed: _lastNotificationSuppressionStatus.Active,
            BrightnessActionReady: _lastBrightnessActionStatus.ActionReady,
            BrightnessActionActive: _lastBrightnessActionStatus.Active,
            BrightnessAdjusted: _lastBrightnessActionStatus.Active,
            BrightnessSupported: _lastBrightnessActionStatus.Supported,
            BrightnessCurrentLevel: _lastBrightnessActionStatus.CurrentLevel,
            BrightnessTargetLevel: _lastBrightnessActionStatus.TargetLevel,
            BrightnessLastError: _lastBrightnessActionStatus.LastError,
            BrightnessError: _lastBrightnessActionStatus.LastError,
            BrightnessBackoffUntilUtc: _lastBrightnessActionStatus.BackoffUntilUtc,
            DbMigrationFailed: _lastDbMigrationFailed,
            BaselineReady: _lastLatestStateDetails?.BaselineReady ?? false,
            ColdStart: _lastLatestStateDetails?.ColdStart ?? false,
            CalibrationStage: calibrationStage,
            SystemActionsEnabled: _lastLatestStateDetails?.SystemActionsEnabled ?? CalibrationTruth.AllowsSystemActions(calibrationStage),
            TrustTier: trustTier,
            OverloadActive: _lastLatestStateDetails?.OverloadActive
                ?? (TryReadBoolFromStateFlags(_latestSnapshot.StateFlagsJson, "overload_active", out bool overloadActive) && overloadActive),
            DeepWorkActive: TryReadBoolFromStateFlags(_latestSnapshot.StateFlagsJson, "deep_work_active", out bool deepWorkActive) && deepWorkActive,
            DeepWorkStreakSeconds: _lastStatusSnapshot?.DeepWorkStreakSeconds ?? 0,
            VisualMode: _lastStatusSnapshot?.VisualMode ?? PresentedVisualModeMapper.ToToken(PresentedVisualMode.NORMAL),
            LatestContributorsJson: _lastLatestStateDetails?.ContributorsJson ?? _lastStatusSnapshot?.LatestContributorsJson,
            LatestWhyJson: _lastStatusSnapshot?.LatestWhyJson,
            LastUpdatedUtc: nowUtc,
            UserAgentObservedHeartbeatUpdatedAtUtc: _lastStatusSnapshot?.UserAgentObservedHeartbeatUpdatedAtUtc,
            UserAgentObservedCaptureContext: _lastStatusSnapshot?.UserAgentObservedCaptureContext,
            UserAgentObservedSessionId: _lastStatusSnapshot?.UserAgentObservedSessionId,
            UserAgentStartupSource: _lastStatusSnapshot?.UserAgentStartupSource,
            UserAgentLaunchReason: _lastStatusSnapshot?.UserAgentLaunchReason,
            UserAgentHeartbeatWriteFailureCount: _lastStatusSnapshot?.UserAgentHeartbeatWriteFailureCount,
            CaptureSelfTestState: _lastStatusSnapshot?.CaptureSelfTestState,
            CaptureSelfTestErrorCode: _lastStatusSnapshot?.CaptureSelfTestErrorCode,
            CaptureSelfTestRemediationCount: _lastStatusSnapshot?.CaptureSelfTestRemediationCount,
            RuntimeStateFreshness: runtimeStateFreshness,
            RuntimeStateLastSuccessfulRefreshUtc: runtimeStateLastSuccessfulRefreshUtc,
            RuntimeStateLastAttemptUtc: runtimeStateLastAttemptUtc,
            RuntimeStateRefreshError: runtimeStateRefreshError,
            PressureLevel: ReadDoubleFromStateFlags(_latestSnapshot.StateFlagsJson, "pressure_level", _latestSnapshot.OverloadLikelihood),
            PressureConfidence: ReadDoubleFromStateFlags(_latestSnapshot.StateFlagsJson, "model_confidence", _latestSnapshot.Confidence));
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

    public IReadOnlyList<AgentPendingIntervention> GetPendingInterventions(string sessionId, int limit = 20)
    {
        string normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            throw new ArgumentException("missing_session_id", nameof(sessionId));
        }

        lock (_gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            UserAgentSessionContextSnapshot? sessionContext = ReadFreshUserAgentSessionContextLocked(nowUtc);
            if (sessionContext is not null)
            {
                string heartbeatSessionId = BuildSessionOwnerId(sessionContext.SessionId);
                if (!string.Equals(heartbeatSessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(InputSignalsSourceSessionMismatch);
                }
            }

            return _stateStore.ReadPendingInterventions(normalizedSessionId, Math.Clamp(limit, 1, 50));
        }
    }

    public void PublishLiveSessionState(AgentLiveSessionSignalPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        string agentInstanceId = packet.AgentInstanceId?.Trim() ?? string.Empty;
        if (!IsValidIdentifier(agentInstanceId))
        {
            throw new ArgumentException("missing_agent_instance_id", nameof(packet.AgentInstanceId));
        }

        string sessionId = packet.SessionId?.Trim() ?? string.Empty;
        if (!IsValidIdentifier(sessionId))
        {
            throw new ArgumentException("missing_session_id", nameof(packet.SessionId));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset updatedAtUtc = packet.UpdatedAtUtc.ToUniversalTime();
        DateTimeOffset sessionStartedAtUtc = packet.SessionStartedAtUtc.ToUniversalTime();
        if (sessionStartedAtUtc > updatedAtUtc)
        {
            sessionStartedAtUtc = updatedAtUtc;
        }

        if ((updatedAtUtc - sessionStartedAtUtc) > TimeSpan.FromDays(7))
        {
            sessionStartedAtUtc = updatedAtUtc.AddDays(-7);
        }

          DateTimeOffset? lastInputEventUtc = packet.LastInputEventUtc?.ToUniversalTime();
        if (lastInputEventUtc.HasValue && lastInputEventUtc.Value > updatedAtUtc)
        {
            lastInputEventUtc = updatedAtUtc;
        }

          string focusAppHash = NormalizeFocusHash(packet.FocusAppHash);
          if (!IsValidFocusHash(focusAppHash))
          {
              throw new ArgumentException("invalid_focus_app_hash", nameof(packet.FocusAppHash));
          }

          DateTimeOffset? focusStartedAtUtc = packet.FocusStartedAtUtc?.ToUniversalTime();
          if (focusStartedAtUtc.HasValue)
          {
              if (focusStartedAtUtc.Value > updatedAtUtc)
              {
                  focusStartedAtUtc = updatedAtUtc;
              }

              if (focusStartedAtUtc.Value < sessionStartedAtUtc)
              {
                  focusStartedAtUtc = sessionStartedAtUtc;
              }
          }

          double idleSeconds = ClampNonNegativeDouble(packet.IdleSeconds, 1800d);

        lock (_gate)
        {
            TryAutoResumeLocked();
            ValidateInputSignalSourceLocked(
                nowUtc,
                agentInstanceId,
                sessionId,
                packet.HeartbeatSequence,
                packet.HeartbeatUpdatedAtUtc,
                packet.HeartbeatCaptureContext);
            _lastLiveSessionState = new AgentLiveSessionState(
                UpdatedAtUtc: updatedAtUtc,
                SessionStartedAtUtc: sessionStartedAtUtc,
                LastInputEventUtc: lastInputEventUtc,
                AgentInstanceId: agentInstanceId,
                SessionId: sessionId,
                KeyboardActive: packet.KeyboardActive,
                MouseActive: packet.MouseActive,
                UserActive: false,
                Idle: false,
                IdleSeconds: idleSeconds,
                FocusAppHash: string.IsNullOrWhiteSpace(focusAppHash) ? null : focusAppHash,
                FocusStartedAtUtc: focusStartedAtUtc,
                Freshness: "fresh",
                DegradedReason: null);
            UpdateLabelMomentTrackerLocked(lastInputEventUtc);
          }
      }

    // Phase 1b v2 — moment tracking off the live input stream. Packets arrive every couple of
    // seconds; LastInputEventUtc only advances when the user actually does something, so a gap
    // between consecutive DISTINCT input timestamps is exactly an away segment.
    private void UpdateLabelMomentTrackerLocked(DateTimeOffset? lastInputEventUtc)
    {
        if (lastInputEventUtc is not DateTimeOffset lastInput)
        {
            return;
        }

        if (_labelPrevInputEventUtc is not DateTimeOffset prevInput)
        {
            // First observed input since service start: begin an active span. Deliberately NOT an
            // away-return event — a fresh boot/login must never trigger the away-return question.
            _labelPrevInputEventUtc = lastInput;
            _labelActivityResumeUtc = lastInput;
            return;
        }

        if (lastInput <= prevInput)
        {
            return; // no new input since the last packet
        }

        double gapMinutes = (lastInput - prevInput).TotalMinutes;
        if (gapMinutes >= PortaExecutionConstants.LabelCapture.AwayMinMinutes)
        {
            // An away segment just ended. Record it for the governor's away-return window and
            // start a new active span (which also resets the warm-up gate).
            _labelPriorActiveMinutesAtAwayReturn = _labelActivityResumeUtc is DateTimeOffset resume
                ? Math.Max(0d, (prevInput - resume).TotalMinutes)
                : 0d;
            _labelLastAwayMinutes = gapMinutes;
            _labelAwayReturnUtc = lastInput;
            _labelActivityResumeUtc = lastInput;
            // A pressure crossing latched before the away is stale evidence — drop it.
            _labelOverloadCrossingUtc = null;
        }

        _labelPrevInputEventUtc = lastInput;
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

    public IReadOnlyDictionary<string, double> GetFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc)
        {
            return new Dictionary<string, double>();
        }

        return _stateStore.ReadFocusAppDwellSeconds(fromUtc, toUtc);
    }

    /// <summary>
    /// Digest redesign — the day's shape by local hour, for the email's timeline strip. The local
    /// offset comes from the machine's own clock, matching how the goal day is resolved everywhere
    /// else in this controller.
    /// </summary>
    public IReadOnlyList<AgentDayTimelineHour> GetDayTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => toUtc <= fromUtc
            ? Array.Empty<AgentDayTimelineHour>()
            : _stateStore.ReadDayTimeline(fromUtc, toUtc, DateTimeOffset.Now.Offset);

    public void IngestInputSignals(InputSignalsPacket packet)
    {
        if (packet is null)
        {
            throw new ArgumentNullException(nameof(packet));
        }

        int schemaVer = packet.SchemaVer;
        if (schemaVer is not 1 and not 2)
        {
            throw new ArgumentException("unsupported_schema_ver", nameof(packet));
        }

        string agentInstanceId = packet.AgentInstanceId?.Trim() ?? string.Empty;
        if (!IsValidIdentifier(agentInstanceId))
        {
            throw new ArgumentException("missing_agent_instance_id", nameof(packet.AgentInstanceId));
        }

        string sessionId = packet.SessionId?.Trim() ?? string.Empty;
        if (!IsValidIdentifier(sessionId))
        {
            throw new ArgumentException("missing_session_id", nameof(packet.SessionId));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset windowEndUtc = packet.WindowEndUtc.GetValueOrDefault(nowUtc).ToUniversalTime();
        DateTimeOffset windowStartUtc = (packet.WindowStartUtc ?? windowEndUtc).ToUniversalTime();
        if (windowEndUtc < windowStartUtc)
        {
            (windowEndUtc, windowStartUtc) = (windowStartUtc, windowEndUtc);
        }

        if ((windowEndUtc - windowStartUtc).Duration() > TimeSpan.FromMinutes(10))
        {
            windowEndUtc = windowStartUtc.AddMinutes(10);
        }

        DateTimeOffset fallbackEventUtc = windowEndUtc;
        if (packet.LastInputEventUtc is not null && packet.LastInputEventUtc.Value.ToUniversalTime() > fallbackEventUtc)
        {
            fallbackEventUtc = packet.LastInputEventUtc.Value.ToUniversalTime();
        }

        string focusAppHash = NormalizeFocusHash(packet.FocusAppHash);
        if (!IsValidFocusHash(focusAppHash))
        {
            throw new ArgumentException("invalid_focus_app_hash", nameof(packet.FocusAppHash));
        }

        // Category bridge (v1.2.112): register the UserAgent-resolved category/label/meeting
        // metadata for this hash into THIS process's classifier registries, BEFORE the bucket
        // pipeline can evaluate the hash. The registries are static and per-process; without
        // this the service brain saw every app as Unknown and the category-aware rules
        // (2f off-task, 2g return-loop, 2b meeting shield) never fired. Idempotent and sticky;
        // re-registering on every request also self-heals after a service restart. Undefined
        // category values are ignored, never thrown on.
        if (!string.IsNullOrWhiteSpace(focusAppHash))
        {
            if (packet.FocusAppCategory != 0
                && Enum.IsDefined(typeof(Porta.Agent.Common.Analysis.AppCategory), packet.FocusAppCategory))
            {
                Porta.Agent.Common.Analysis.AppCategoryClassifier.RegisterHashCategory(
                    focusAppHash, (Porta.Agent.Common.Analysis.AppCategory)packet.FocusAppCategory);
            }

            if (!string.IsNullOrWhiteSpace(packet.FocusAppLabel))
            {
                Porta.Agent.Common.Analysis.AppCategoryClassifier.RegisterHashLabel(
                    focusAppHash, packet.FocusAppLabel);
            }

            if (packet.FocusAppIsMeeting)
            {
                Porta.Agent.Common.Analysis.AppCategoryClassifier.RegisterHashMeeting(focusAppHash);
            }
        }
        int keyboardKeydownCount = ClampNonNegativeInt(packet.KeyboardKeydownCount, 200);
        int keyboardBackspaceCount = ClampNonNegativeInt(packet.KeyboardBackspaceCount, 200);
        int mouseWheelDelta = ClampFiniteInt(packet.MouseWheelDelta, -2000, 2000);
        double mouseMoveDx = ClampFiniteDouble(packet.MouseMoveDx, -8192d, 8192d);
        double mouseMoveDy = ClampFiniteDouble(packet.MouseMoveDy, -8192d, 8192d);
        double idleSeconds = ClampNonNegativeDouble(packet.IdleSeconds, 1800d);
        double keyboardIkiMeanMs = ClampNonNegativeNullableDouble(packet.KeyboardIkiMeanMs, 5000d);
        double keyboardIkiStdMs = ClampNonNegativeNullableDouble(packet.KeyboardIkiStdMs, 5000d);
        double keyboardIkiCv = ClampNonNegativeNullableDouble(packet.KeyboardIkiCv, 10d);
        int keyboardBurstCount = ClampNonNegativeInt(packet.KeyboardBurstCount, 500);
        double keyboardBurstRate = ClampNonNegativeNullableDouble(packet.KeyboardBurstRate, 100d);
        int keyboardCorrectionSpikeCount = ClampNonNegativeInt(packet.KeyboardCorrectionSpikeCount, 500);
        double keyboardCorrectionSpikeRate = ClampNonNegativeNullableDouble(packet.KeyboardCorrectionSpikeRate, 100d);
        int mouseMoveSampleCount = ClampNonNegativeInt(packet.MouseMoveSampleCount, 5000);
        double mousePathLengthPx = ClampNonNegativeNullableDouble(packet.MousePathLengthPx, 100000d);
        double mouseSpeedMeanPxPerSec = ClampNonNegativeNullableDouble(packet.MouseSpeedMeanPxPerSec, 100000d);
        double mouseJerkP95 = ClampNonNegativeNullableDouble(packet.MouseJerkP95, 1000000d);
        int mouseMicroCorrectionCount = ClampNonNegativeInt(packet.MouseMicroCorrectionCount, 5000);
        double mouseMicroCorrectionRate = ClampNonNegativeNullableDouble(packet.MouseMicroCorrectionRate, 1000d);
        int mouseWheelEventCount = ClampNonNegativeInt(packet.MouseWheelEventCount, 5000);
        int mouseScrollBurstCount = ClampNonNegativeInt(packet.MouseScrollBurstCount, 5000);
        double mouseScrollBurstRate = ClampNonNegativeNullableDouble(packet.MouseScrollBurstRate, 1000d);
        double mouseScrollSpeedMean = ClampNonNegativeNullableDouble(packet.MouseScrollSpeedMean, 100000d);
        int focusObservationCount = ClampNonNegativeInt(packet.FocusObservationCount, 600);
        int focusSwitchCount = ClampNonNegativeInt(packet.FocusSwitchCount, 200);
        int focusActiveAppCount = ClampNonNegativeInt(packet.FocusActiveAppCount, 200);
        double focusAppEntropy = ClampNonNegativeNullableDouble(packet.FocusAppEntropy, 10d);
        int idleObservationCount = ClampNonNegativeInt(packet.IdleObservationCount, 600);
        double idleSecondsWithinWindow = ClampNonNegativeNullableDouble(packet.IdleSecondsWithinWindow, 1800d);
        int microIdleCount = ClampNonNegativeInt(packet.MicroIdleCount, 200);
        double microIdleSeconds = ClampNonNegativeNullableDouble(packet.MicroIdleSeconds, 1800d);
        double breakSecondsWithinWindow = ClampNonNegativeNullableDouble(packet.BreakSecondsWithinWindow, 1800d);

        InputSignalsPacket? normalizedPacket = null;

        lock (_gate)
        {
            TryAutoResumeLocked();
            ValidateInputSignalSourceLocked(
                nowUtc,
                agentInstanceId,
                sessionId,
                packet.HeartbeatSequence,
                packet.HeartbeatUpdatedAtUtc,
                packet.HeartbeatCaptureContext);
            _lastInputAgentInstanceId = agentInstanceId;
            _lastInputSessionId = sessionId;
            // Only advance the last-input timestamp when the packet carries REAL captured
            // input. The old fallback (windowEndUtc) fabricated fresh "last activity" values
            // for packets with zero input, masking dead capture pipelines fleet-wide.
            bool packetHasRealInput = keyboardKeydownCount > 0
                || keyboardBackspaceCount > 0
                || mouseWheelDelta != 0
                || mouseMoveDx is not 0d
                || mouseMoveDy is not 0d;
            if (packet.LastInputEventUtc.HasValue && packetHasRealInput)
            {
                _lastInputEventUtc = packet.LastInputEventUtc.Value.ToUniversalTime();
            }

            if (keyboardKeydownCount > 0 || keyboardBackspaceCount > 0)
            {
                _lastKeyboardSignalUtc = packet.LastInputEventUtc?.ToUniversalTime() ?? fallbackEventUtc;
            }

            if (mouseWheelDelta != 0 || mouseMoveDx is not 0d || mouseMoveDy is not 0d)
            {
                _lastMouseSignalUtc = packet.LastInputEventUtc?.ToUniversalTime() ?? fallbackEventUtc;
            }

            if (_paused || !_consentActive)
            {
                _lastEventTimestampUtc = fallbackEventUtc;
                return;
            }

            if (_inputSignalsIngress is null)
            {
                throw new InvalidOperationException("input_signals_ingress_unavailable");
            }

            normalizedPacket = new InputSignalsPacket(
                SchemaVer: schemaVer,
                AgentInstanceId: agentInstanceId,
                SessionId: sessionId,
                WindowStartUtc: windowStartUtc,
                WindowEndUtc: windowEndUtc,
                KeyboardKeydownCount: keyboardKeydownCount,
                KeyboardBackspaceCount: keyboardBackspaceCount,
                MouseWheelDelta: mouseWheelDelta,
                MouseMoveDx: mouseMoveDx,
                MouseMoveDy: mouseMoveDy,
                FocusAppHash: string.IsNullOrWhiteSpace(focusAppHash) ? null : focusAppHash,
                IdleSeconds: idleSeconds,
                LastInputEventUtc: packet.LastInputEventUtc?.ToUniversalTime() ?? fallbackEventUtc,
                KeyboardIkiMeanMs: keyboardIkiMeanMs,
                KeyboardIkiStdMs: keyboardIkiStdMs,
                KeyboardIkiCv: keyboardIkiCv,
                KeyboardBurstCount: keyboardBurstCount,
                KeyboardBurstRate: keyboardBurstRate,
                KeyboardCorrectionSpikeCount: keyboardCorrectionSpikeCount,
                KeyboardCorrectionSpikeRate: keyboardCorrectionSpikeRate,
                MouseMoveSampleCount: mouseMoveSampleCount,
                MousePathLengthPx: mousePathLengthPx,
                MouseSpeedMeanPxPerSec: mouseSpeedMeanPxPerSec,
                MouseJerkP95: mouseJerkP95,
                MouseMicroCorrectionCount: mouseMicroCorrectionCount,
                MouseMicroCorrectionRate: mouseMicroCorrectionRate,
                MouseWheelEventCount: mouseWheelEventCount,
                MouseScrollBurstCount: mouseScrollBurstCount,
                MouseScrollBurstRate: mouseScrollBurstRate,
                MouseScrollSpeedMean: mouseScrollSpeedMean,
                FocusObservationCount: focusObservationCount,
                FocusSwitchCount: focusSwitchCount,
                FocusActiveAppCount: focusActiveAppCount,
                FocusAppEntropy: focusAppEntropy,
                IdleObservationCount: idleObservationCount,
                IdleSecondsWithinWindow: idleSecondsWithinWindow,
                MicroIdleCount: microIdleCount,
                MicroIdleSeconds: microIdleSeconds,
                BreakSecondsWithinWindow: breakSecondsWithinWindow,
                ShortIdleBoundaryObserved: packet.ShortIdleBoundaryObserved,
                NotificationToastsEnabled: packet.NotificationToastsEnabled,
                HeartbeatSequence: packet.HeartbeatSequence,
                HeartbeatUpdatedAtUtc: packet.HeartbeatUpdatedAtUtc?.ToUniversalTime(),
                HeartbeatCaptureContext: packet.HeartbeatCaptureContext);
        }

        _inputSignalsIngress.IngestInputSignals(normalizedPacket);

        lock (_gate)
        {
            _lastEventTimestampUtc = packet.LastInputEventUtc is DateTimeOffset lastInputEventUtc
                ? lastInputEventUtc
                : fallbackEventUtc;
        }
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
            _latestSnapshotBackedByStore = false;
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
            _latestSnapshotBackedByStore = false;
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
            _latestSnapshotBackedByStore = false;
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
            _latestSnapshotBackedByStore = false;
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
            _latestSnapshotBackedByStore = false;
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
            _latestSnapshotBackedByStore = false;
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
            ResetRuntimeStateCacheLocked();
            _latestSnapshot = _latestSnapshot with { LastAction = "local_data_deleted" };
            _latestSnapshotBackedByStore = false;
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

    // Phase 1b — Step 3b: gather the live inputs and ask the (pure) governor whether a sampled
    // one-tap prompt is warranted right now, and which question. Read-only; changes nothing.
    public AgentLabelDiagnostics GetLabelDiagnostics() => _stateStore.ReadLabelDiagnostics();

    // Phase 1c: decide whether an unlinked device should be nudged to sign in right now. Read-mostly —
    // the only side effect is recording a shown nudge so the weekly + lifetime caps engage.
    public AgentSignupNudgeDecision GetSignupNudgeDecision(bool isLinked)
    {
        AgentServiceStatusSnapshot status = GetStatus();
        AgentSignupNudgeContext ctx = _stateStore.ReadSignupNudgeContext();

        var input = new Analysis.SignupNudgeGovernorInput(
            NowUtc: DateTimeOffset.UtcNow,
            IsLinked: isLinked,
            DaysOfData: ctx.DaysOfData,
            Paused: status.Paused,
            ConsentActive: status.ConsentActive,
            NudgesShownTotal: ctx.NudgesShownTotal,
            LastNudgeAtUtc: ctx.LastNudgeAtUtc);

        Analysis.SignupNudgeDecision decision = Analysis.SignupNudgeGovernor.Decide(input);

        if (decision.ShouldNudge)
        {
            _stateStore.RecordSignupNudgeShown(DateTimeOffset.UtcNow);
        }

        return new AgentSignupNudgeDecision(decision.ShouldNudge, decision.ReasonCode);
    }

    public AgentLabelPromptDecision GetLabelPromptDecision(bool meetingInProgress)
    {
        AgentServiceStatusSnapshot status = GetStatus();
        AgentBrainSnapshot snapshot = GetCurrentSnapshot();
        AgentLabelPromptCadence cadence = _stateStore.ReadLabelPromptCadence();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        // v2 moment inputs — gathered under _gate from the live session state + moment tracker.
        double secondsSinceResume;
        double secondsSinceLastInput;
        bool overloadCrossingLatched;
        bool awayReturnActive;
        double awayReturnAwayMinutes;
        double awayReturnPriorActiveMinutes;
        bool meetingNow;
        lock (_gate)
        {
            // Pressure-crossing latch: detect a rise through the threshold between successive polls,
            // and expire stale latches. Poll cadence (widget: ~60s) is the sampling rate.
            double pressure = status.PressureLevel;
            if (_labelLastPressureSample >= 0
                && _labelLastPressureSample < PortaExecutionConstants.LabelCapture.OverloadCheckPressureMin
                && pressure >= PortaExecutionConstants.LabelCapture.OverloadCheckPressureMin)
            {
                _labelOverloadCrossingUtc = nowUtc;
            }

            _labelLastPressureSample = pressure;
            if (_labelOverloadCrossingUtc is DateTimeOffset crossing
                && (nowUtc - crossing).TotalMinutes > PortaExecutionConstants.LabelCapture.OverloadLatchValidMinutes)
            {
                _labelOverloadCrossingUtc = null;
            }

            secondsSinceResume = _labelActivityResumeUtc is DateTimeOffset resume
                ? Math.Max(0d, (nowUtc - resume).TotalSeconds)
                : -1d;
            secondsSinceLastInput = _lastLiveSessionState.LastInputEventUtc is DateTimeOffset lastInput
                ? Math.Max(0d, (nowUtc - lastInput.ToUniversalTime()).TotalSeconds)
                : -1d;
            overloadCrossingLatched = _labelOverloadCrossingUtc is not null;
            awayReturnActive = _labelAwayReturnUtc is DateTimeOffset awayReturn
                && (nowUtc - awayReturn).TotalMinutes <= PortaExecutionConstants.LabelCapture.AwayReturnPromptWindowMinutes;
            awayReturnAwayMinutes = _labelLastAwayMinutes;
            awayReturnPriorActiveMinutes = _labelPriorActiveMinutesAtAwayReturn;

            // Meeting truth lives server-side (live foreground app), not in the widget: the caller's
            // flag is advisory-only and was historically hardcoded false.
            meetingNow = Porta.Agent.Common.Analysis.AppCategoryClassifier.IsMeetingApp(_lastLiveSessionState.FocusAppHash);
        }

        var input = new Analysis.LabelPromptGovernorInput(
            NowUtc: nowUtc,
            Confidence: status.PressureConfidence,
            PressureLevel: status.PressureLevel,
            CalibrationStage: status.CalibrationStage ?? string.Empty,
            DominantState: snapshot.DominantStateName ?? string.Empty,
            SuggestionsEnabled: status.ConsentActive,
            Paused: status.Paused,
            MeetingInProgress: meetingInProgress || meetingNow,
            ActivePromptsLast24h: cadence.ActivePromptsLast24h,
            LastActivePromptAtUtc: cadence.LastActivePromptAtUtc,
            UnansweredPromptsLast24h: cadence.UnansweredPromptsLast24h,
            SecondsSinceActivityResume: secondsSinceResume,
            SustainedAttentionSec: snapshot.SustainedAttentionDurationSec,
            SecondsSinceLastInput: secondsSinceLastInput,
            OverloadCrossingLatched: overloadCrossingLatched,
            AwayReturnActive: awayReturnActive,
            AwayReturnAwayMinutes: awayReturnAwayMinutes,
            AwayReturnPriorActiveMinutes: awayReturnPriorActiveMinutes);

        Analysis.LabelPromptDecision decision = Analysis.LabelPromptGovernor.Decide(input);

        // Anti-spam: when the governor says "prompt now", the widget host shows it unconditionally, so
        // record it as SHOWN here. This is what makes the cap + cooldown engage on shown prompts (not
        // only answered ones) and drives the progressive back-off when the user keeps ignoring them.
        if (decision.ShouldPrompt)
        {
            _stateStore.RecordPromptShown(decision.LabelTarget, decision.PromptKind, nowUtc);
            lock (_gate)
            {
                // Consume one-shot triggers so the same moment can't fire twice.
                if (decision.LabelTarget == "overload")
                {
                    _labelOverloadCrossingUtc = null;
                }

                if (decision.PromptKind == "away_return")
                {
                    _labelAwayReturnUtc = null;
                }

                // Stash the trigger context so the answering label can be stamped with it.
                _labelLastPromptKind = decision.PromptKind;
                _labelLastPromptSecondsSinceResume = secondsSinceResume;
                _labelLastPromptAtUtc = nowUtc;
            }
        }

        return new AgentLabelPromptDecision(
            ShouldPrompt: decision.ShouldPrompt,
            LabelTarget: decision.LabelTarget,
            PromptKind: decision.PromptKind,
            ReasonCode: decision.ReasonCode);
    }

    // Phase 1b — Step 3b: record a widget one-tap answer as a durable ground-truth label, tagged
    // with the current feature context. Returns false for an unrecognized target/answer.
    public bool RecordFocusLabel(AgentFocusLabelRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        (string source, string responseKind, int value) = Analysis.LabelResponseMap.Map(request.LabelTarget, request.Answer);
        if (source.Length == 0)
        {
            return false;
        }

        AgentServiceStatusSnapshot status = GetStatus();
        AgentBrainSnapshot snapshot = GetCurrentSnapshot();

        var label = new GroundTruthLabel(
            CreatedAtUtc: DateTimeOffset.UtcNow,
            BucketSeq: -1,
            BucketTimestampUtc: status.LastUpdatedUtc.ToString("O"),
            LabelSource: source,
            PromptKind: string.IsNullOrWhiteSpace(request.PromptKind) ? source : request.PromptKind.Trim(),
            ResponseKind: responseKind,
            LabelTarget: (request.LabelTarget ?? string.Empty).Trim().ToLowerInvariant(),
            LabelValue: value,
            ResponseLatencyMs: Math.Max(-1, request.ResponseLatencyMs),
            InterventionId: string.Empty,
            FeaturesJson: StampLabelMomentContext(
                string.IsNullOrWhiteSpace(status.LatestContributorsJson) ? "{}" : status.LatestContributorsJson),
            PressureLevel: status.PressureLevel,
            OverloadLikelihood: status.OverloadLikelihood,
            Confidence: status.PressureConfidence,
            DominantState: snapshot.DominantStateName ?? string.Empty,
            CalibrationStage: status.CalibrationStage ?? string.Empty,
            TrustTier: status.TrustTier ?? string.Empty,
            SchemaVer: 1);

        _stateStore.RecordGroundTruthLabel(label);

        // Anti-spam: this answer resolves the most recent shown prompt for this target, resetting the
        // back-off streak so an engaged user isn't penalised by the progressive cooldown.
        _stateStore.ResolvePromptAnswered(label.LabelTarget, DateTimeOffset.UtcNow);
        return true;
    }

    // Phase 1b v2 — stamp the moment context (what triggered the prompt, how long since activity
    // resumed) into the label's feature snapshot, so training can weight or filter labels by
    // moment quality — and so we can audit that prompts stopped landing at session start.
    // Best-effort: any parse problem returns the original JSON untouched.
    private string StampLabelMomentContext(string featuresJson)
    {
        string promptKind;
        double secondsSinceResume;
        lock (_gate)
        {
            // Only stamp when the answer plausibly belongs to the last shown prompt (one prompt is
            // active at a time; widget display window + answering latency stay well under this).
            if (_labelLastPromptAtUtc is not DateTimeOffset shownAt
                || (DateTimeOffset.UtcNow - shownAt).TotalMinutes > 10d)
            {
                return featuresJson;
            }

            promptKind = _labelLastPromptKind;
            secondsSinceResume = _labelLastPromptSecondsSinceResume;
        }

        try
        {
            System.Text.Json.Nodes.JsonNode? node = System.Text.Json.Nodes.JsonNode.Parse(featuresJson);
            if (node is not System.Text.Json.Nodes.JsonObject obj)
            {
                return featuresJson;
            }

            obj["prompt_trigger"] = promptKind;
            obj["seconds_since_resume"] = Math.Round(secondsSinceResume, 1);
            return obj.ToJsonString();
        }
        catch (Exception)
        {
            return featuresJson;
        }
    }

    // ── Phase 2c: reminders ─────────────────────────────────────────────────────
    // Thin pass-throughs to the store (which owns the clock semantics). Validation here:
    // a due time in the past is clamped to one minute out, so a slow round-trip or a
    // parsed "at" time racing midnight can never create an instantly-fired reminder.
    public AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset due = dueAtUtc.ToUniversalTime();
        if (due <= nowUtc)
        {
            due = nowUtc.AddMinutes(1);
        }

        return _stateStore.CreateReminder(title, due, nowUtc);
    }

    public AgentReminder? GetNextReminder() => _stateStore.ReadNextReminder(DateTimeOffset.UtcNow);

    public bool AcknowledgeReminder(long id, string action)
        => _stateStore.AcknowledgeReminder(id, action, DateTimeOffset.UtcNow);

    // Workstream 4 — timed tasks. Closed tasks from earlier days drop off the widget list while
    // open ones always show.
    //
    // Step 3 — "today" here is the GOAL day, not the calendar day. It used to start at local
    // midnight while the check-in half of the same day started at 05:00 (GoalDay below), so between
    // midnight and 5am the two halves disagreed about which day it was: the goal row still read
    // yesterday while today's task list had already emptied itself. Now that the morning check-in
    // creates the tasks, both halves have to answer that question the same way.
    public AgentTask? CreateTask(string title, int plannedSeconds)
        => _stateStore.CreateTask(title, plannedSeconds, DateTimeOffset.UtcNow, projectId: 0);

    // Step 9a — the same call carrying the project the user picked on the card. Ari never guesses
    // a project, so this is only ever a value the user tapped; 0 means untagged, which is a valid
    // permanent state and the default.
    public AgentTask? CreateTask(string title, int plannedSeconds, long projectId)
        => _stateStore.CreateTask(title, plannedSeconds, DateTimeOffset.UtcNow, projectId);

    public AgentProject? CreateProject(string name)
        => _stateStore.CreateProject(name, DateTimeOffset.UtcNow);

    public IReadOnlyList<AgentProject> GetProjects(bool includeArchived = false)
        => _stateStore.ReadProjects(includeArchived);

    public AgentTask? SetTaskProject(long taskId, long projectId)
        => _stateStore.SetTaskProject(taskId, projectId);

    public AgentProject? RenameProject(long id, string name)
        => _stateStore.RenameProject(id, name);

    public AgentProject? SetProjectArchived(long id, bool archived)
        => _stateStore.SetProjectArchived(id, archived, DateTimeOffset.UtcNow);

    public AgentTask? SetTaskClientLabel(long taskId, string label)
        => _stateStore.SetTaskClientLabel(taskId, label);

    public AgentTask? SetTaskReportFields(long taskId, string? status, string? resultLine, string? blockedReason, string? expectedFinishDay)
        => _stateStore.SetTaskReportFields(taskId, status, resultLine, blockedReason, expectedFinishDay);

    public AgentProject? SetProjectClientName(long id, string clientName)
        => _stateStore.SetProjectClientName(id, clientName);

    public AgentProject? SetProjectLabelStyle(long id, bool useClientLabels)
        => _stateStore.SetProjectLabelStyle(id, useClientLabels);

    public AgentProject? SetProjectRate(long id, long rateMinor, string currency)
        => _stateStore.SetProjectRate(id, rateMinor, currency);

    public AgentProject? SetProjectEngagement(
        long id,
        string? status,
        string? startDay,
        string? completionDay,
        IReadOnlyList<string>? recipients,
        string? reportingFrequency)
        => _stateStore.SetProjectEngagement(id, status, startDay, completionDay, recipients, reportingFrequency);

    public AgentProjectStats ReadProjectStats(long projectId)
        => _stateStore.ReadProjectStats(projectId);
    public IReadOnlyList<AgentProjectCard> ReadProjectCards(bool includeArchived = false)
        => _stateStore.ReadProjectCards(includeArchived);

    public IReadOnlyList<AgentProjectTaskRow> ReadProjectTasks(long projectId)
        => _stateStore.ReadProjectTasks(projectId);

    public IReadOnlyList<AgentTaskRow> ReadAllTaskRows()
        => _stateStore.ReadAllTaskRows();

    /// <summary>
    /// Step 10 phase B — the report draft. The local offset is resolved HERE, the same place
    /// GetAppUsage resolves it, so no caller has to know the store holds UTC.
    /// </summary>
    public AgentReportDraft GetReportDraft(long projectId, string fromLocalDate, string toLocalDate)
        => _stateStore.ReadReportDraft(projectId, fromLocalDate, toLocalDate, DateTimeOffset.Now.Offset);

    public AgentWeeklyDigestData GetWeeklyDigestData(string fromLocalDate, string toLocalDate)
        => _stateStore.ReadWeeklyDigestData(fromLocalDate, toLocalDate, DateTimeOffset.Now.Offset);

    // Step 10 phase C/D — pass-through to the store. The rules live in ReportSendPreflight and in
    // the endpoint that orchestrates them; nothing is decided here.
    public IReadOnlyList<AgentReportRecipient> GetRecipients() => _stateStore.ReadRecipients();

    public AgentReportRecipient? UpsertRecipient(string name, string email)
        => _stateStore.UpsertRecipient(name, email, DateTimeOffset.UtcNow);

    public bool DeleteRecipient(long id) => _stateStore.DeleteRecipient(id);

    public bool HasSentToRecipientBefore(string email) => _stateStore.HasSentToRecipientBefore(email);

    public IReadOnlyList<AgentSentReport> GetSentReports() => _stateStore.ReadSentReports();

    public bool SetSentReportArchived(string reportId, bool archived)
        => _stateStore.SetSentReportArchived(reportId, archived);

    public bool RecordSentReport(AgentSentReport report) => _stateStore.RecordSentReport(report);

    public void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error)
        => _stateStore.MarkSentReportOutcome(reportId, sent, sentAtUtc, error);

    public void MarkSentReportDeletePending(string reportId)
        => _stateStore.MarkSentReportDeletePending(reportId);

    public bool DeleteSentReportLocally(string reportId) => _stateStore.DeleteSentReportLocally(reportId);

    public void MarkRecipientUsed(string email) => _stateStore.MarkRecipientUsed(email, DateTimeOffset.UtcNow);

    /// <summary>
    /// 2026-08-12 — the hourly app rollup for a window, already resolved to projects. The local
    /// offset is resolved HERE, the same place GoalDay resolves the local day, so callers never
    /// have to know the store holds UTC.
    /// </summary>
    public IReadOnlyList<AgentAppUsageHour> GetAppUsage(DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => _stateStore.ReadAppUsage(fromUtc, toUtc, DateTimeOffset.Now.Offset);

    public IReadOnlyList<AgentTask> GetTodayTasks()
        => _stateStore.ReadTasks(
            GoalDayStartLocal(DateTimeOffset.Now).ToUniversalTime(), DateTimeOffset.UtcNow);

    /// <summary>
    /// Tasks from the last <paramref name="goalDaysBack"/> goal-days as well as today. Open tasks
    /// always come back regardless of age; the window only decides how far back CLOSED ones reach.
    /// </summary>
    /// <remarks>
    /// The narrative needs this and <see cref="GetTodayTasks"/> cannot serve it. The morning card
    /// fires around 07:00, after the 05:00 boundary, so yesterday's finished and abandoned tasks
    /// are already outside today's window — the exact moment Ari most needs to know what happened,
    /// it would otherwise be handed an empty list. Measured on 2026-08-10: at 07:39 the day's task
    /// list was empty while two tasks had been abandoned the previous afternoon.
    /// </remarks>
    public IReadOnlyList<AgentTask> GetRecentTasks(int goalDaysBack)
        => _stateStore.ReadTasks(
            GoalDayStartLocal(DateTimeOffset.Now).AddDays(-Math.Max(0, goalDaysBack)).ToUniversalTime(),
            DateTimeOffset.UtcNow);

    // Complaint 3 (2026-08-11) — "defer" resolves tomorrow's goal-day HERE, because the goal day is
    // a local, 05:00-boundary concept and the store must not have to guess it from a UTC instant.
    // Every other action ignores the extra argument.
    public AgentTask? ActOnTask(long id, string action)
        => _stateStore.ActOnTask(
            id,
            action,
            DateTimeOffset.UtcNow,
            deferUntilDay: string.Equals(action?.Trim(), "defer", StringComparison.OrdinalIgnoreCase)
                ? GoalDay(DateTimeOffset.Now.AddDays(1))
                : null);

    // Workstream D — conversational check-in goals. The "goal day" is the 5:00-local-boundary
    // day (a check-in at 02:00 still belongs to yesterday), matching the widget-hide / mute
    // boundary so the whole day lines up.
    internal static string GoalDay(DateTimeOffset nowLocal)
        => (nowLocal.Hour < 5 ? nowLocal.AddDays(-1) : nowLocal).ToString("yyyy-MM-dd");

    // The instant GoalDay(nowLocal) began — the same boundary expressed as a timestamp, so the task
    // list and the goal row can never disagree about where the day starts. Pure, so the 00:00–05:00
    // window is unit-tested rather than reasoned about.
    internal static DateTimeOffset GoalDayStartLocal(DateTimeOffset nowLocal)
    {
        DateTimeOffset day = nowLocal.Hour < 5 ? nowLocal.AddDays(-1) : nowLocal;
        return new DateTimeOffset(day.Date, day.Offset).AddHours(5);
    }

    // Defensive only: the morning card's ceiling is three (locked 2026-08-07), and this is a
    // localhost request, but a malformed count must not be able to write labels without bound.
    private const int MaxMorningGoalLabels = 10;

    // Step 3 — taskCount is how many tasks the morning check-in actually recorded. One label per
    // task (decided by Yadin, 2026-08-07): naming three things is three commitments, and the ops
    // console's per-user label counts should reflect that rather than flattening it to one.
    // Defaults to 1, so every pre-step-3 caller keeps its exact previous behaviour.
    public AgentDailyGoal? SetMorningGoal(string goalText, string trackingPref, int taskCount = 1)
    {
        AgentDailyGoal? goal = _stateStore.UpsertMorningGoal(
            GoalDay(DateTimeOffset.Now), goalText, trackingPref, DateTimeOffset.UtcNow);

        // D4 — the stated morning goal is a Phase-1b ground-truth label, generated for free by the
        // check-in loop. Recorded here in the controller so it lands regardless of which caller
        // drove the check-in. Neutral value: a stated goal, not a right/wrong confirmation.
        if (goal is not null && !string.IsNullOrWhiteSpace(goalText))
        {
            int labels = Math.Clamp(taskCount, 1, MaxMorningGoalLabels);
            for (int i = 0; i < labels; i++)
            {
                RecordGoalGroundTruthLabel(
                    source: "morning_goal",
                    promptKind: "morning_goal",
                    responseKind: string.IsNullOrWhiteSpace(trackingPref) ? "stated" : trackingPref.Trim().ToLowerInvariant(),
                    target: "daily_goal",
                    value: 0);
            }
        }

        return goal;
    }

    public AgentDailyGoal? GetTodayGoal()
        => _stateStore.GetDailyGoal(GoalDay(DateTimeOffset.Now));

    // Part 3 — memory. The EOD check-in writes plan_tomorrow onto TODAY's goal row; until now
    // nothing ever read it back, so the night plan evaporated by morning. The morning check-in
    // calls this to carry it forward: yesterday's plan seeds today's goal ask, but only while
    // today has no stated goal of its own.
    public string? GetCarriedOverPlan()
    {
        DateTimeOffset nowLocal = DateTimeOffset.Now;
        AgentDailyGoal? today = _stateStore.GetDailyGoal(GoalDay(nowLocal));
        AgentDailyGoal? yesterday = _stateStore.GetDailyGoal(GoalDay(nowLocal.AddDays(-1)));
        return ResolveCarriedOverPlan(today, yesterday);
    }

    // Pure so the carry-forward rule is unit-testable without a controller: surface yesterday's
    // plan_tomorrow only when today has no goal_text yet.
    internal static string? ResolveCarriedOverPlan(AgentDailyGoal? today, AgentDailyGoal? yesterday)
    {
        if (!string.IsNullOrWhiteSpace(today?.GoalText))
        {
            return null;
        }

        string plan = (yesterday?.PlanTomorrow ?? string.Empty).Trim();
        return plan.Length > 0 ? plan : null;
    }

    public AgentDailyGoal? RecordMiddayAnswer(string answer)
    {
        AgentDailyGoal? goal = _stateStore.RecordMiddayGoalAnswer(
            GoalDay(DateTimeOffset.Now), answer, DateTimeOffset.UtcNow);

        // D4 — the yes/partly/not_yet midday answer is a Phase-1b ground-truth label: did Ari's day
        // help the user get to what mattered? yes = +1, not_yet = -1, partly = neutral.
        string normalized = (answer ?? string.Empty).Trim().ToLowerInvariant();
        if (goal is not null && normalized is "yes" or "partly" or "not_yet")
        {
            RecordGoalGroundTruthLabel(
                source: "midday_check",
                promptKind: "midday_progress",
                responseKind: normalized,
                target: "goal_progress",
                value: normalized switch { "yes" => 1, "not_yet" => -1, _ => 0 });
        }

        return goal;
    }

    // D4 — record a conversational check-in answer as a durable ground-truth label. The morning-goal
    // and midday-progress labels are exactly the Phase-1b ground truth the charging plan needs,
    // captured for free from the check-in loop. Context fields mirror RecordFocusLabel.
    private void RecordGoalGroundTruthLabel(
        string source,
        string promptKind,
        string responseKind,
        string target,
        int value)
    {
        AgentServiceStatusSnapshot status = GetStatus();
        AgentBrainSnapshot snapshot = GetCurrentSnapshot();

        var label = new GroundTruthLabel(
            CreatedAtUtc: DateTimeOffset.UtcNow,
            BucketSeq: -1,
            BucketTimestampUtc: status.LastUpdatedUtc.ToString("O"),
            LabelSource: source,
            PromptKind: promptKind,
            ResponseKind: responseKind,
            LabelTarget: target,
            LabelValue: value,
            ResponseLatencyMs: -1,
            InterventionId: string.Empty,
            FeaturesJson: string.IsNullOrWhiteSpace(status.LatestContributorsJson) ? "{}" : status.LatestContributorsJson,
            PressureLevel: status.PressureLevel,
            OverloadLikelihood: status.OverloadLikelihood,
            Confidence: status.PressureConfidence,
            DominantState: snapshot.DominantStateName ?? string.Empty,
            CalibrationStage: status.CalibrationStage ?? string.Empty,
            TrustTier: status.TrustTier ?? string.Empty,
            SchemaVer: 1);

        _stateStore.RecordGroundTruthLabel(label);
    }

    public AgentDailyGoal? RecordEndOfDay(string planTomorrow)
        => _stateStore.RecordEndOfDayPlan(GoalDay(DateTimeOffset.Now), planTomorrow, DateTimeOffset.UtcNow);

    // Phase 5 — deliberately NOT paired with RecordEndOfDay. A correction says the day is not over,
    // so stamping it would record the opposite of what the user just said.
    public AgentCheckInCorrection? RecordCheckInCorrection(
        string cardKind,
        string correction,
        string assumedEndLocal,
        double assumedActiveSeconds,
        int assumedFinishedCount,
        int assumedOpenCount)
        => _stateStore.RecordCheckInCorrection(
            GoalDay(DateTimeOffset.Now),
            cardKind,
            correction,
            assumedEndLocal,
            assumedActiveSeconds,
            assumedFinishedCount,
            assumedOpenCount,
            DateTimeOffset.UtcNow);

    public IReadOnlyList<AgentCheckInCorrection> GetCheckInCorrectionsToday()
        => _stateStore.ReadCheckInCorrections(GoalDay(DateTimeOffset.Now));

    public void AcknowledgeIntervention(AgentInterventionAck ack)
    {
        if (ack is null)
        {
            throw new ArgumentNullException(nameof(ack));
        }

        string sessionId = ack.SessionId?.Trim() ?? string.Empty;
        string agentInstanceId = ack.AgentInstanceId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("missing_session_id", nameof(ack));
        }

        if (string.IsNullOrWhiteSpace(agentInstanceId))
        {
            throw new ArgumentException("missing_agent_instance_id", nameof(ack));
        }

        lock (_gate)
        {
            ValidateInputSignalSourceLocked(DateTimeOffset.UtcNow, agentInstanceId, sessionId);
            bool acknowledged = _stateStore.AcknowledgeIntervention(ack);
            if (!acknowledged)
            {
                throw new InvalidOperationException("intervention_not_found");
            }

            _lastError = null;
            _lastEventTimestampUtc = ack.AppliedAtUtc;
            WriteDiagnosticsLocked(force: false);
        }
    }

    public void ApplyBrainSnapshot(AgentBrainSnapshot snapshot, bool backendConnected)
    {
        RuntimeStateCacheRefreshResult refresh = ReadRuntimeStateCacheFromStore(
            snapshot.TimestampUtc,
            System.Threading.Interlocked.Increment(ref _runtimeStateRefreshSequence));

        lock (_gate)
        {
            _latestSnapshot = snapshot;
            _latestSnapshotBackedByStore = false;
            ApplyRuntimeStateCacheRefreshLocked(refresh);
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
            _latestSnapshotBackedByStore = false;
            _stateStore.WriteSnapshot(_latestSnapshot);
            RefreshRuntimeStateCacheFromStoreLocked(nowUtc);

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
                ["policy"] = ResolvePolicyVersion()
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
            _latestSnapshotBackedByStore = false;
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
        if (PortaActionSemantics.IsPolicyIntentAction(action))
        {
            return PortaActionSemantics.DescribePolicyIntent(action, lifecycleState);
        }

        if (string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase))
        {
            return "Intervention ended and automatic restoration was attempted.";
        }

        return action switch
        {
            PortaBreakSemantics.LongBreakKind or PortaBreakSemantics.LegacyLongBreakAction
                => "Long break suggestion was presented to the user.",
            "focus_lock_in_suggestion" => "Focus lock-in suggestion was presented in the user interface.",
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction
                => "Short break suggestion was presented to the user.",
            "deep_work_notifications_suppressed" => "Notification suppression was requested for deep work protection.",
            "deep_work_notifications_restored" => "Notification restoration was requested after deep work ended.",
            "inform_overload_risk" => "Overload risk was logged (inform-only).",
            "inform_focus_protection" => "Focus protection was logged (inform-only).",
            "inform_fatigue_prevention" => "Fatigue prevention was logged (inform-only).",
            _ => $"Action {action} was recorded."
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
            PortaBreakSemantics.LongBreakKind
                or PortaBreakSemantics.ShortBreakKind
                or PortaBreakSemantics.LegacyLongBreakAction
                or PortaBreakSemantics.LegacyShortBreakAction
                or "focus_lock_in_suggestion"
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

        try
        {
            _stateStore.WriteDiagnostics(new AgentDiagnosticsSnapshot(
                TimestampUtc: nowUtc,
                BackendConnected: _backendConnected,
                ConsentActive: _consentActive,
                Paused: _paused,
                LastError: _lastError,
                PolicyVersion: ResolvePolicyVersion(),
                SchemaVersion: DefaultSchemaVersion,
                AgentVersion: _agentVersion,
                LastEventTimestampUtc: _lastEventTimestampUtc,
                LastBackendOkUtc: _lastBackendOkUtc));

            _lastDiagnosticsWriteUtc = nowUtc;
        }
        catch (Exception ex)
        {
            WriteLogLocked($"diagnostics_write_failed error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static string NormalizeTestAction(string action)
    {
        string normalized = action.Trim().ToLowerInvariant();
        // Normalize legacy break action IDs to canonical names.
        string canonical = PortaBreakSemantics.ResolveBreakKind(normalized) ?? normalized;
        return canonical switch
        {
            "inform_overload_risk" => canonical,
            "inform_focus_protection" => canonical,
            "inform_fatigue_prevention" => canonical,
            PortaBreakSemantics.LongBreakKind => canonical,
            PortaBreakSemantics.ShortBreakKind => canonical,
            "focus_lock_in_suggestion" => canonical,
            "deep_work_notifications_suppressed" => canonical,
            "deep_work_notifications_restored" => canonical,
            "focus_mode_enabled" => canonical,
            "focus_assist_enabled" => canonical,
            "fatigue_relief_enabled" => canonical,
            "temporary_app_restriction_enabled" => canonical,
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
            PortaBreakSemantics.LongBreakKind or PortaBreakSemantics.LegacyLongBreakAction
                => ("fatigue", 0.82, "Suggest", false, 300),
            "focus_lock_in_suggestion" => ("focus", 0.83, "Suggest", false, 300),
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction
                => ("fatigue", 0.84, "Suggest", false, 300),
            "deep_work_notifications_suppressed" => ("focus", 0.88, "Nudge", true, 600),
            "deep_work_notifications_restored" => ("focus", 0.74, "Inform", true, 600),
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
            PortaBreakSemantics.LongBreakKind or PortaBreakSemantics.LegacyLongBreakAction
                => "fatigue-prevention-v1",
            "focus_lock_in_suggestion" => "focus-protection-v1",
            "focus_assist_enabled" => "focus-protection-v1",
            "inform_focus_protection" => "focus-protection-v1",
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction
                => "fatigue-prevention-v1",
            "fatigue_relief_enabled" => "fatigue-prevention-v1",
            "inform_fatigue_prevention" => "fatigue-prevention-v1",
            "deep_work_notifications_suppressed" => "deep_work_notifications:local-default",
            "deep_work_notifications_restored" => "deep_work_notifications:local-default",
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
            PortaBreakSemantics.LongBreakKind or PortaBreakSemantics.LegacyLongBreakAction => "fatigue",
            "focus_lock_in_suggestion" => "focus",
            "focus_assist_enabled" => "focus",
            "inform_focus_protection" => "focus",
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction => "fatigue",
            "fatigue_relief_enabled" => "fatigue",
            "inform_fatigue_prevention" => "fatigue",
            "deep_work_notifications_suppressed" => "focus",
            "deep_work_notifications_restored" => "focus",
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
            "deep_work_notifications_suppressed" => 0.28,
            "deep_work_notifications_restored" => 0.24,
            "focus_assist_enabled" => 0.38,
            "focus_lock_in_suggestion" => 0.33,
            "inform_focus_protection" => 0.30,
            "fatigue_relief_enabled" => 0.34,
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction => 0.36,
            "inform_fatigue_prevention" => 0.40,
            _ => Math.Max(baseline.OverloadLikelihood, 0.45)
        };

        double fatigue = action switch
        {
            PortaBreakSemantics.LongBreakKind or PortaBreakSemantics.LegacyLongBreakAction => 0.82,
            "fatigue_relief_enabled" => 0.88,
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction => 0.86,
            "inform_fatigue_prevention" => 0.78,
            _ => Math.Max(baseline.FatigueProbability, 0.33)
        };
        double focus = action switch
        {
            "focus_mode_enabled" => 0.64,
            "focus_assist_enabled" => 0.88,
            "focus_lock_in_suggestion" => 0.84,
            "inform_focus_protection" => 0.78,
            "deep_work_notifications_suppressed" => 0.91,
            "deep_work_notifications_restored" => 0.82,
            "fatigue_relief_enabled" => 0.36,
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction => 0.34,
            "inform_fatigue_prevention" => 0.40,
            PortaBreakSemantics.LongBreakKind or PortaBreakSemantics.LegacyLongBreakAction => 0.41,
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

    private static bool IsValidIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxSignalIdLength
            && IdPattern.IsMatch(value);
    }

    private static bool IsValidFocusHash(string focusHash)
    {
        if (string.IsNullOrWhiteSpace(focusHash))
        {
            return true;
        }

        if (focusHash.Length > MaxFocusHashLength)
        {
            return false;
        }

        return FocusHashPattern.IsMatch(focusHash);
    }

    private static int ClampNonNegativeInt(int value, int max)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static int ClampFiniteInt(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static double ClampFiniteDouble(double value, double min, double max)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, min, max);
    }

    private static double ClampNonNegativeDouble(double value, double max)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        if (value < 0d)
        {
            return 0d;
        }

        return Math.Min(value, max);
    }

    private static double ClampNonNegativeNullableDouble(double? value, double max)
    {
        if (!value.HasValue)
        {
            return 0d;
        }

        return ClampNonNegativeDouble(value.Value, max);
    }

    private static string NormalizeFocusHash(string? focusAppHash)
    {
        if (string.IsNullOrWhiteSpace(focusAppHash))
        {
            return string.Empty;
        }

        return focusAppHash.Trim().ToLowerInvariant();
    }

    private static string? NormalizeIdentifier(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string BuildSessionOwnerId(int sessionId)
    {
        return $"session-{Math.Max(0, sessionId)}";
    }

    private static string BuildBackendInstanceId()
    {
        return $"backend-{Environment.ProcessId}-{Guid.NewGuid():N}";
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

    private static bool IsSignalActive(DateTimeOffset? signalUtc, DateTimeOffset nowUtc)
    {
        return signalUtc.HasValue
            && (nowUtc - signalUtc.Value.ToUniversalTime()) <= SignalHealthActiveWindow;
    }

    private static bool IsCaptureContextValid(string captureContext)
    {
        return string.Equals(captureContext, CaptureContextUserSession, StringComparison.Ordinal);
    }

    private string? ResolveCaptureFailureReasonLocked(
        string captureContext,
        UserAgentSessionContextSnapshot? observedSessionContext,
        string? ownershipWarning,
        string? captureSelfTestState)
    {
        if (string.Equals(captureContext, CaptureContextUserSession, StringComparison.Ordinal)
            && string.Equals(captureSelfTestState, CaptureSelfTestStateFailed, StringComparison.Ordinal))
        {
            return CaptureFailureReasonInputSelfTestFailed;
        }

        if (string.Equals(captureContext, CaptureContextUserSession, StringComparison.Ordinal))
        {
            if (string.Equals(ownershipWarning, OwnershipWarningSessionMismatch, StringComparison.Ordinal))
            {
                return CaptureFailureReasonSessionSwitched;
            }

            if (string.Equals(ownershipWarning, OwnershipWarningAgentInstanceMismatch, StringComparison.Ordinal))
            {
                return OwnershipWarningAgentInstanceMismatch;
            }

            return null;
        }

        string observedCaptureContext = NormalizeCaptureContext(observedSessionContext?.CaptureContext);
        if (string.Equals(observedCaptureContext, CaptureContextUserSession, StringComparison.Ordinal))
        {
            if (IsObservedSessionDifferentFromLastAcceptedOwner(observedSessionContext))
            {
                return CaptureFailureReasonSessionSwitched;
            }

            return CaptureFailureReasonHeartbeatStale;
        }

        return captureContext switch
        {
            CaptureContextServiceSession => CaptureFailureReasonServiceSession,
            CaptureContextUnknown => CaptureFailureReasonUnknown,
            _ => null
        };
    }

    private string ResolveCaptureContextLocked(
        DateTimeOffset nowUtc,
        out UserAgentSessionContextSnapshot? sessionContext)
    {
        sessionContext = ReadFreshUserAgentSessionContextLocked(nowUtc);
        if (sessionContext is not null)
        {
            _captureContext = NormalizeCaptureContext(sessionContext.CaptureContext);
            return _captureContext;
        }

        _captureContext = CaptureContextUnknown;
        return _captureContext;
    }

    private static string NormalizeCaptureContext(string? captureContext)
    {
        return captureContext?.Trim() switch
        {
            CaptureContextUserSession => CaptureContextUserSession,
            CaptureContextServiceSession => CaptureContextServiceSession,
            _ => CaptureContextUnknown
        };
    }

    private static string? NormalizeObservedCaptureContext(string? captureContext)
    {
        string normalized = NormalizeCaptureContext(captureContext);
        return normalized == CaptureContextUnknown ? null : normalized;
    }

    private static int? NormalizeHeartbeatWriteFailureCount(int? count)
    {
        if (!count.HasValue || count.Value < 0)
        {
            return null;
        }

        return count.Value;
    }

    private static string? NormalizeCaptureSelfTestState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant();
    }

    private UserAgentSessionContextSnapshot? ReadFreshUserAgentSessionContextLocked(DateTimeOffset nowUtc)
    {
        UserAgentSessionContextSnapshot? snapshot = _userAgentSessionContextStore.ReadCurrent();
        if (snapshot is null)
        {
            return null;
        }

        DateTimeOffset updatedAtUtc = snapshot.UpdatedAtUtc.ToUniversalTime();
        if (updatedAtUtc > nowUtc || nowUtc - updatedAtUtc > CaptureContextFreshnessWindow)
        {
            return null;
        }

        return snapshot;
    }

    private void ReconcileStaleLastAcceptedOwnershipLocked(
        DateTimeOffset nowUtc,
        UserAgentSessionContextSnapshot? sessionContext)
    {
        if (sessionContext is null
            || !string.Equals(NormalizeCaptureContext(sessionContext.CaptureContext), CaptureContextUserSession, StringComparison.Ordinal))
        {
            return;
        }

        string? lastInputAgentInstanceId = NormalizeIdentifier(_lastInputAgentInstanceId);
        string? lastInputSessionId = NormalizeIdentifier(_lastInputSessionId);
        if (lastInputAgentInstanceId is null && lastInputSessionId is null)
        {
            return;
        }

        string? heartbeatAgentInstanceId = NormalizeIdentifier(sessionContext.AgentInstanceId);
        string heartbeatSessionId = BuildSessionOwnerId(sessionContext.SessionId);
        bool agentMismatch =
            heartbeatAgentInstanceId is not null
            && lastInputAgentInstanceId is not null
            && !string.Equals(heartbeatAgentInstanceId, lastInputAgentInstanceId, StringComparison.Ordinal);
        bool sessionMismatch =
            lastInputSessionId is not null
            && !string.Equals(heartbeatSessionId, lastInputSessionId, StringComparison.Ordinal);
        if (!agentMismatch && !sessionMismatch)
        {
            return;
        }

        DateTimeOffset heartbeatUpdatedAtUtc = sessionContext.UpdatedAtUtc.ToUniversalTime();
        if (heartbeatUpdatedAtUtc > nowUtc
            || (nowUtc - heartbeatUpdatedAtUtc) < OwnershipReconciliationGraceWindow)
        {
            return;
        }

        DateTimeOffset? lastInputEventUtc = _lastInputEventUtc?.ToUniversalTime();
        if (lastInputEventUtc.HasValue && heartbeatUpdatedAtUtc <= lastInputEventUtc.Value)
        {
            return;
        }

        _lastInputEventUtc = null;
        _lastInputAgentInstanceId = null;
        _lastInputSessionId = null;
        _lastKeyboardSignalUtc = null;
        _lastMouseSignalUtc = null;
    }

    private void ValidateInputSignalSourceLocked(
        DateTimeOffset nowUtc,
        string agentInstanceId,
        string sessionId,
        long? heartbeatSequence = null,
        DateTimeOffset? heartbeatUpdatedAtUtc = null,
        string? heartbeatCaptureContext = null)
    {
        if (HasRequestBoundHeartbeatProof(heartbeatSequence, heartbeatUpdatedAtUtc, heartbeatCaptureContext))
        {
            try
            {
                ValidateRequestBoundHeartbeatProofLocked(
                    nowUtc,
                    heartbeatSequence!.Value,
                    heartbeatUpdatedAtUtc!.Value,
                    heartbeatCaptureContext!,
                    agentInstanceId,
                    sessionId);
                return;
            }
            catch (InvalidOperationException ex) when (string.Equals(ex.Message, InputSignalsSourceHeartbeatMissing, StringComparison.Ordinal)
                && IsSameProcessHeartbeatStillAcceptableLocked(nowUtc, agentInstanceId, sessionId))
            {
                return;
            }
        }

        if (IsSameProcessHeartbeatStillAcceptableLocked(nowUtc, agentInstanceId, sessionId))
        {
            return;
        }

        UserAgentSessionContextSnapshot? sessionContext = ReadFreshUserAgentSessionContextLocked(nowUtc);
        if (sessionContext is null)
        {
            throw new InvalidOperationException(InputSignalsSourceHeartbeatMissing);
        }

        string captureContext = NormalizeCaptureContext(sessionContext.CaptureContext);
        if (!IsCaptureContextValid(captureContext))
        {
            throw new InvalidOperationException(InputSignalsSourceInvalidContext);
        }

        string? heartbeatAgentInstanceId = NormalizeIdentifier(sessionContext.AgentInstanceId);
        if (!string.Equals(heartbeatAgentInstanceId, agentInstanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(InputSignalsSourceAgentMismatch);
        }

        string heartbeatSessionId = BuildSessionOwnerId(sessionContext.SessionId);
        if (!string.Equals(heartbeatSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(InputSignalsSourceSessionMismatch);
        }

        RecordAcceptedHeartbeatOwnerLocked(
            heartbeatSequence: sessionContext.HeartbeatSequence,
            heartbeatUpdatedAtUtc: sessionContext.UpdatedAtUtc,
            agentInstanceId,
            sessionId);
    }

    private static bool HasRequestBoundHeartbeatProof(
        long? heartbeatSequence,
        DateTimeOffset? heartbeatUpdatedAtUtc,
        string? heartbeatCaptureContext)
    {
        return heartbeatSequence.HasValue
            && heartbeatSequence.Value > 0
            && heartbeatUpdatedAtUtc.HasValue
            && !string.IsNullOrWhiteSpace(heartbeatCaptureContext);
    }

    private void ValidateRequestBoundHeartbeatProofLocked(
        DateTimeOffset nowUtc,
        long heartbeatSequence,
        DateTimeOffset heartbeatUpdatedAtUtc,
        string heartbeatCaptureContext,
        string agentInstanceId,
        string sessionId)
    {
        if (heartbeatSequence <= 0)
        {
            throw new InvalidOperationException(InputSignalsSourceHeartbeatMissing);
        }

        DateTimeOffset updatedAtUtc = heartbeatUpdatedAtUtc.ToUniversalTime();
        if (updatedAtUtc > nowUtc || nowUtc - updatedAtUtc > CaptureContextFreshnessWindow)
        {
            throw new InvalidOperationException(InputSignalsSourceHeartbeatMissing);
        }

        string captureContext = NormalizeCaptureContext(heartbeatCaptureContext);
        if (!IsCaptureContextValid(captureContext))
        {
            throw new InvalidOperationException(InputSignalsSourceInvalidContext);
        }

        if (!IsValidIdentifier(agentInstanceId))
        {
            throw new InvalidOperationException(InputSignalsSourceAgentMismatch);
        }

        if (!IsValidIdentifier(sessionId))
        {
            throw new InvalidOperationException(InputSignalsSourceSessionMismatch);
        }

        RecordAcceptedHeartbeatOwnerLocked(
            heartbeatSequence,
            updatedAtUtc,
            agentInstanceId,
            sessionId);
    }

    private bool IsSameProcessHeartbeatStillAcceptableLocked(
        DateTimeOffset nowUtc,
        string agentInstanceId,
        string sessionId)
    {
        if (!_lastAcceptedHeartbeatSequence.HasValue
            || !_lastAcceptedHeartbeatUpdatedAtUtc.HasValue
            || string.IsNullOrWhiteSpace(_lastAcceptedHeartbeatAgentInstanceId)
            || string.IsNullOrWhiteSpace(_lastAcceptedHeartbeatSessionId))
        {
            return false;
        }

        if (!string.Equals(_lastAcceptedHeartbeatAgentInstanceId, agentInstanceId, StringComparison.Ordinal)
            || !string.Equals(_lastAcceptedHeartbeatSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        DateTimeOffset acceptedAtUtc = _lastAcceptedHeartbeatUpdatedAtUtc.Value.ToUniversalTime();
        if (acceptedAtUtc > nowUtc)
        {
            return false;
        }

        return (nowUtc - acceptedAtUtc) <= (CaptureContextFreshnessWindow + SameProcessHeartbeatGraceWindow);
    }

    private void RecordAcceptedHeartbeatOwnerLocked(
        long? heartbeatSequence,
        DateTimeOffset heartbeatUpdatedAtUtc,
        string agentInstanceId,
        string sessionId)
    {
        _lastAcceptedHeartbeatSequence = heartbeatSequence > 0 ? heartbeatSequence : null;
        _lastAcceptedHeartbeatUpdatedAtUtc = heartbeatUpdatedAtUtc.ToUniversalTime();
        _lastAcceptedHeartbeatAgentInstanceId = agentInstanceId;
        _lastAcceptedHeartbeatSessionId = sessionId;
    }

    private string? ResolveOwnershipWarning(UserAgentSessionContextSnapshot? sessionContext)
    {
        string? lastInputAgentInstanceId = NormalizeIdentifier(_lastInputAgentInstanceId);
        string? lastInputSessionId = NormalizeIdentifier(_lastInputSessionId);
        if (lastInputAgentInstanceId is null && lastInputSessionId is null)
        {
            return null;
        }

        if (sessionContext is null)
        {
            return OwnershipWarningHeartbeatMissing;
        }

        string? userAgentInstanceId = NormalizeIdentifier(sessionContext.AgentInstanceId);
        if (userAgentInstanceId is not null
            && lastInputAgentInstanceId is not null
            && !string.Equals(userAgentInstanceId, lastInputAgentInstanceId, StringComparison.Ordinal))
        {
            return OwnershipWarningAgentInstanceMismatch;
        }

        string userAgentSessionId = BuildSessionOwnerId(sessionContext.SessionId);
        if (lastInputSessionId is not null
            && !string.Equals(userAgentSessionId, lastInputSessionId, StringComparison.Ordinal))
        {
            return OwnershipWarningSessionMismatch;
        }

        return null;
    }

    private bool IsObservedSessionDifferentFromLastAcceptedOwner(UserAgentSessionContextSnapshot? observedSessionContext)
    {
        if (observedSessionContext is null)
        {
            return false;
        }

        string? lastInputSessionId = NormalizeIdentifier(_lastInputSessionId);
        if (lastInputSessionId is null)
        {
            return false;
        }

        return !string.Equals(
            BuildSessionOwnerId(observedSessionContext.SessionId),
            lastInputSessionId,
            StringComparison.Ordinal);
    }

    private string BuildDiagnosticsSummaryJson()
    {
        AgentNotificationSuppressionStatus suppressionStatus = _lastNotificationSuppressionStatus;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        string policyVersion = ResolvePolicyVersion();
        (string runtimeStateFreshness, DateTimeOffset? runtimeStateLastSuccessfulRefreshUtc, DateTimeOffset? runtimeStateLastAttemptUtc, string? runtimeStateRefreshError) =
            GetRuntimeStateFreshnessMetadataLocked(nowUtc);
        UserAgentSessionContextSnapshot? observedSessionContext = _userAgentSessionContextStore.ReadCurrent();
        string captureContext = ResolveCaptureContextLocked(nowUtc, out UserAgentSessionContextSnapshot? sessionContext);
        ReconcileStaleLastAcceptedOwnershipLocked(nowUtc, sessionContext);
        string? userAgentInstanceId = NormalizeIdentifier(sessionContext?.AgentInstanceId);
        int? userAgentProcessId = sessionContext?.ProcessId > 0 ? sessionContext.ProcessId : null;
        string? userAgentSessionId = sessionContext is not null
            ? BuildSessionOwnerId(sessionContext.SessionId)
            : null;
        DateTimeOffset? userAgentObservedHeartbeatUpdatedAtUtc = observedSessionContext?.UpdatedAtUtc;
        string? userAgentObservedCaptureContext = NormalizeObservedCaptureContext(observedSessionContext?.CaptureContext);
        string? userAgentObservedSessionId = observedSessionContext is not null
            ? BuildSessionOwnerId(observedSessionContext.SessionId)
            : null;
        string? userAgentStartupSource = NormalizeIdentifier(observedSessionContext?.StartupSource);
        string? userAgentLaunchReason = NormalizeIdentifier(observedSessionContext?.LaunchReason);
        int? userAgentHeartbeatWriteFailureCount = NormalizeHeartbeatWriteFailureCount(observedSessionContext?.HeartbeatWriteFailureCount);
        string? captureSelfTestState = NormalizeCaptureSelfTestState(sessionContext?.CaptureSelfTestState);
        string? captureSelfTestErrorCode = NormalizeIdentifier(sessionContext?.CaptureSelfTestErrorCode);
        string? ownershipWarning = ResolveOwnershipWarning(sessionContext);
        string? captureFailureReason = ResolveCaptureFailureReasonLocked(
            captureContext,
            observedSessionContext,
            ownershipWarning,
            captureSelfTestState);
        string? effectiveLastError = ResolveEffectiveLastError(
            _lastError,
            _latestSnapshot.StateFlagsJson,
            captureSelfTestState,
            captureSelfTestErrorCode);
        AgentStatus status = effectiveLastError is null
            ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
            : AgentStatus.Degraded;

        var payload = new
        {
            generated_at_utc = nowUtc,
            service_running = true,
            status = status.ToString(),
            paused = _paused,
            consent_active = _consentActive,
            backend_connected = _backendConnected,
            last_event_timestamp_utc = _lastEventTimestampUtc,
            backend_instance_id = _backendInstanceId,
            backend_process_id = Environment.ProcessId,
            capture_context = captureContext,
            user_agent_instance_id = userAgentInstanceId,
            user_agent_process_id = userAgentProcessId,
            user_agent_session_id = userAgentSessionId,
            user_agent_observed_heartbeat_utc = userAgentObservedHeartbeatUpdatedAtUtc,
            user_agent_observed_capture_context = userAgentObservedCaptureContext,
            user_agent_observed_session_id = userAgentObservedSessionId,
            user_agent_startup_source = userAgentStartupSource,
            user_agent_launch_reason = userAgentLaunchReason,
            user_agent_heartbeat_write_failure_count = userAgentHeartbeatWriteFailureCount,
            capture_failure_reason = captureFailureReason,
            keyboard_signal_active = IsSignalActive(_lastKeyboardSignalUtc, nowUtc),
            mouse_signal_active = IsSignalActive(_lastMouseSignalUtc, nowUtc),
            last_input_event_utc = _lastInputEventUtc,
            last_input_agent_instance_id = _lastInputAgentInstanceId,
            last_input_session_id = _lastInputSessionId,
            ownership_warning = ownershipWarning,
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
            runtime_state = new
            {
                freshness = runtimeStateFreshness,
                last_successful_refresh_utc = runtimeStateLastSuccessfulRefreshUtc,
                last_attempt_utc = runtimeStateLastAttemptUtc,
                refresh_error = runtimeStateRefreshError
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
                policy = policyVersion
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private RuntimeStateCacheRefreshResult ReadRuntimeStateCacheFromStore(DateTimeOffset nowUtc, long refreshSequence)
    {
        string? refreshError = null;
        AgentBrainSnapshot? latestSnapshotFromStore = null;
        bool latestSnapshotReadSucceeded = false;
        AgentLatestStateDetails? latestStateDetails = null;
        bool latestStateDetailsReadSucceeded = false;
        AgentNotificationSuppressionStatus notificationSuppressionStatus = DefaultNotificationSuppressionStatus;
        bool notificationSuppressionStatusReadSucceeded = false;
        AgentBrightnessActionStatus brightnessActionStatus = DefaultBrightnessActionStatus;
        bool brightnessActionStatusReadSucceeded = false;
        bool dbMigrationFailed = false;
        bool dbMigrationFailedReadSucceeded = false;

        try
        {
            latestSnapshotFromStore = _stateStore.ReadLatestSnapshot();
            latestSnapshotReadSucceeded = true;
        }
        catch (Exception ex)
        {
            refreshError ??= ClassifyRuntimeStateRefreshError(ex);
        }

        try
        {
            latestStateDetails = _stateStore.ReadLatestStateDetails();
            latestStateDetailsReadSucceeded = true;
        }
        catch (Exception ex)
        {
            refreshError ??= ClassifyRuntimeStateRefreshError(ex);
        }

        try
        {
            notificationSuppressionStatus = _stateStore.ReadNotificationSuppressionStatus();
            notificationSuppressionStatusReadSucceeded = true;
        }
        catch (Exception ex)
        {
            refreshError ??= ClassifyRuntimeStateRefreshError(ex);
        }

        try
        {
            brightnessActionStatus = _stateStore.ReadBrightnessActionStatus();
            brightnessActionStatusReadSucceeded = true;
        }
        catch (Exception ex)
        {
            refreshError ??= ClassifyRuntimeStateRefreshError(ex);
        }

        try
        {
            dbMigrationFailed = _stateStore.ReadDbMigrationFailed();
            dbMigrationFailedReadSucceeded = true;
        }
        catch (Exception ex)
        {
            refreshError ??= ClassifyRuntimeStateRefreshError(ex);
        }

        return new RuntimeStateCacheRefreshResult(
            Sequence: refreshSequence,
            AttemptedAtUtc: nowUtc,
            LatestSnapshotFromStore: latestSnapshotFromStore,
            LatestSnapshotReadSucceeded: latestSnapshotReadSucceeded,
            LatestStateDetails: latestStateDetails,
            LatestStateDetailsReadSucceeded: latestStateDetailsReadSucceeded,
            NotificationSuppressionStatus: notificationSuppressionStatus,
            NotificationSuppressionStatusReadSucceeded: notificationSuppressionStatusReadSucceeded,
            BrightnessActionStatus: brightnessActionStatus,
            BrightnessActionStatusReadSucceeded: brightnessActionStatusReadSucceeded,
            DbMigrationFailed: dbMigrationFailed,
            DbMigrationFailedReadSucceeded: dbMigrationFailedReadSucceeded,
            RefreshError: refreshError);
    }

    private void RefreshRuntimeStateCacheFromStoreLocked(DateTimeOffset nowUtc)
    {
        RuntimeStateCacheRefreshResult refresh = ReadRuntimeStateCacheFromStore(
            nowUtc,
            System.Threading.Interlocked.Increment(ref _runtimeStateRefreshSequence));
        ApplyRuntimeStateCacheRefreshLocked(refresh);
    }

    // Refresh DB-derived status enrichments when runtime state advances, not on UI request paths.
    private void ApplyRuntimeStateCacheRefreshLocked(RuntimeStateCacheRefreshResult refresh)
    {
        if (refresh.Sequence < _lastAppliedRuntimeStateRefreshSequence)
        {
            return;
        }

        if (refresh.LatestSnapshotReadSucceeded && refresh.LatestSnapshotFromStore != null)
        {
            (_latestSnapshot, _latestSnapshotBackedByStore) = MergeStoreSnapshotWithNewerActionMetadata(
                currentSnapshot: _latestSnapshot,
                currentSnapshotBackedByStore: _latestSnapshotBackedByStore,
                storeSnapshot: refresh.LatestSnapshotFromStore);
        }

        if (refresh.LatestStateDetailsReadSucceeded)
        {
            _lastLatestStateDetails = refresh.LatestStateDetails;
        }

        if (refresh.NotificationSuppressionStatusReadSucceeded)
        {
            _lastNotificationSuppressionStatus = refresh.NotificationSuppressionStatus;
        }

        if (refresh.BrightnessActionStatusReadSucceeded)
        {
            _lastBrightnessActionStatus = refresh.BrightnessActionStatus;
        }

        if (refresh.DbMigrationFailedReadSucceeded)
        {
            _lastDbMigrationFailed = refresh.DbMigrationFailed;
        }

        _runtimeStateLastAttemptUtc = refresh.AttemptedAtUtc;
        if (refresh.RefreshError is null)
        {
            _runtimeStateLastSuccessfulRefreshUtc = refresh.AttemptedAtUtc;
            _runtimeStateRefreshError = null;
        }
        else
        {
            _runtimeStateRefreshError = refresh.RefreshError;
        }

        _lastAppliedRuntimeStateRefreshSequence = refresh.Sequence;
    }

    private void ResetRuntimeStateCacheLocked()
    {
        _lastLatestStateDetails = null;
        _lastNotificationSuppressionStatus = DefaultNotificationSuppressionStatus;
        _lastBrightnessActionStatus = DefaultBrightnessActionStatus;
        _lastDbMigrationFailed = false;
        _runtimeStateLastSuccessfulRefreshUtc = null;
        _runtimeStateLastAttemptUtc = null;
        _runtimeStateRefreshError = null;
        _lastCurrentSnapshotProjection = null;
        _lastStatusSnapshot = null;
    }

    private (string Freshness, DateTimeOffset? LastSuccessfulRefreshUtc, DateTimeOffset? LastAttemptUtc, string? RefreshError)
        GetRuntimeStateFreshnessMetadataLocked(DateTimeOffset nowUtc)
    {
        bool stale = !_runtimeStateLastSuccessfulRefreshUtc.HasValue
            || !string.IsNullOrWhiteSpace(_runtimeStateRefreshError)
            || (nowUtc - _runtimeStateLastSuccessfulRefreshUtc.Value) > RuntimeStateStaleAfter;

        return (
            Freshness: stale ? "stale" : "fresh",
            LastSuccessfulRefreshUtc: _runtimeStateLastSuccessfulRefreshUtc,
            LastAttemptUtc: _runtimeStateLastAttemptUtc,
            RefreshError: _runtimeStateRefreshError);
    }

    private static (AgentBrainSnapshot Snapshot, bool BackedByStore) MergeStoreSnapshotWithNewerActionMetadata(
        AgentBrainSnapshot currentSnapshot,
        bool currentSnapshotBackedByStore,
        AgentBrainSnapshot storeSnapshot)
    {
        bool preserveCurrentActionMetadata =
            !currentSnapshotBackedByStore
            && !string.Equals(currentSnapshot.LastAction, "starting", StringComparison.Ordinal);

        if (!preserveCurrentActionMetadata)
        {
            return (storeSnapshot, true);
        }

        return (storeSnapshot with
        {
            LastAction = currentSnapshot.LastAction,
            ActivationWindowTriggered = currentSnapshot.ActivationWindowTriggered,
            CooldownTriggered = currentSnapshot.CooldownTriggered,
            ActivationWindowOpen = currentSnapshot.ActivationWindowOpen,
            Contributors = currentSnapshot.Contributors,
            PolicyId = currentSnapshot.PolicyId,
            Domain = currentSnapshot.Domain,
            EscalationLevel = currentSnapshot.EscalationLevel,
            PlannedEndTimestampUtc = currentSnapshot.PlannedEndTimestampUtc,
            ActualEndTimestampUtc = currentSnapshot.ActualEndTimestampUtc,
            CooldownEndTimestampUtc = currentSnapshot.CooldownEndTimestampUtc
        }, false);
    }

    private readonly record struct RuntimeStateCacheRefreshResult(
        long Sequence,
        DateTimeOffset AttemptedAtUtc,
        AgentBrainSnapshot? LatestSnapshotFromStore,
        bool LatestSnapshotReadSucceeded,
        AgentLatestStateDetails? LatestStateDetails,
        bool LatestStateDetailsReadSucceeded,
        AgentNotificationSuppressionStatus NotificationSuppressionStatus,
        bool NotificationSuppressionStatusReadSucceeded,
        AgentBrightnessActionStatus BrightnessActionStatus,
        bool BrightnessActionStatusReadSucceeded,
        bool DbMigrationFailed,
        bool DbMigrationFailedReadSucceeded,
        string? RefreshError);

    private static string ClassifyRuntimeStateRefreshError(Exception ex)
    {
        string message = ex.Message ?? string.Empty;
        if (message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("database is busy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("locked", StringComparison.OrdinalIgnoreCase))
        {
            return "db_busy";
        }

        if (message.Contains("readonly", StringComparison.OrdinalIgnoreCase)
            || message.Contains("disk i/o", StringComparison.OrdinalIgnoreCase))
        {
            return "db_unavailable";
        }

        return $"refresh_failed:{ex.GetType().Name}";
    }

    private static string? ResolveEffectiveLastError(
        string? controllerLastError,
        string stateFlagsJson,
        string? captureSelfTestState = null,
        string? captureSelfTestErrorCode = null)
    {
        if (!string.IsNullOrWhiteSpace(controllerLastError))
        {
            return controllerLastError;
        }

        if (string.Equals(captureSelfTestState, CaptureSelfTestStateFailed, StringComparison.Ordinal))
        {
            string normalizedErrorCode = string.IsNullOrWhiteSpace(captureSelfTestErrorCode)
                ? "unknown"
                : captureSelfTestErrorCode.Trim().ToLowerInvariant();
            return $"capture_self_test_failed:{normalizedErrorCode}";
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

    private static bool IsDbMigrationFailure(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
        {
            return false;
        }

        return errorText.Contains("db_migration_failed", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("migration", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("readonly database", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("sqlite error", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolvePolicyVersion()
    {
        string version = _runtimePolicyProvider.GetEnvelope().Version;
        return string.IsNullOrWhiteSpace(version) ? "local-default" : version;
    }

    private static int ComputeDeepWorkStreakSeconds(
        bool deepWorkActive,
        long deepWorkStartedAtUtcMs,
        double fallbackSustainedAttentionSeconds)
    {
        if (!deepWorkActive)
        {
            return 0;
        }

        if (deepWorkStartedAtUtcMs > 0)
        {
            DateTimeOffset startedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(deepWorkStartedAtUtcMs);
            double elapsedSeconds = (DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds;
            return (int)Math.Max(0, Math.Floor(elapsedSeconds));
        }

        return (int)Math.Max(0, Math.Floor(fallbackSustainedAttentionSeconds));
    }

    private static bool TryReadBoolFromStateFlags(string? stateFlagsJson, string propertyName, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(stateFlagsJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(propertyName, out JsonElement prop))
            {
                return false;
            }

            switch (prop.ValueKind)
            {
                case JsonValueKind.True:
                    value = true;
                    return true;
                case JsonValueKind.False:
                    value = false;
                    return true;
                case JsonValueKind.Number when prop.TryGetInt32(out int intValue):
                    value = intValue != 0;
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static double ReadDoubleFromStateFlags(string? stateFlagsJson, string propertyName, double fallback)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return fallback;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(propertyName, out JsonElement prop))
            {
                return fallback;
            }

            return prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double parsed) && double.IsFinite(parsed)
                ? Math.Clamp(parsed, 0d, 1d)
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static long ReadInt64FromStateFlags(string? stateFlagsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return 0;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(propertyName, out JsonElement prop))
            {
                return 0;
            }

            return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long parsed)
                ? Math.Max(0, parsed)
                : 0;
        }
        catch
        {
            return 0;
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
            string[] candidatePaths =
            [
                Path.Combine(AppContext.BaseDirectory, "Porta.UserAgent.exe"),
                Path.Combine(AppContext.BaseDirectory, "AriTray.exe")
            ];

            string? path = candidatePaths.FirstOrDefault(File.Exists);
            if (path == null)
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
