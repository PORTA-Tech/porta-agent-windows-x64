namespace Porta.UserAgent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core;
using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;
using Porta.Agent.Runtime.Input;
using Porta.UserAgent.Interventions;
using Porta.UserAgent.Input;
using System.Diagnostics;

public sealed class UserAgentHost : BackgroundService, IDisposable
{
    private static readonly TimeSpan PassiveSamplingPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WidgetDetailRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly ILogger<UserAgentHost> _logger;
    private readonly IUserAgentSessionContextStore _sessionContextStore;
    private readonly UserAgentStartupMetadata _startupMetadata;
    private readonly LocalApiClient _localApiClient = new();
    private readonly UserDesktopWidgetPresenter _toastRenderer = new();
    private readonly UserSessionWindowsNotificationController _notificationController = new();
    private readonly UserAgentNotificationSnapshotStore _notificationSnapshotStore = new();
    private readonly UserInterventionPreferenceStore _interventionPreferenceStore = new();
    private readonly UserAgentInterventionExecutor _interventionExecutor;
    private readonly UserSessionSignalAggregator _signalAggregator = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly UserKeyboardActivityPoller _keyboardPoller = new();
    private readonly MouseHook _mouseHook = new();
    private readonly CaptureSelfTestCoordinator _captureSelfTest = new();
    private readonly string _agentInstanceId = BuildAgentInstanceId();
    private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
    private long _lastRecoveryEventId;
    private long _lastCommunicationEventId;
    private UserInterventionToastRenderer.UserTrayVisualState? _lastTrayState;
    private UserDesktopWidgetState? _lastWidgetState;
    private IReadOnlyList<LocalApiEventResponse> _widgetDetailEvents = [];
    private LocalApiTimelineResponse? _widgetDetailTimeline;
    private LocalApiSettingsResponse? _widgetDetailSettings;
    private DateTimeOffset _lastWidgetDetailRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUiRefreshUtc = DateTimeOffset.MinValue;
    private bool _widgetFallbackLogged;
    private bool _hooksInstalled;
    private bool _keyboardCaptureAvailable;
    private bool _mouseCaptureAvailable;
    private bool _localApiUnavailableLogged;
    private int _heartbeatWriteFailureCount;
    private bool _heartbeatReadyLogged;
    private bool _systemEventsSubscribed;
    private int _sessionId;
    private string _captureContext = UserAgentCaptureContexts.Unknown;

    public UserAgentHost(
        ILogger<UserAgentHost> logger,
        IUserAgentSessionContextStore sessionContextStore,
        UserAgentStartupMetadata startupMetadata)
    {
        _logger = logger;
        _sessionContextStore = sessionContextStore;
        _startupMetadata = startupMetadata;
        _interventionExecutor = new UserAgentInterventionExecutor(
            _toastRenderer,
            new UserSessionPresentationGate(),
            _notificationController,
            _notificationSnapshotStore,
            _interventionPreferenceStore);
        _keyboardHook.KeyChanged += OnKeyboardChanged;
        _keyboardPoller.KeyChanged += OnKeyboardChanged;
        _mouseHook.MouseMoved += OnMouseMoved;
        _mouseHook.ScrollDeltaChanged += OnMouseWheelChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Process currentProcess = Process.GetCurrentProcess();
        _sessionId = currentProcess.SessionId;
        _captureContext = IsInteractiveUserSession(_sessionId)
            ? UserAgentCaptureContexts.UserSession
            : UserAgentCaptureContexts.ServiceSession;
        _windowStartUtc = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Porta.UserAgent running in process {ProcessId} with session {SessionId}, capture context {CaptureContext}, agent instance {AgentInstanceId}.",
            Environment.ProcessId,
            _sessionId,
            _captureContext,
            _agentInstanceId);
        UserAgentFileLog.Info(
            $"user_agent_host_started source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} capture_context={_captureContext} agent_instance={_agentInstanceId} heartbeat_path={ResolveSessionContextPath()}");

        if (_captureContext == UserAgentCaptureContexts.ServiceSession)
        {
            _logger.LogWarning("Porta.UserAgent started outside an interactive desktop session; capture stays in heartbeat-only mode.");
            UserAgentFileLog.Info(
                $"user_agent_non_interactive_session source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId}");
        }
        else
        {
            StartCaptureHooks();
            SubscribeToSessionSwitchNotifications();
            ArmCaptureSelfTest(_startupMetadata.EffectiveLaunchReason);
        }

        UpdateTrayState(new(
            UserInterventionToastRenderer.UserTrayMode.Normal,
            "PORTA | Starting"));

        await RefreshLocalUiSignalsAsync(stoppingToken).ConfigureAwait(false);
        _lastUiRefreshUtc = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(PassiveSamplingPeriod);

