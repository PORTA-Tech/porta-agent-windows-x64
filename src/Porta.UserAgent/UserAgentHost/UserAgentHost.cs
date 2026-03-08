namespace Porta.UserAgent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<UserAgentHost> _logger;
    private readonly IUserAgentSessionContextStore _sessionContextStore;
    private readonly LocalApiClient _localApiClient = new();
    private readonly UserInterventionToastRenderer _toastRenderer = new();
    private readonly UserSessionWindowsNotificationController _notificationController = new();
    private readonly UserAgentNotificationSnapshotStore _notificationSnapshotStore = new();
    private readonly UserAgentInterventionExecutor _interventionExecutor;
    private readonly UserSessionSignalAggregator _signalAggregator = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly UserKeyboardActivityPoller _keyboardPoller = new();
    private readonly MouseHook _mouseHook = new();
    private readonly string _agentInstanceId = BuildAgentInstanceId();
    private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
    private long _lastRecoveryEventId;
    private UserInterventionToastRenderer.UserTrayVisualState? _lastTrayState;
    private bool _hooksInstalled;
    private bool _localApiUnavailableLogged;
    private int _sessionId;
    private string _captureContext = UserAgentCaptureContexts.Unknown;

    public UserAgentHost(
        ILogger<UserAgentHost> logger,
        IUserAgentSessionContextStore sessionContextStore)
    {
        _logger = logger;
        _sessionContextStore = sessionContextStore;
        _interventionExecutor = new UserAgentInterventionExecutor(
            _toastRenderer,
            new UserSessionPresentationGate(),
            _notificationController,
            _notificationSnapshotStore);
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

        if (_captureContext == UserAgentCaptureContexts.ServiceSession)
        {
            _logger.LogWarning("Porta.UserAgent started outside an interactive desktop session; capture stays in heartbeat-only mode.");
        }
        else
        {
            StartCaptureHooks();
        }

        UpdateTrayState(new(
            UserInterventionToastRenderer.UserTrayMode.Normal,
            "PORTA | Starting"));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Time.bucketSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            PublishSessionContext();

            if (_captureContext == UserAgentCaptureContexts.UserSession)
            {
                await FlushSignalsAsync(stoppingToken).ConfigureAwait(false);
                await PollPendingInterventionsAsync(stoppingToken).ConfigureAwait(false);
                await RefreshLocalUiSignalsAsync(stoppingToken).ConfigureAwait(false);
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
        StopCaptureHooks();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
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

        UserAgentVisualSignals signals = UserAgentVisualSignalsAdapter.Build(
            status,
            snapshot,
            recentEvents,
            DateTimeOffset.UtcNow,
            _lastRecoveryEventId);

        UpdateTrayState(new(
            MapTrayMode(signals.Mode),
            signals.TooltipText));

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

        UserSessionSignalSnapshot snapshot = _signalAggregator.SnapshotAndReset();
        string sessionId = BuildSessionId(_sessionId);
        string? focusAppHash = UserSessionNativeSignals.TryGetForegroundAppHash();
        double idleSeconds = UserSessionNativeSignals.ReadIdleDurationSeconds();

        var request = new LocalApiInputSignalsRequest(
            SchemaVer: 1,
            AgentInstanceId: _agentInstanceId,
            SessionId: sessionId,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: snapshot.KeyboardKeydownCount,
            KeyboardBackspaceCount: snapshot.KeyboardBackspaceCount,
            MouseWheelDelta: snapshot.MouseWheelDelta,
            MouseMoveDx: snapshot.MouseMoveDx,
            MouseMoveDy: snapshot.MouseMoveDy,
            FocusAppHash: focusAppHash,
            IdleSeconds: idleSeconds,
            LastInputEventUtc: snapshot.LastInputEventUtc);

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

        string sessionId = BuildSessionId(_sessionId);
        IReadOnlyList<LocalApiInterventionPendingResponse> pending =
            await _localApiClient.GetPendingInterventionsAsync(sessionId, limit: 12, token).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (LocalApiInterventionPendingResponse intervention in pending)
        {
            await ExecuteInterventionAsync(intervention, token).ConfigureAwait(false);
        }
    }

    private async Task ExecuteInterventionAsync(LocalApiInterventionPendingResponse intervention, CancellationToken token)
    {
        LocalApiInterventionAckRequest ack = _interventionExecutor.ExecuteOrReplay(
            intervention,
            BuildSessionId(_sessionId),
            _agentInstanceId);
        await SendInterventionAckAsync(ack, token).ConfigureAwait(false);
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
            _sessionContextStore.WriteCurrent(new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: _captureContext,
                ProcessId: Environment.ProcessId,
                SessionId: _sessionId,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                AgentInstanceId: _agentInstanceId));
        }
        catch (Exception ex)
        {
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
        }
    }

    private void StopCaptureHooks()
    {
        if (!_hooksInstalled)
        {
            return;
        }

        _keyboardHook.Uninstall();
        _keyboardPoller.Uninstall();
        _mouseHook.Uninstall();
        _hooksInstalled = false;
    }

    private void OnKeyboardChanged(bool isKeyDown, bool isBackspace, bool isInjected)
    {
        if (!isKeyDown || isInjected)
        {
            return;
        }

        _signalAggregator.RecordKeyboard(isBackspace, DateTimeOffset.UtcNow);
    }

    private void OnMouseMoved(int dx, int dy)
    {
        _signalAggregator.RecordMouseMove(dx, dy, DateTimeOffset.UtcNow);
    }

    private void OnMouseWheelChanged(int delta, bool _)
    {
        _signalAggregator.RecordMouseWheel(delta, DateTimeOffset.UtcNow);
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

    private static bool IsInteractiveUserSession(int sessionId)
    {
        return Environment.UserInteractive && sessionId > 0;
    }

    private static string BuildAgentInstanceId()
    {
        return $"useragent-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    private static string BuildSessionId(int sessionId)
    {
        return $"session-{Math.Max(0, sessionId)}";
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
}
