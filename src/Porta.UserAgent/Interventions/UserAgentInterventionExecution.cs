namespace Porta.UserAgent.Interventions;

using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;
using Porta.UserAgent.Input;
using System.Text.Json;

internal interface IUserInterventionPresenter
{
    bool TryShow(string title, string message, out string errorCode);
    bool TryPresentPrompt(
        UserInterventionPrompt prompt,
        Action<UserInterventionPromptResponse> onResponse,
        out string errorCode);
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
    private readonly IUserInterventionPreferenceStore _preferenceStore;
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalApiInterventionAckRequest> _ackCache = new(StringComparer.Ordinal);
    private readonly Queue<LocalApiInterventionAckRequest> _completedPromptAcks = new();
    private ActivePromptState? _activePrompt;

    public UserAgentInterventionExecutor(
        IUserInterventionPresenter presenter,
        IUserSessionPresentationGate presentationGate,
        INotificationController notificationController,
        IUserAgentNotificationSnapshotStore notificationSnapshotStore,
        IUserInterventionPreferenceStore preferenceStore)
    {
        _presenter = presenter;
        _presentationGate = presentationGate;
        _notificationController = notificationController;
        _notificationSnapshotStore = notificationSnapshotStore;
        _preferenceStore = preferenceStore;
    }

    public LocalApiInterventionAckRequest? ExecuteOrReplay(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        lock (_gate)
        {
            if (_ackCache.TryGetValue(intervention.InterventionId, out LocalApiInterventionAckRequest? cachedAck))
            {
                return cachedAck;
            }

            if (_activePrompt != null && IsPromptCommand(intervention.CommandType))
            {
                return null;
            }
        }

        LocalApiInterventionAckRequest? ack = (intervention.CommandType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "show_break_toast" or "show_focus_lock_in_toast" or "show_stretch_break_toast"
                => BuildPromptAck(intervention, sessionId, agentInstanceId),
            "show_status_toast"
                => BuildToastAck(intervention, sessionId, agentInstanceId),
            "suppress_notifications"
                => BuildNotificationSuppressAck(intervention, sessionId, agentInstanceId),
            "restore_notifications"
                => BuildNotificationRestoreAck(intervention, sessionId, agentInstanceId),
            _
                => BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode: "unsupported_command")
        };

        if (ack != null)
        {
            RememberInterventionAck(intervention.InterventionId, ack);
        }