        while (!stoppingToken.IsCancellationRequested)
        {
            PublishSessionContext();

            if (_captureContext == UserAgentCaptureContexts.UserSession)
            {
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                SampleAmbientSignals(nowUtc);
                await EvaluateCaptureSelfTestAsync(nowUtc, stoppingToken).ConfigureAwait(false);
                if ((nowUtc - _lastUiRefreshUtc) >= UiRefreshInterval)
                {
                    await RefreshLocalUiSignalsAsync(stoppingToken).ConfigureAwait(false);
                    _lastUiRefreshUtc = nowUtc;
                }

                if ((nowUtc - _windowStartUtc) >= TimeSpan.FromSeconds(Time.bucketSeconds))
                {
                    await FlushSignalsAsync(stoppingToken).ConfigureAwait(false);
                    await RefreshLocalUiSignalsAsync(stoppingToken).ConfigureAwait(false);
                    _lastUiRefreshUtc = DateTimeOffset.UtcNow;
                    await PollPendingInterventionsAsync(stoppingToken).ConfigureAwait(false);
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Porta.UserAgent stopping.");
        UserAgentFileLog.Info(
            $"user_agent_host_stopping source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId}");
        UnsubscribeFromSessionSwitchNotifications();
        StopCaptureHooks();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        UnsubscribeFromSessionSwitchNotifications();
        StopCaptureHooks();
        _keyboardHook.Dispose();
        _keyboardPoller.Dispose();
        _mouseHook.Dispose();
        _toastRenderer.Dispose();
        _localApiClient.Dispose();
        base.Dispose();
    }

    private async Task RefreshLocalUiSignalsAsync(CancellationToken token)
    {
        if (!_localApiClient.IsConfigured)
        {
            UpdateTrayState(new(
                UserInterventionToastRenderer.UserTrayMode.Unavailable,
                "PORTA | Waiting for core service"));
            return;
        }

        LocalApiStatusResponse? status = await _localApiClient.GetStatusAsync(token).ConfigureAwait(false);
        LocalApiCurrentSnapshotResponse? snapshot = await _localApiClient.GetCurrentSnapshotAsync(token).ConfigureAwait(false);
        IReadOnlyList<LocalApiEventResponse> recentEvents = await _localApiClient
            .GetRecentEventsAsync(limit: 12, sinceUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(3), token)
            .ConfigureAwait(false);

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        WidgetDetailSnapshot widgetDetails = _toastRenderer.SupportsWidgetSurface
            ? await RefreshWidgetDetailsAsync(nowUtc, token).ConfigureAwait(false)
            : WidgetDetailSnapshot.Empty;
        UserAgentVisualSignals signals = UserAgentVisualSignalsAdapter.Build(
            status,
            snapshot,
            recentEvents,
            nowUtc,
            _lastRecoveryEventId);

        UserCommunicationToast? captureDegradedToast = UserCommunicationRouter.ResolveCaptureDegradedState(status, snapshot);

        UpdateTrayState(new(
            MapTrayMode(signals.Mode),
            signals.TooltipText));
        UpdateWidgetState(UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            signals,
            captureDegradedToast,
            widgetDetails.RecentEvents,
            widgetDetails.Timeline,
            widgetDetails.Settings,
            nowUtc));

        UserCommunicationToast? recentEventToast = UserCommunicationRouter.ResolveRecentEventToast(
            recentEvents,
            nowUtc,
            _lastCommunicationEventId);
        if (recentEventToast != null)
        {
            if (TryShowCommunicationToast(recentEventToast))
            {
                _lastCommunicationEventId = recentEventToast.EventId;
                return;
            }
        }

        if (!signals.ShowRecoveryToast || signals.RecoveryEventId <= 0)
        {
            return;
        }

        _lastRecoveryEventId = signals.RecoveryEventId;
        if (!UserSessionNativeSignals.IsInterventionPresentationAvailable(out _))
        {
            return;
        }

        if (!_toastRenderer.TryShow("PORTA", signals.RecoveryMessage, out string errorCode))
        {
            _logger.LogDebug(
                "Failed to present overload recovery message for event {EventId}. Error={ErrorCode}",
                signals.RecoveryEventId,
                errorCode);
        }
    }

    private async Task FlushSignalsAsync(CancellationToken token)
    {
        DateTimeOffset windowEndUtc = DateTimeOffset.UtcNow;
        DateTimeOffset windowStartUtc = _windowStartUtc;
        if (windowEndUtc <= windowStartUtc)
        {
            return;
        }

        SampleAmbientSignals(windowEndUtc);
        UserSessionSignalSnapshot snapshot = _signalAggregator.SnapshotAndReset();
        string sessionId = BuildSessionId(_sessionId);
        double windowSeconds = Math.Max(0.001d, (windowEndUtc - windowStartUtc).TotalSeconds);
        string? fallbackFocusAppHash = UserSessionNativeSignals.TryGetForegroundAppHash();
        double fallbackIdleSeconds = UserSessionNativeSignals.ReadIdleDurationSeconds();
        UserSessionKeyboardTelemetrySummary keyboard = snapshot.BuildKeyboardSummary(windowSeconds);
        UserSessionMouseTelemetrySummary mouse = snapshot.BuildMouseSummary(windowSeconds);
        UserSessionFocusTelemetrySummary focus = snapshot.BuildFocusSummary(windowStartUtc, windowEndUtc, fallbackFocusAppHash);
        UserSessionIdleTelemetrySummary idle = snapshot.BuildIdleSummary(windowStartUtc, windowEndUtc, fallbackIdleSeconds);
        bool? notificationToastsEnabled = ReadNotificationToastsEnabled();

        var request = new LocalApiInputSignalsRequest(
            SchemaVer: 2,
            AgentInstanceId: _agentInstanceId,
            SessionId: sessionId,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: snapshot.KeyboardKeydownCount,
            KeyboardBackspaceCount: snapshot.KeyboardBackspaceCount,
            MouseWheelDelta: snapshot.MouseWheelDelta,
            MouseMoveDx: snapshot.MouseMoveDx,
            MouseMoveDy: snapshot.MouseMoveDy,
            FocusAppHash: focus.DominantAppHash ?? fallbackFocusAppHash,
            IdleSeconds: fallbackIdleSeconds,
            LastInputEventUtc: snapshot.LastInputEventUtc,
            KeyboardIkiMeanMs: keyboard.IkiMeanMs,
            KeyboardIkiStdMs: keyboard.IkiStdMs,
            KeyboardIkiCv: keyboard.IkiCv,
            KeyboardBurstCount: keyboard.BurstCount,
            KeyboardBurstRate: keyboard.BurstRate,
            KeyboardCorrectionSpikeCount: keyboard.CorrectionSpikeCount,
            KeyboardCorrectionSpikeRate: keyboard.CorrectionSpikeRate,
            MouseMoveSampleCount: mouse.MoveSampleCount,
            MousePathLengthPx: mouse.PathLengthPx,
            MouseSpeedMeanPxPerSec: mouse.SpeedMeanPxPerSec,
            MouseJerkP95: mouse.JerkP95,
            MouseMicroCorrectionCount: mouse.MicroCorrectionCount,
            MouseMicroCorrectionRate: mouse.MicroCorrectionRate,
            MouseWheelEventCount: mouse.WheelEventCount,
            MouseScrollBurstCount: mouse.ScrollBurstCount,
            MouseScrollBurstRate: mouse.ScrollBurstRate,
            MouseScrollSpeedMean: mouse.ScrollSpeedMean,
            FocusObservationCount: focus.ObservationCount,
            FocusSwitchCount: focus.SwitchCount,
            FocusActiveAppCount: focus.ActiveAppCount,
            FocusAppEntropy: focus.AppEntropy,
            IdleObservationCount: idle.ObservationCount,
            IdleSecondsWithinWindow: idle.IdleSecondsWithinWindow,
            MicroIdleCount: idle.MicroIdleCount,
            MicroIdleSeconds: idle.MicroIdleSeconds,
            BreakSecondsWithinWindow: idle.BreakSecondsWithinWindow,
            ShortIdleBoundaryObserved: idle.ShortIdleBoundaryObserved,
            NotificationToastsEnabled: notificationToastsEnabled);

        if (!_localApiClient.IsConfigured)
        {
            _signalAggregator.Requeue(snapshot);
            if (!_localApiUnavailableLogged)
            {
                _localApiUnavailableLogged = true;
                _logger.LogWarning("Porta.UserAgent could not find local API settings yet. Waiting for the core service.");
            }

            return;
        }

        LocalApiAckResponse? ack = await _localApiClient.SendInputSignalsAsync(request, token).ConfigureAwait(false);
        if (ack?.Success == true)
        {
            _windowStartUtc = windowEndUtc;
            _localApiUnavailableLogged = false;
            _captureSelfTest.RecordPublishAccepted(windowEndUtc);
            return;
        }

        _signalAggregator.Requeue(snapshot);
        _logger.LogWarning(
            "Porta.UserAgent failed to publish input signals for window {WindowStartUtc}..{WindowEndUtc}. Ack={AckMessage}.",
            windowStartUtc,
            windowEndUtc,
            ack?.Message ?? "null");
    }

    private async Task PollPendingInterventionsAsync(CancellationToken token)
    {
        if (!_localApiClient.IsConfigured)
        {
            return;
        }

        await FlushCompletedInterventionAcksAsync(token).ConfigureAwait(false);

        string sessionId = BuildSessionId(_sessionId);
        IReadOnlyList<LocalApiInterventionPendingResponse> pending =
            await _localApiClient.GetPendingInterventionsAsync(sessionId, limit: 12, token).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            await FlushCompletedInterventionAcksAsync(token).ConfigureAwait(false);
            return;
        }

        foreach (LocalApiInterventionPendingResponse intervention in pending)
        {
            await ExecuteInterventionAsync(intervention, token).ConfigureAwait(false);
        }

        await FlushCompletedInterventionAcksAsync(token).ConfigureAwait(false);
    }

