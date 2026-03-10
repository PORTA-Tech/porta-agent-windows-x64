using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string DefaultPolicyVersion = "1.0.0";
    private const int MaxSignalIdLength = 128;
    private const int MaxFocusHashLength = 128;
    private const string CaptureContextUnknown = UserAgentCaptureContexts.Unknown;
    private const string CaptureContextUserSession = UserAgentCaptureContexts.UserSession;
    private const string CaptureContextServiceSession = UserAgentCaptureContexts.ServiceSession;
    private const string CaptureFailureReasonUnknown = "CAPTURE_CONTEXT_UNKNOWN";
    private const string CaptureFailureReasonServiceSession = "CAPTURE_CONTEXT_SERVICE_SESSION";
    private const string OwnershipWarningHeartbeatMissing = "USER_AGENT_HEARTBEAT_MISSING";
    private const string OwnershipWarningAgentInstanceMismatch = "USER_AGENT_INSTANCE_MISMATCH";
    private const string OwnershipWarningSessionMismatch = "USER_AGENT_SESSION_MISMATCH";
    private const string InputSignalsSourceHeartbeatMissing = "input_signals_source_heartbeat_missing";
    private const string InputSignalsSourceInvalidContext = "input_signals_source_invalid_context";
    private const string InputSignalsSourceAgentMismatch = "input_signals_source_agent_mismatch";
    private const string InputSignalsSourceSessionMismatch = "input_signals_source_session_mismatch";
    private static readonly TimeSpan SignalHealthActiveWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CaptureContextFreshnessWindow = TimeSpan.FromSeconds(30);
    private static readonly string ConsentDisclosureHash = ComputeSha256Hex(ConsentDisclosureText);
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex FocusHashPattern = new(@"^[a-fA-F0-9-_.:]+$", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly IBrainStateStore _stateStore;
    private readonly IInputSignalsIngress? _inputSignalsIngress;
    private readonly IUserAgentSessionContextStore _userAgentSessionContextStore;
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
        : this(
            new BucketFinalizerStateStore(new SqliteBrainStateStore()),
            new UserAgentSessionContextStore())
    {
    }

    public AgentCoreController(
        IBrainStateStore stateStore,
        IUserAgentSessionContextStore? userAgentSessionContextStore = null)
    {
        _stateStore = stateStore;
        _inputSignalsIngress = stateStore as IInputSignalsIngress;
        _userAgentSessionContextStore = userAgentSessionContextStore ?? new UserAgentSessionContextStore();
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
            AgentLatestStateDetails? latestState = _stateStore.ReadLatestStateDetails();
            string stateFlagsJson = latestState?.StateFlagsJson ?? _latestSnapshot.StateFlagsJson;
            bool overloadActive = latestState?.OverloadActive
                ?? (TryReadBoolFromStateFlags(stateFlagsJson, "overload_active", out bool parsedOverloadActive)
                    && parsedOverloadActive);
            bool deepWorkActive = TryReadBoolFromStateFlags(stateFlagsJson, "deep_work_active", out bool parsedDeepWorkActive)
                && parsedDeepWorkActive;
            long deepWorkStartedAtUtcMs = ReadInt64FromStateFlags(stateFlagsJson, "deep_work_started_at_utc_ms");
            double fallbackSustainedAttentionSeconds = latestState?.SustainedAttentionDurationSec ?? _latestSnapshot.SustainedAttentionDurationSec;
            int deepWorkStreakSeconds = ComputeDeepWorkStreakSeconds(
                deepWorkActive,
                deepWorkStartedAtUtcMs,
                fallbackSustainedAttentionSeconds);
            PresentedVisualMode visualMode = PresentedVisualModeMapper.Map(overloadActive, deepWorkActive);
            string? latestContributorsJson = latestState?.ContributorsJson;
            string? latestWhyJson = _stateStore.ReadRecentEvents(1).FirstOrDefault()?.WhyJson;
            string? effectiveLastError = ResolveEffectiveLastError(_lastError, _latestSnapshot.StateFlagsJson);
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
            bool dbMigrationFailedFromStore = _stateStore.ReadDbMigrationFailed();
            bool dbMigrationFailed = IsDbMigrationFailure(effectiveLastError);
            dbMigrationFailed = dbMigrationFailed || dbMigrationFailedFromStore;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            string captureContext = ResolveCaptureContextLocked(nowUtc, out UserAgentSessionContextSnapshot? sessionContext);
            bool keyboardSignalActive = IsSignalActive(_lastKeyboardSignalUtc, nowUtc);
            bool mouseSignalActive = IsSignalActive(_lastMouseSignalUtc, nowUtc);
            string? userAgentInstanceId = NormalizeIdentifier(sessionContext?.AgentInstanceId);
            int? userAgentProcessId = sessionContext?.ProcessId > 0 ? sessionContext.ProcessId : null;
            string? userAgentSessionId = sessionContext is not null
                ? BuildSessionOwnerId(sessionContext.SessionId)
                : null;
            DateTimeOffset? userAgentHeartbeatUpdatedAtUtc = sessionContext?.UpdatedAtUtc;
            string? ownershipWarning = ResolveOwnershipWarning(sessionContext);
            string? captureFailureReason = ResolveCaptureFailureReason(captureContext);
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
                LastUpdatedUtc: nowUtc);
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

        lock (_gate)
        {
            TryAutoResumeLocked();
            _lastInputAgentInstanceId = agentInstanceId;
            _lastInputSessionId = sessionId;
            ValidateInputSignalSourceLocked(nowUtc, agentInstanceId, sessionId);
            _lastInputEventUtc = packet.LastInputEventUtc?.ToUniversalTime() ?? fallbackEventUtc;
            if (keyboardKeydownCount > 0 || keyboardBackspaceCount > 0)
            {
                _lastKeyboardSignalUtc = _lastInputEventUtc;
            }

            if (mouseWheelDelta != 0 || mouseMoveDx is not 0d || mouseMoveDy is not 0d)
            {
                _lastMouseSignalUtc = _lastInputEventUtc;
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

            _inputSignalsIngress.IngestInputSignals(new InputSignalsPacket(
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
                NotificationToastsEnabled: packet.NotificationToastsEnabled));

            if (packet.LastInputEventUtc is DateTimeOffset lastInputEventUtc)
            {
                _lastEventTimestampUtc = lastInputEventUtc;
            }
            else
            {
                _lastEventTimestampUtc = fallbackEventUtc;
            }
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
            "break_suggestion" => "Break suggestion was presented in the user interface.",
            "focus_lock_in_suggestion" => "Focus lock-in suggestion was presented in the user interface.",
            "stretch_break_suggestion" => "Stretch break suggestion was presented in the user interface.",
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

    private static string? ResolveCaptureFailureReason(string captureContext)
    {
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

    private void ValidateInputSignalSourceLocked(
        DateTimeOffset nowUtc,
        string agentInstanceId,
        string sessionId)
    {
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

    private string BuildDiagnosticsSummaryJson()
    {
        AgentNotificationSuppressionStatus suppressionStatus = _stateStore.ReadNotificationSuppressionStatus();
        string? effectiveLastError = ResolveEffectiveLastError(_lastError, _latestSnapshot.StateFlagsJson);
        AgentStatus status = effectiveLastError is null
            ? (_paused ? AgentStatus.Degraded : AgentStatus.Running)
            : AgentStatus.Degraded;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        string captureContext = ResolveCaptureContextLocked(nowUtc, out UserAgentSessionContextSnapshot? sessionContext);
        string? userAgentInstanceId = NormalizeIdentifier(sessionContext?.AgentInstanceId);
        int? userAgentProcessId = sessionContext?.ProcessId > 0 ? sessionContext.ProcessId : null;
        string? userAgentSessionId = sessionContext is not null
            ? BuildSessionOwnerId(sessionContext.SessionId)
            : null;
        string? ownershipWarning = ResolveOwnershipWarning(sessionContext);
        string? captureFailureReason = ResolveCaptureFailureReason(captureContext);

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