        return ack;
    }

    public bool TryDequeueCompletedAck(out LocalApiInterventionAckRequest ack)
    {
        lock (_gate)
        {
            if (_completedPromptAcks.Count > 0)
            {
                ack = _completedPromptAcks.Dequeue();
                return true;
            }
        }

        ack = default!;
        return false;
    }

    private LocalApiInterventionAckRequest? BuildPromptAck(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        if (!TryParsePromptPayload(intervention, out UserInterventionPrompt prompt, out string errorCode))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode);
        }

        if (_preferenceStore.IsTipSuppressed(prompt.TipKey))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "suppressed_by_preference", errorCode: null);
        }

        if (!_presentationGate.IsPresentationAvailable(out string sessionError))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", sessionError);
        }

        ActivePromptState promptState = new(intervention, sessionId, agentInstanceId, prompt);
        lock (_gate)
        {
            if (_activePrompt != null)
            {
                return null;
            }

            _activePrompt = promptState;
        }

        bool shown = _presenter.TryPresentPrompt(prompt, OnPromptResponse, out string renderError);
        if (!shown)
        {
            lock (_gate)
            {
                if (_activePrompt?.Intervention.InterventionId == intervention.InterventionId)
                {
                    _activePrompt = null;
                }
            }

            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", shown ? null : renderError);
        }

        return null;
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
        lock (_gate)
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
    }

    private void OnPromptResponse(UserInterventionPromptResponse response)
    {
        ActivePromptState? activePrompt;
        lock (_gate)
        {
            if (_activePrompt == null
                || !string.Equals(_activePrompt.Intervention.InterventionId, response.InterventionId, StringComparison.Ordinal))
            {
                return;
            }

            activePrompt = _activePrompt;
            _activePrompt = null;
        }

        string result = MapPromptResult(response.Kind);
        LocalApiInterventionAckRequest ack;

        if (response.Kind == UserInterventionPromptResponseKind.SuppressedByPreference)
        {
            bool persisted = _preferenceStore.TrySuppressTip(response.TipKey, DateTimeOffset.UtcNow);
            ack = BuildAck(
                activePrompt.Intervention,
                activePrompt.SessionId,
                activePrompt.AgentInstanceId,
                persisted ? result : "failed",
                persisted ? null : "tip_preference_persist_failed");
        }
        else
        {
            ack = BuildAck(
                activePrompt.Intervention,
                activePrompt.SessionId,
                activePrompt.AgentInstanceId,
                result,
                errorCode: null);
        }

        lock (_gate)
        {
            RememberInterventionAck(activePrompt.Intervention.InterventionId, ack);
            _completedPromptAcks.Enqueue(ack);
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

    private static bool TryParsePromptPayload(
        LocalApiInterventionPendingResponse intervention,
        out UserInterventionPrompt prompt,
        out string errorCode)
    {
        prompt = new UserInterventionPrompt(
            InterventionId: intervention.InterventionId,
            TipKey: string.Empty,
            Title: string.Empty,
            Message: string.Empty,
            AcceptLabel: "Yes",
            DismissLabel: "No",
            SnoozeLabel: "Later",
            SuppressLabel: "Turn off this tip",
            DurationSeconds: 120);
        errorCode = string.Empty;

        if (!TryParseToastPayload(intervention.PayloadJson, out string title, out string message, out errorCode))
        {
            return false;
        }

        string tipKey = ResolveTipKey(intervention.Action);
        string acceptLabel = "Yes";
        string dismissLabel = "No";
        string snoozeLabel = "Later";
        string suppressLabel = "Turn off this tip";
        int durationSeconds = 120;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(intervention.PayloadJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("tip_key", out JsonElement tipKeyNode) && tipKeyNode.ValueKind == JsonValueKind.String)
                {
                    tipKey = NormalizeTipKey(tipKeyNode.GetString(), intervention.Action);
                }

                if (root.TryGetProperty("accept_label", out JsonElement acceptLabelNode) && acceptLabelNode.ValueKind == JsonValueKind.String)
                {
                    acceptLabel = NormalizeLabel(acceptLabelNode.GetString(), "Yes");
                }

                if (root.TryGetProperty("dismiss_label", out JsonElement dismissLabelNode) && dismissLabelNode.ValueKind == JsonValueKind.String)
                {
                    dismissLabel = NormalizeLabel(dismissLabelNode.GetString(), "No");
                }

                if (root.TryGetProperty("snooze_label", out JsonElement snoozeLabelNode) && snoozeLabelNode.ValueKind == JsonValueKind.String)
                {
                    snoozeLabel = NormalizeLabel(snoozeLabelNode.GetString(), "Later");
                }

                if (root.TryGetProperty("suppress_label", out JsonElement suppressLabelNode) && suppressLabelNode.ValueKind == JsonValueKind.String)
                {
                    suppressLabel = NormalizeLabel(suppressLabelNode.GetString(), "Turn off this tip");
                }

                if (root.TryGetProperty("duration_seconds", out JsonElement durationNode)
                    && durationNode.TryGetInt32(out int parsedDuration))
                {
                    durationSeconds = Math.Clamp(parsedDuration, 30, 300);
                }
            }
        }
        catch
        {
            errorCode = "payload_parse_failed";
            return false;
        }

        prompt = new UserInterventionPrompt(
            InterventionId: intervention.InterventionId,
            TipKey: tipKey,
            Title: title,
            Message: message,
            AcceptLabel: acceptLabel,
            DismissLabel: dismissLabel,
            SnoozeLabel: snoozeLabel,
            SuppressLabel: suppressLabel,
            DurationSeconds: durationSeconds);
        return true;
    }

    private static string NormalizeErrorCode(string? errorCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return fallback;
        }

        return errorCode.Trim().ToLowerInvariant();
    }

    private static bool IsPromptCommand(string? commandType)
    {
        string normalized = commandType?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "show_break_toast" or "show_focus_lock_in_toast" or "show_stretch_break_toast";
    }

    private static string MapPromptResult(UserInterventionPromptResponseKind kind)
    {
        return kind switch
        {
            UserInterventionPromptResponseKind.Accepted => "accepted",
            UserInterventionPromptResponseKind.Dismissed => "dismissed",
            UserInterventionPromptResponseKind.Snoozed => "snoozed",
            UserInterventionPromptResponseKind.SuppressedByPreference => "suppressed_by_preference",
            _ => "dismissed"
        };
    }

    private static string ResolveTipKey(string? action)
    {
        return NormalizeTipKey(action, fallback: "general_tip");
    }

    private static string NormalizeTipKey(string? tipKey, string? fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(tipKey)
            ? string.Empty
            : tipKey.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return string.IsNullOrWhiteSpace(fallback)
            ? "general_tip"
            : fallback.Trim().ToLowerInvariant();
    }

    private static string NormalizeLabel(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }

    private sealed record ActivePromptState(
        LocalApiInterventionPendingResponse Intervention,
        string SessionId,
        string AgentInstanceId,
        UserInterventionPrompt Prompt);
}
