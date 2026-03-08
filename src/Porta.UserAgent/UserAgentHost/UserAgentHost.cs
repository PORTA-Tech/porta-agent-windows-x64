namespace Porta.UserAgent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;
using Porta.Agent.Runtime.Input;
using Porta.UserAgent.Interventions;
using Porta.UserAgent.Input;
using System.Diagnostics;
using System.Text.Json;

public sealed class UserAgentHost : BackgroundService, IDisposable
{
    private readonly ILogger<UserAgentHost> _logger;
    private readonly IUserAgentSessionContextStore _sessionContextStore;
    private readonly LocalApiClient _localApiClient = new();
    private readonly UserInterventionToastRenderer _toastRenderer = new();
    private readonly Dictionary<string, LocalApiInterventionAckRequest> _interventionAckCache = new(StringComparer.Ordinal);
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
                await PollPendingInterventionsAsync(stoppingToken).ConfigureAwait(false);
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
        _toastRenderer.Dispose();
        _localApiClient.Dispose();
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
        if (_interventionAckCache.TryGetValue(intervention.InterventionId, out LocalApiInterventionAckRequest? cachedAck))
        {
            await SendInterventionAckAsync(cachedAck, token).ConfigureAwait(false);
            return;
        }

        LocalApiInterventionAckRequest ack;
        switch ((intervention.CommandType ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "show_break_toast":
            case "show_focus_lock_in_toast":
            case "show_stretch_break_toast":
                ack = BuildToastAck(intervention);
                break;

            default:
                ack = BuildAck(intervention, result: "failed", errorCode: "unsupported_command");
                break;
        }

        RememberInterventionAck(intervention.InterventionId, ack);
        await SendInterventionAckAsync(ack, token).ConfigureAwait(false);
    }

    private LocalApiInterventionAckRequest BuildToastAck(LocalApiInterventionPendingResponse intervention)
    {
        if (!TryParseToastPayload(intervention.PayloadJson, out string title, out string message, out string errorCode))
        {
            return BuildAck(intervention, result: "failed", errorCode);
        }

        if (!UserSessionNativeSignals.IsInterventionPresentationAvailable(out string sessionError))
        {
            _logger.LogInformation(
                "Skipping intervention {InterventionId} because the user session cannot present UI right now. Reason={Reason}",
                intervention.InterventionId,
                sessionError);
            return BuildAck(intervention, result: "failed", sessionError);
        }

        bool shown = _toastRenderer.TryShow(title, message, out string renderError);
        return BuildAck(intervention, shown ? "applied" : "failed", shown ? null : renderError);
    }

    private LocalApiInterventionAckRequest BuildAck(
        LocalApiInterventionPendingResponse intervention,
        string result,
        string? errorCode)
    {
        return new LocalApiInterventionAckRequest(
            SchemaVer: 1,
            InterventionId: intervention.InterventionId,
            SessionId: BuildSessionId(_sessionId),
            AgentInstanceId: _agentInstanceId,
            Result: result,
            ErrorCode: errorCode,
            AppliedAtUtc: DateTimeOffset.UtcNow);
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

    private void RememberInterventionAck(string interventionId, LocalApiInterventionAckRequest ack)
    {
        if (string.IsNullOrWhiteSpace(interventionId))
        {
            return;
        }

        _interventionAckCache[interventionId] = ack;
        while (_interventionAckCache.Count > 256)
        {
            string oldestKey = _interventionAckCache.Keys.First();
            _interventionAckCache.Remove(oldestKey);
        }
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

    private static bool TryParseToastPayload(
        string payloadJson,
        out string title,
        out string message,
        out string errorCode)
    {
        title = "PORTA";
        message = string.Empty;
        errorCode = string.Empty;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            errorCode = "missing_payload";
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_payload";
                return false;
            }

            if (root.TryGetProperty("title", out JsonElement titleNode) && titleNode.ValueKind == JsonValueKind.String)
            {
                title = titleNode.GetString() ?? "PORTA";
            }

            if (root.TryGetProperty("message", out JsonElement messageNode) && messageNode.ValueKind == JsonValueKind.String)
            {
                message = messageNode.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                errorCode = "missing_toast_message";
                return false;
            }

            return true;
        }
        catch
        {
            errorCode = "payload_parse_failed";
            return false;
        }
    }
}
