namespace Porta.UserAgent.Interventions;

using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;
using Porta.UserAgent.Input;
using System.Text.Json;

internal interface IUserInterventionPresenter
{
    bool TryShow(string title, string message, out string errorCode);
}

internal interface IUserSessionPresentationGate
{
    bool IsPresentationAvailable(out string errorCode);
}

internal interface IUserAgentNotificationSnapshotStore
{
    void Save(NotificationControllerSnapshot snapshot);
    NotificationControllerSnapshot? Load();
    void Clear();
}

internal sealed class UserSessionPresentationGate : IUserSessionPresentationGate
{
    public bool IsPresentationAvailable(out string errorCode)
    {
        return UserSessionNativeSignals.IsInterventionPresentationAvailable(out errorCode);
    }
}

internal sealed class UserAgentInterventionExecutor
{
    private readonly IUserInterventionPresenter _presenter;
    private readonly IUserSessionPresentationGate _presentationGate;
    private readonly INotificationController _notificationController;
    private readonly IUserAgentNotificationSnapshotStore _notificationSnapshotStore;
    private readonly Dictionary<string, LocalApiInterventionAckRequest> _ackCache = new(StringComparer.Ordinal);

    public UserAgentInterventionExecutor(
        IUserInterventionPresenter presenter,
        IUserSessionPresentationGate presentationGate,
        INotificationController notificationController,
        IUserAgentNotificationSnapshotStore notificationSnapshotStore)
    {
        _presenter = presenter;
        _presentationGate = presentationGate;
        _notificationController = notificationController;
        _notificationSnapshotStore = notificationSnapshotStore;
    }

    public LocalApiInterventionAckRequest ExecuteOrReplay(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        if (_ackCache.TryGetValue(intervention.InterventionId, out LocalApiInterventionAckRequest? cachedAck))
        {
            return cachedAck;
        }

        LocalApiInterventionAckRequest ack = (intervention.CommandType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "show_break_toast" or "show_focus_lock_in_toast" or "show_stretch_break_toast" or "show_status_toast"
                => BuildToastAck(intervention, sessionId, agentInstanceId),
            "suppress_notifications"
                => BuildNotificationSuppressAck(intervention, sessionId, agentInstanceId),
            "restore_notifications"
                => BuildNotificationRestoreAck(intervention, sessionId, agentInstanceId),
            _
                => BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode: "unsupported_command")
        };

        RememberInterventionAck(intervention.InterventionId, ack);
        return ack;
    }

    private LocalApiInterventionAckRequest BuildToastAck(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        if (!TryParseToastPayload(intervention.PayloadJson, out string title, out string message, out string errorCode))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode);
        }

        if (!_presentationGate.IsPresentationAvailable(out string sessionError))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", sessionError);
        }

        bool shown = _presenter.TryShow(title, message, out string renderError);
        return BuildAck(intervention, sessionId, agentInstanceId, shown ? "applied" : "failed", shown ? null : renderError);
    }

    private LocalApiInterventionAckRequest BuildNotificationSuppressAck(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        NotificationControllerSnapshotResult snapshotResult = _notificationController.GetSnapshot();
        if (!snapshotResult.Succeeded)
        {
            return BuildAck(
                intervention,
                sessionId,
                agentInstanceId,
                result: "failed",
                errorCode: NormalizeErrorCode(snapshotResult.ErrorCode, "snapshot_capture_failed"));
        }

        if (!snapshotResult.Snapshot.IsValid)
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode: "snapshot_invalid");
        }

        try
        {
            _notificationSnapshotStore.Save(snapshotResult.Snapshot);
        }
        catch
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode: "snapshot_persist_failed");
        }

        NotificationControllerActionResult applyResult = _notificationController.ApplySuppression(snapshotResult.Snapshot);
        if (!applyResult.Succeeded)
        {
            _notificationSnapshotStore.Clear();
            return BuildAck(
                intervention,
                sessionId,
                agentInstanceId,
                result: "failed",
                errorCode: NormalizeErrorCode(applyResult.ErrorCode, "suppression_apply_failed"));
        }

        LocalApiInterventionAckRequest ack = VerifyNotificationState(
            intervention,
            sessionId,
            agentInstanceId,
            expectedToastsEnabled: false,
            verificationMismatchError: "suppression_verify_failed");
        TryShowOptionalSuccessToast(intervention, ack);
        return ack;
    }

    private LocalApiInterventionAckRequest BuildNotificationRestoreAck(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        NotificationControllerSnapshot? snapshot = _notificationSnapshotStore.Load();
        if (!snapshot.HasValue || !snapshot.Value.IsValid)
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode: "snapshot_missing_or_invalid");
        }

        NotificationControllerActionResult restoreResult = _notificationController.Restore(snapshot.Value);
        if (!restoreResult.Succeeded)
        {
            return BuildAck(
                intervention,
                sessionId,
                agentInstanceId,
                result: "failed",
                errorCode: NormalizeErrorCode(restoreResult.ErrorCode, "suppression_restore_failed"));
        }

        _notificationSnapshotStore.Clear();
        LocalApiInterventionAckRequest ack = VerifyNotificationState(
            intervention,
            sessionId,
            agentInstanceId,
            expectedToastsEnabled: snapshot.Value.ToastBannersEnabled,
            verificationMismatchError: "restore_verify_failed");
        TryShowOptionalSuccessToast(intervention, ack);
        return ack;
    }

    private LocalApiInterventionAckRequest VerifyNotificationState(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId,
        bool expectedToastsEnabled,
        string verificationMismatchError)
    {
        NotificationControllerSnapshotResult verification = _notificationController.GetSnapshot();
        if (!verification.Succeeded || !verification.Snapshot.IsValid)
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "applied", errorCode: null);
        }

        if (verification.Snapshot.ToastBannersEnabled != expectedToastsEnabled)
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode: verificationMismatchError);
        }

        return BuildAck(intervention, sessionId, agentInstanceId, result: "verified", errorCode: null);
    }

    private static LocalApiInterventionAckRequest BuildAck(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId,
        string result,
        string? errorCode)
    {
        return new LocalApiInterventionAckRequest(
            SchemaVer: 1,
            InterventionId: intervention.InterventionId,
            SessionId: sessionId,
            AgentInstanceId: agentInstanceId,
            Result: result,
            ErrorCode: errorCode,
            AppliedAtUtc: DateTimeOffset.UtcNow);
    }

    private void TryShowOptionalSuccessToast(
        LocalApiInterventionPendingResponse intervention,
        LocalApiInterventionAckRequest ack)
    {
        if (!string.Equals(ack.Result, "applied", StringComparison.Ordinal)
            && !string.Equals(ack.Result, "verified", StringComparison.Ordinal))
        {
            return;
        }

        if (!TryParseToastPayload(intervention.PayloadJson, out string title, out string message, out _))
        {
            return;
        }

        if (!_presentationGate.IsPresentationAvailable(out _))
        {
            return;
        }

        _presenter.TryShow(title, message, out _);
    }

    private void RememberInterventionAck(string interventionId, LocalApiInterventionAckRequest ack)
    {
        if (string.IsNullOrWhiteSpace(interventionId))
        {
            return;
        }

        _ackCache[interventionId] = ack;
        while (_ackCache.Count > 256)
        {
            string oldestKey = _ackCache.Keys.First();
            _ackCache.Remove(oldestKey);
        }
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

    private static string NormalizeErrorCode(string? errorCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return fallback;
        }

        return errorCode.Trim().ToLowerInvariant();
    }
}