    private async Task ExecuteInterventionAsync(LocalApiInterventionPendingResponse intervention, CancellationToken token)
    {
        LocalApiInterventionAckRequest? ack = _interventionExecutor.ExecuteOrReplay(
            intervention,
            BuildSessionId(_sessionId),
            _agentInstanceId);
        if (ack != null)
        {
            await SendInterventionAckAsync(ack, token).ConfigureAwait(false);
        }
    }

    private async Task FlushCompletedInterventionAcksAsync(CancellationToken token)
    {
        while (_interventionExecutor.TryDequeueCompletedAck(out LocalApiInterventionAckRequest ack))
        {
            await SendInterventionAckAsync(ack, token).ConfigureAwait(false);
        }
    }

    private async Task SendInterventionAckAsync(LocalApiInterventionAckRequest ack, CancellationToken token)
    {
        LocalApiAckResponse? response = await _localApiClient.AcknowledgeInterventionAsync(ack, token).ConfigureAwait(false);
        if (response?.Success == true)
        {
            return;
        }

        _logger.LogWarning(
            "Failed to acknowledge intervention {InterventionId}. Result={Result} Response={ResponseMessage}",
            ack.InterventionId,
            ack.Result,
            response?.Message ?? "null");
    }

    private void PublishSessionContext()
    {
        try
        {
            int heartbeatWriteFailureCount = _heartbeatWriteFailureCount;
            CaptureSelfTestHeartbeatProjection selfTest = _captureSelfTest.GetHeartbeatProjection();
            _sessionContextStore.WriteCurrent(new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: _captureContext,
                ProcessId: Environment.ProcessId,
                SessionId: _sessionId,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: _agentInstanceId,
                StartupSource: _startupMetadata.Source,
                LaunchReason: _startupMetadata.EffectiveLaunchReason,
                HeartbeatWriteFailureCount: heartbeatWriteFailureCount,
                CaptureSelfTestState: selfTest.State,
                CaptureSelfTestErrorCode: selfTest.ErrorCode,
                CaptureSelfTestRemediationCount: selfTest.RemediationCount));

            _captureSelfTest.RecordHeartbeatPublished(DateTimeOffset.UtcNow);

            if (!_heartbeatReadyLogged)
            {
                _heartbeatReadyLogged = true;
                UserAgentFileLog.Info(
                    $"heartbeat_publishing_ready source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} path={ResolveSessionContextPath()}");
            }

            if (heartbeatWriteFailureCount > 0)
            {
                UserAgentFileLog.Info(
                    $"heartbeat_write_recovered source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} path={ResolveSessionContextPath()} failures={heartbeatWriteFailureCount}");
                _heartbeatWriteFailureCount = 0;
            }
        }
        catch (Exception ex)
        {
            _heartbeatWriteFailureCount++;
            if (ShouldLogHeartbeatWriteFailure(_heartbeatWriteFailureCount))
            {
                UserAgentFileLog.Error(
                    $"heartbeat_write_failed source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} path={ResolveSessionContextPath()} failure_count={_heartbeatWriteFailureCount}",
                    ex);
            }
            _logger.LogError(ex, "Failed to publish user-session capture context heartbeat.");
        }
    }

    private void StartCaptureHooks()
    {
        bool keyboardInstalled = _keyboardHook.Install();
        if (!keyboardInstalled)
        {
            _logger.LogWarning("Keyboard hook did not install. Falling back to keyboard polling.");
            keyboardInstalled = _keyboardPoller.Install();
        }

        bool mouseInstalled = _mouseHook.Install();
        _keyboardCaptureAvailable = keyboardInstalled;
        _mouseCaptureAvailable = mouseInstalled;
        _hooksInstalled = keyboardInstalled || mouseInstalled;

        if (!keyboardInstalled)
        {
            _logger.LogWarning("Keyboard capture is unavailable. Keyboard capture will stay inactive.");
        }

        if (!mouseInstalled)
        {
            _logger.LogWarning("Mouse hook did not install. Mouse capture will stay inactive.");
        }

        if (_hooksInstalled)
        {
            _logger.LogInformation(
                "User-session input capture ready. keyboard={KeyboardInstalled} mouse={MouseInstalled}",
                keyboardInstalled,
                mouseInstalled);
            UserAgentFileLog.Info(
                $"capture_hooks_ready source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} keyboard={keyboardInstalled} mouse={mouseInstalled}");
        }
        else
        {
            UserAgentFileLog.Info(
                $"capture_hooks_unavailable source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} keyboard={keyboardInstalled} mouse={mouseInstalled}");
        }
    }

    private void StopCaptureHooks()
    {
        if (!_hooksInstalled)
        {
            _keyboardCaptureAvailable = false;
            _mouseCaptureAvailable = false;
            return;
        }

        _keyboardHook.Uninstall();
        _keyboardPoller.Uninstall();
        _mouseHook.Uninstall();
        _hooksInstalled = false;
        _keyboardCaptureAvailable = false;
        _mouseCaptureAvailable = false;
    }

    private void OnKeyboardChanged(bool isKeyDown, bool isBackspace, bool isInjected)
    {
        if (!isKeyDown || isInjected)
        {
            return;
        }

        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.RecordRawInput(observedAtUtc);
        _signalAggregator.RecordKeyboard(isBackspace, observedAtUtc);
    }

    private void OnMouseMoved(int dx, int dy)
    {
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.RecordRawInput(observedAtUtc);
        _signalAggregator.RecordMouseMove(dx, dy, observedAtUtc);
    }

    private void OnMouseWheelChanged(int delta, bool _)
    {
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.RecordRawInput(observedAtUtc);
        _signalAggregator.RecordMouseWheel(delta, observedAtUtc);
    }

    private void SampleAmbientSignals(DateTimeOffset sampleUtc)
    {
        _signalAggregator.RecordFocusObservation(UserSessionNativeSignals.TryGetForegroundAppHash(), sampleUtc);
        _signalAggregator.RecordIdleObservation(UserSessionNativeSignals.ReadIdleDurationSeconds(), sampleUtc);
    }

    private bool? ReadNotificationToastsEnabled()
    {
        NotificationControllerSnapshotResult snapshotResult = _notificationController.GetSnapshot();
        return snapshotResult.Succeeded
            ? snapshotResult.Snapshot.ToastBannersEnabled
            : null;
    }

    private async Task EvaluateCaptureSelfTestAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        double idleSeconds = UserSessionNativeSignals.ReadIdleDurationSeconds();
        CaptureSelfTestDecision decision = _captureSelfTest.Evaluate(
            nowUtc,
            heartbeatFresh: _heartbeatWriteFailureCount == 0,
            hooksAvailable: _keyboardCaptureAvailable || _mouseCaptureAvailable,
            idleSeconds);

        if (decision.LogMessage is not null)
        {
            _logger.LogInformation("{Message}", decision.LogMessage);
            UserAgentFileLog.Info(decision.LogMessage);
        }

        if (decision.RequestHookReset)
        {
            StopCaptureHooks();
            StartCaptureHooks();
        }

        if (decision.RequestImmediateFlush)
        {
            await FlushSignalsAsync(token).ConfigureAwait(false);
        }

        if (decision.NotifyUserFailure)
        {
            ShowCaptureSelfTestFailureToast(decision);
        }
    }

    private void ArmCaptureSelfTest(string triggerReason)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.Arm(triggerReason, nowUtc);
        string reason = string.IsNullOrWhiteSpace(triggerReason) ? "unspecified" : triggerReason.Trim();
        UserAgentFileLog.Info(
            $"capture_self_test_armed source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} trigger={reason} pid={Environment.ProcessId} session={_sessionId}");
    }

    private void SubscribeToSessionSwitchNotifications()
    {
        if (_systemEventsSubscribed)
        {
            return;
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        _systemEventsSubscribed = true;
    }

    private void UnsubscribeFromSessionSwitchNotifications()
    {
        if (!_systemEventsSubscribed)
        {
            return;
        }

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch
        {
        }

        _systemEventsSubscribed = false;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not SessionSwitchReason.SessionLogon
            and not SessionSwitchReason.SessionUnlock
            and not SessionSwitchReason.ConsoleConnect
            and not SessionSwitchReason.RemoteConnect)
        {
            return;
        }

        ArmCaptureSelfTest(e.Reason.ToString());
    }

    private void ShowCaptureSelfTestFailureToast(CaptureSelfTestDecision decision)
    {
        if (!UserSessionNativeSignals.IsInterventionPresentationAvailable(out _))
        {
            return;
        }

        string message = decision.FailureCode switch
        {
            "heartbeat_not_fresh"
                => "Ari is connected, but the live session heartbeat is not updating yet. Ari will keep trying in the background. If this stays stuck, sign out and back in.",
            "hooks_unavailable"
                => "Ari could not attach live keyboard or mouse capture in this session. Sign out and back in if live activity stays stuck at zero.",
            "input_publish_not_confirmed"
                => "Ari saw live input locally, but the core did not confirm it yet. If activity stays stuck at zero, sign out and back in.",
            _
                => "Ari is not receiving live keyboard or mouse activity yet. If activity stays stuck at zero, sign out and back in."
        };

        if (!_toastRenderer.TryShow("PORTA", message, out string errorCode))
        {
            _logger.LogDebug(
                "Failed to present capture self-test warning toast. Error={ErrorCode}",
                errorCode);
        }
    }

    private bool TryShowCommunicationToast(UserCommunicationToast communication)
    {
        if (!UserSessionNativeSignals.IsInterventionPresentationAvailable(out _))
        {
            return false;
        }

        if (_toastRenderer.TryShow(communication.Title, communication.Message, out string errorCode))
        {
            return true;
        }

        _logger.LogDebug(
            "Failed to present Ari communication toast {CommunicationKey}. Error={ErrorCode}",
            communication.Key,
            errorCode);
        return false;
    }

    private void UpdateTrayState(UserInterventionToastRenderer.UserTrayVisualState state)
    {
        if (_lastTrayState.HasValue && _lastTrayState.Value.Equals(state))
        {
            return;
        }

        if (_toastRenderer.TryUpdateTrayState(state, out string errorCode))
        {
            _lastTrayState = state;
            return;
        }

        _logger.LogDebug("Failed to update PORTA tray state. Error={ErrorCode}", errorCode);
    }

    private void UpdateWidgetState(UserDesktopWidgetState state)
    {
        UserDesktopWidgetState observedState = _toastRenderer.SupportsWidgetSurface
            ? state
            : state with { CriticalState = null };
        _interventionExecutor.ObserveWidgetState(observedState);

        if (!_toastRenderer.SupportsWidgetSurface)
        {
            _lastWidgetState = null;
            if (_widgetFallbackLogged)
            {
                return;
            }

            _widgetFallbackLogged = true;
            _logger.LogInformation(
                "Desktop widget disabled for rollout. Keeping tray + urgent toast fallback active. Reason={ReasonCode}",
                _toastRenderer.WidgetUnavailableReasonCode);
            UserAgentFileLog.Info($"desktop_widget_rollout_active reason={_toastRenderer.WidgetUnavailableReasonCode}");
            return;
        }

        if (_lastWidgetState != null && _lastWidgetState.Equals(state))
        {
            return;
        }

        if (_toastRenderer.TryUpdateWidgetState(state, out string errorCode))
        {
            _lastWidgetState = state;
            return;
        }

        _logger.LogDebug("Failed to update desktop widget state. Error={ErrorCode}", errorCode);
    }

    private static bool IsInteractiveUserSession(int sessionId)
    {
        return Environment.UserInteractive && sessionId > 0;
    }

    private static DateTimeOffset ResolveWidgetTimelineStartUtc(DateTimeOffset nowUtc)
    {
        DateTimeOffset localNow = nowUtc.ToLocalTime();
        DateTimeOffset startOfDayLocal = new(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, localNow.Offset);
        return startOfDayLocal.ToUniversalTime();
    }

    private async Task<WidgetDetailSnapshot> RefreshWidgetDetailsAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        bool shouldRefresh = _widgetDetailTimeline == null
            || _widgetDetailSettings == null
            || (nowUtc - _lastWidgetDetailRefreshUtc) >= WidgetDetailRefreshInterval;
        if (shouldRefresh && _localApiClient.IsConfigured)
        {
            DateTimeOffset widgetTimelineStartUtc = ResolveWidgetTimelineStartUtc(nowUtc);
            IReadOnlyList<LocalApiEventResponse> events = await _localApiClient
                .GetRecentEventsAsync(limit: 40, token: token)
                .ConfigureAwait(false);
            LocalApiTimelineResponse? timeline = await _localApiClient
                .GetTimelineAsync(widgetTimelineStartUtc, nowUtc, bucketSeconds: 60, token)
                .ConfigureAwait(false);
            LocalApiSettingsResponse? settings = await _localApiClient
                .GetEffectiveSettingsAsync(token)
                .ConfigureAwait(false);

            _widgetDetailEvents = events;
            _widgetDetailTimeline = timeline ?? _widgetDetailTimeline;
            _widgetDetailSettings = settings ?? _widgetDetailSettings;
            _lastWidgetDetailRefreshUtc = nowUtc;
        }

        return new WidgetDetailSnapshot(
            _widgetDetailEvents,
            _widgetDetailTimeline,
            _widgetDetailSettings);
    }

    private static string BuildAgentInstanceId()
    {
        return $"useragent-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    private static string BuildSessionId(int sessionId)
    {
        return $"session-{Math.Max(0, sessionId)}";
    }

    private static bool ShouldLogHeartbeatWriteFailure(int failureCount)
    {
        return failureCount <= 3 || (failureCount % 30) == 0;
    }

    private string ResolveSessionContextPath()
    {
        return _sessionContextStore is UserAgentSessionContextStore fileStore
            ? fileStore.ContextPath
            : "unknown";
    }

    private static UserInterventionToastRenderer.UserTrayMode MapTrayMode(UserAgentVisualMode mode)
    {
        return mode switch
        {
            UserAgentVisualMode.DeepWork => UserInterventionToastRenderer.UserTrayMode.DeepWork,
            UserAgentVisualMode.Overload => UserInterventionToastRenderer.UserTrayMode.Overload,
            UserAgentVisualMode.Unavailable => UserInterventionToastRenderer.UserTrayMode.Unavailable,
            _ => UserInterventionToastRenderer.UserTrayMode.Normal
        };
    }

    private sealed record WidgetDetailSnapshot(
        IReadOnlyList<LocalApiEventResponse> RecentEvents,
        LocalApiTimelineResponse? Timeline,
        LocalApiSettingsResponse? Settings)
    {
        public static WidgetDetailSnapshot Empty { get; } = new([], null, null);
    }
}

