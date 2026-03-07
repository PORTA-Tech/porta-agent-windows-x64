namespace Porta.UserAgent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.Agent.Runtime.Input;
using Porta.UserAgent.Input;
using System.Diagnostics;

public sealed class UserAgentHost : BackgroundService, IDisposable
{
    private readonly ILogger<UserAgentHost> _logger;
    private readonly IUserAgentSessionContextStore _sessionContextStore;
    private readonly UserSessionSignalAggregator _signalAggregator = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly MouseHook _mouseHook = new();
    private readonly string _agentInstanceId = BuildAgentInstanceId();
    private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
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
        _keyboardHook.KeyChanged += OnKeyboardChanged;
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

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Time.bucketSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            PublishSessionContext();

            if (_captureContext == UserAgentCaptureContexts.UserSession)
            {
                await FlushSignalsAsync(stoppingToken).ConfigureAwait(false);
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
        _mouseHook.Dispose();
        base.Dispose();
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

        using var client = new LocalApiClient();
        if (!client.IsConfigured)
        {
            _signalAggregator.Requeue(snapshot);
            if (!_localApiUnavailableLogged)
            {
                _localApiUnavailableLogged = true;
                _logger.LogWarning("Porta.UserAgent could not find local API settings yet. Waiting for the core service.");
            }

            return;
        }

        LocalApiAckResponse? ack = await client.SendInputSignalsAsync(request, token).ConfigureAwait(false);
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
        bool mouseInstalled = _mouseHook.Install();
        _hooksInstalled = keyboardInstalled || mouseInstalled;

        if (!keyboardInstalled)
        {
            _logger.LogWarning("Keyboard hook did not install. Keyboard capture will stay inactive.");
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
}