internal sealed class CaptureSelfTestCoordinator
{
    private static readonly TimeSpan HeartbeatGrace = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HooksReadyGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RawInputGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PublishConfirmationGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FlushTriggerDelay = TimeSpan.FromSeconds(1);
    private const int MaxRemediationAttempts = 2;
    private const double RecentUserInputIdleThresholdSeconds = 2.5d;
    private readonly object _gate = new();
    private DateTimeOffset? _armedAtUtc;
    private DateTimeOffset? _observedUserInputUtc;
    private DateTimeOffset? _lastHeartbeatPublishedUtc;
    private DateTimeOffset? _lastRawInputUtc;
    private DateTimeOffset? _lastPublishAcceptedUtc;
    private double? _lastObservedIdleSeconds;
    private string _state = CaptureSelfTestStates.Healthy;
    private string? _errorCode;
    private string _triggerReason = "startup";
    private int _remediationAttempts;
    private bool _failureNotified;
    private bool _flushRequested;

    public void Arm(string triggerReason, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _armedAtUtc = nowUtc;
            _observedUserInputUtc = null;
            _lastObservedIdleSeconds = null;
            _flushRequested = false;
            _failureNotified = false;
            _state = CaptureSelfTestStates.Pending;
            _errorCode = null;
            _triggerReason = string.IsNullOrWhiteSpace(triggerReason) ? "startup" : triggerReason.Trim();
            _remediationAttempts = 0;
        }
    }

    public void RecordHeartbeatPublished(DateTimeOffset publishedAtUtc)
    {
        lock (_gate)
        {
            _lastHeartbeatPublishedUtc = publishedAtUtc;
        }
    }

    public void RecordRawInput(DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            _lastRawInputUtc = observedAtUtc;
        }
    }

    public void RecordPublishAccepted(DateTimeOffset acceptedAtUtc)
    {
        lock (_gate)
        {
            _lastPublishAcceptedUtc = acceptedAtUtc;
        }
    }

    public CaptureSelfTestHeartbeatProjection GetHeartbeatProjection()
    {
        lock (_gate)
        {
            string? state = _state == CaptureSelfTestStates.Healthy ? null : _state;
            string? errorCode = _state == CaptureSelfTestStates.Failed ? _errorCode : null;
            int? remediationCount = _remediationAttempts > 0 ? _remediationAttempts : null;
            return new CaptureSelfTestHeartbeatProjection(state, errorCode, remediationCount);
        }
    }

    public CaptureSelfTestDecision Evaluate(
        DateTimeOffset nowUtc,
        bool heartbeatFresh,
        bool hooksAvailable,
        double idleSeconds)
    {
        lock (_gate)
        {
            if (!_armedAtUtc.HasValue)
            {
                return CaptureSelfTestDecision.None;
            }

            ObserveUserInput(nowUtc, idleSeconds);

            if (HasPublishConfirmationSinceObservedInput())
            {
                return SetHealthy("capture_self_test_passed");
            }

            if (!heartbeatFresh || !IsHeartbeatFresh(nowUtc))
            {
                if ((nowUtc - _armedAtUtc.Value) >= HeartbeatGrace)
                {
                    return SetFailed("heartbeat_not_fresh", "capture_self_test_failed reason=heartbeat_not_fresh");
                }

                return SetPending();
            }

            if (!hooksAvailable)
            {
                if ((nowUtc - _armedAtUtc.Value) >= HooksReadyGrace)
                {
                    return HandleRecoverableFailure(nowUtc, "hooks_unavailable");
                }

                return SetPending();
            }

            if (!_observedUserInputUtc.HasValue)
            {
                return SetPending();
            }

            if (!HasRawInputSinceObservedUserInput())
            {
                if ((nowUtc - _observedUserInputUtc.Value) >= RawInputGrace)
                {
                    return HandleRecoverableFailure(nowUtc, "no_live_input_after_user_activity");
                }

                return SetPending();
            }

            if (!_flushRequested
                && _lastRawInputUtc.HasValue
                && (nowUtc - _lastRawInputUtc.Value) >= FlushTriggerDelay)
            {
                _flushRequested = true;
                return new CaptureSelfTestDecision(
                    State: _state,
                    FailureCode: _errorCode,
                    RequestHookReset: false,
                    RequestImmediateFlush: true,
                    NotifyUserFailure: false,
                    LogMessage: "capture_self_test_force_flush_requested");
            }

            if ((nowUtc - _observedUserInputUtc.Value) >= PublishConfirmationGrace)
            {
                return HandleRecoverableFailure(nowUtc, "input_publish_not_confirmed");
            }

            return SetPending();
        }
    }

    private void ObserveUserInput(DateTimeOffset nowUtc, double idleSeconds)
    {
        bool recentInput = idleSeconds <= RecentUserInputIdleThresholdSeconds;
        bool idleReset = _lastObservedIdleSeconds.HasValue
            && idleSeconds + 0.5d < _lastObservedIdleSeconds.Value;
        _lastObservedIdleSeconds = idleSeconds;

        if ((recentInput || idleReset) && !_observedUserInputUtc.HasValue)
        {
            _observedUserInputUtc = nowUtc;
        }
    }

    private bool HasRawInputSinceObservedUserInput()
    {
        return _observedUserInputUtc.HasValue
            && _lastRawInputUtc.HasValue
            && _lastRawInputUtc.Value >= _observedUserInputUtc.Value.AddSeconds(-1);
    }

    private bool HasPublishConfirmationSinceObservedInput()
    {
        return _observedUserInputUtc.HasValue
            && _lastPublishAcceptedUtc.HasValue
            && _lastPublishAcceptedUtc.Value >= _observedUserInputUtc.Value.AddSeconds(-1);
    }

    private bool IsHeartbeatFresh(DateTimeOffset nowUtc)
    {
        return _lastHeartbeatPublishedUtc.HasValue
            && (nowUtc - _lastHeartbeatPublishedUtc.Value) <= HeartbeatGrace;
    }

    private CaptureSelfTestDecision HandleRecoverableFailure(DateTimeOffset nowUtc, string errorCode)
    {
        if (_remediationAttempts < MaxRemediationAttempts)
        {
            _remediationAttempts++;
            _state = CaptureSelfTestStates.Recovering;
            _errorCode = errorCode;
            _armedAtUtc = nowUtc;
            _observedUserInputUtc = null;
            _lastObservedIdleSeconds = null;
            _flushRequested = false;
            _failureNotified = false;
            return new CaptureSelfTestDecision(
                State: _state,
                FailureCode: _errorCode,
                RequestHookReset: true,
                RequestImmediateFlush: false,
                NotifyUserFailure: false,
                LogMessage: $"capture_self_test_recovery_requested trigger={_triggerReason} error={errorCode} attempt={_remediationAttempts}");
        }

        return SetFailed(errorCode, $"capture_self_test_failed trigger={_triggerReason} error={errorCode} attempts={_remediationAttempts}");
    }

    private CaptureSelfTestDecision SetHealthy(string logMessage)
    {
        bool changed = _state != CaptureSelfTestStates.Healthy || _errorCode is not null || _armedAtUtc.HasValue;
        _state = CaptureSelfTestStates.Healthy;
        _errorCode = null;
        _armedAtUtc = null;
        _observedUserInputUtc = null;
        _lastObservedIdleSeconds = null;
        _flushRequested = false;
        _remediationAttempts = 0;
        _failureNotified = false;
        return changed
            ? new CaptureSelfTestDecision(
                State: _state,
                FailureCode: null,
                RequestHookReset: false,
                RequestImmediateFlush: false,
                NotifyUserFailure: false,
                LogMessage: logMessage)
            : CaptureSelfTestDecision.None;
    }

    private CaptureSelfTestDecision SetPending()
    {
        _state = CaptureSelfTestStates.Pending;
        _errorCode = null;
        return CaptureSelfTestDecision.None;
    }

    private CaptureSelfTestDecision SetFailed(string errorCode, string logMessage)
    {
        bool notify = !_failureNotified;
        _state = CaptureSelfTestStates.Failed;
        _errorCode = errorCode;
        _failureNotified = true;
        return new CaptureSelfTestDecision(
            State: _state,
            FailureCode: _errorCode,
            RequestHookReset: false,
            RequestImmediateFlush: false,
            NotifyUserFailure: notify,
            LogMessage: logMessage);
    }
}

internal static class CaptureSelfTestStates
{
    public const string Pending = "pending";
    public const string Healthy = "healthy";
    public const string Recovering = "recovering";
    public const string Failed = "failed";
}

internal sealed record CaptureSelfTestHeartbeatProjection(
    string? State,
    string? ErrorCode,
    int? RemediationCount);

internal sealed record CaptureSelfTestDecision(
    string State,
    string? FailureCode,
    bool RequestHookReset,
    bool RequestImmediateFlush,
    bool NotifyUserFailure,
    string? LogMessage)
{
    public static CaptureSelfTestDecision None { get; } = new(
        State: CaptureSelfTestStates.Healthy,
        FailureCode: null,
        RequestHookReset: false,
        RequestImmediateFlush: false,
        NotifyUserFailure: false,
        LogMessage: null);
}
