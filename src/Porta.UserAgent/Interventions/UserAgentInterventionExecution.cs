namespace Porta.UserAgent.Interventions;

using Porta.Agent.Common;
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
    private readonly UserInterventionAntiSpamGate _antiSpamGate;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalApiInterventionAckRequest> _terminalAckCache = new(StringComparer.Ordinal);
    private readonly Queue<LocalApiInterventionAckRequest> _completedPromptAcks = new();
    private readonly Dictionary<string, PromptSessionState> _promptSessions = new(StringComparer.Ordinal);
    private ActivePromptState? _activePrompt;

    public UserAgentInterventionExecutor(
        IUserInterventionPresenter presenter,
        IUserSessionPresentationGate presentationGate,
        INotificationController notificationController,
        IUserAgentNotificationSnapshotStore notificationSnapshotStore,
        IUserInterventionPreferenceStore preferenceStore,
        UserInterventionAntiSpamGate? antiSpamGate = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _presenter = presenter;
        _presentationGate = presentationGate;
        _notificationController = notificationController;
        _notificationSnapshotStore = notificationSnapshotStore;
        _preferenceStore = preferenceStore;
        _antiSpamGate = antiSpamGate ?? new UserInterventionAntiSpamGate();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public void ObserveWidgetState(UserDesktopWidgetState state)
    {
        _antiSpamGate.ObserveWidgetState(state);
    }

    public LocalApiInterventionAckRequest? ExecuteOrReplay(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        lock (_gate)
        {
            if (_terminalAckCache.TryGetValue(intervention.InterventionId, out LocalApiInterventionAckRequest? cachedAck))
            {
                return cachedAck;
            }

            if (_promptSessions.ContainsKey(intervention.InterventionId))
            {
                return null;
            }

            if (_activePrompt != null
                && IsPromptCommand(intervention.CommandType)
                && string.Equals(_activePrompt.Intervention.InterventionId, intervention.InterventionId, StringComparison.Ordinal))
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
            RememberTerminalInterventionAck(intervention.InterventionId, ack);
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
        DateTimeOffset nowUtc = _utcNowProvider();
        if (!TryParsePromptPayload(intervention, out UserInterventionPrompt prompt, out string errorCode))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode, prompt: null);
        }

        bool usesBreakOverlay = RequiresBreakOverlayLifecycle(prompt);
        bool recoveringAcceptedBreak = usesBreakOverlay
            && string.Equals(intervention.ExecutionState, "accepted", StringComparison.Ordinal)
            || usesBreakOverlay
            && string.Equals(intervention.ExecutionState, "overlay_recovered", StringComparison.Ordinal);

        if (recoveringAcceptedBreak)
        {
            return TryResumeBreakOverlay(intervention, sessionId, agentInstanceId, prompt);
        }

        if (_preferenceStore.IsTipSuppressed(prompt.TipKey))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "suppressed_by_preference", errorCode: null, prompt);
        }

        if (_preferenceStore.IsTipSnoozed(prompt.TipKey, nowUtc))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "snoozed", errorCode: "tip_snoozed_active", prompt);
        }

        if (_antiSpamGate.IsCriticalStateVisible())
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "snoozed", errorCode: "anti_spam_critical_state", prompt);
        }

        ActivePromptState? activePrompt = null;
        lock (_gate)
        {
            activePrompt = _activePrompt;
        }

        if (activePrompt != null)
        {
            int activePriority = ResolvePromptPriority(activePrompt.Intervention.Action);
            int incomingPriority = ResolvePromptPriority(intervention.Action);
            if (incomingPriority <= activePriority)
            {
                return BuildAck(intervention, sessionId, agentInstanceId, result: "snoozed", errorCode: "anti_spam_active_prompt", prompt);
            }
        }

        UserInterventionGateDecision antiSpamDecision = _antiSpamGate.EvaluatePrompt(intervention.Action, nowUtc);
        if (!antiSpamDecision.Allowed)
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "snoozed", errorCode: antiSpamDecision.ReasonCode, prompt);
        }

        if (!_presentationGate.IsPresentationAvailable(out string sessionError))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", sessionError, prompt);
        }

        ActivePromptState promptState = new(intervention, sessionId, agentInstanceId, prompt);
        lock (_gate)
        {
            if (_activePrompt != null)
            {
                return null;
            }

            _activePrompt = promptState;
            _promptSessions[intervention.InterventionId] = new PromptSessionState(intervention, sessionId, agentInstanceId, prompt, usesBreakOverlay);
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

                _promptSessions.Remove(intervention.InterventionId);
            }

            string normalizedRenderError = NormalizeErrorCode(renderError, "prompt_render_failed");
            string result = ShouldSnoozePromptWhenUnavailable(normalizedRenderError)
                ? "snoozed"
                : "failed";
            return BuildAck(intervention, sessionId, agentInstanceId, result: result, errorCode: normalizedRenderError, prompt);
        }

        _antiSpamGate.MarkPromptShown(intervention.Action, nowUtc);
        return usesBreakOverlay
            ? BuildAck(intervention, sessionId, agentInstanceId, result: "overlay_started", errorCode: null, prompt)
            : null;
    }

    private LocalApiInterventionAckRequest BuildToastAck(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId)
    {
        DateTimeOffset nowUtc = _utcNowProvider();
        if (!TryParseToastPayload(intervention.PayloadJson, out string title, out string message, out string errorCode))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", errorCode);
        }

        UserInterventionGateDecision antiSpamDecision = _antiSpamGate.EvaluateToast(intervention.Action, nowUtc);
        if (!antiSpamDecision.Allowed)
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "applied", errorCode: null);
        }

        if (!_presentationGate.IsPresentationAvailable(out string sessionError))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", sessionError);
        }

        bool shown = _presenter.TryShow(title, message, out string renderError);
        if (shown)
        {
            _antiSpamGate.MarkToastShown(intervention.Action, nowUtc);
        }

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
        string? errorCode,
        UserInterventionPrompt? prompt = null)
    {
        return new LocalApiInterventionAckRequest(
            SchemaVer: 1,
            InterventionId: intervention.InterventionId,
            SessionId: sessionId,
            AgentInstanceId: agentInstanceId,
            Result: result,
            ErrorCode: errorCode,
            AppliedAtUtc: DateTimeOffset.UtcNow,
            InteractionKind: prompt?.InteractionKind ?? intervention.InteractionKind,
            DeliverySurface: prompt?.DeliverySurface ?? intervention.DeliverySurface,
            BreakKind: prompt?.BreakKind ?? intervention.BreakKind);
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

        DateTimeOffset nowUtc = _utcNowProvider();
        if (!_antiSpamGate.EvaluateToast(intervention.Action, nowUtc).Allowed)
        {
            return;
        }

        if (!_presentationGate.IsPresentationAvailable(out _))
        {
            return;
        }

        if (_presenter.TryShow(title, message, out _))
        {
            _antiSpamGate.MarkToastShown(intervention.Action, nowUtc);
        }
    }

    private void RememberTerminalInterventionAck(string interventionId, LocalApiInterventionAckRequest ack)
    {
        if (!IsTerminalAckResult(ack.Result))
        {
            return;
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(interventionId))
            {
                return;
            }

            _terminalAckCache[interventionId] = ack;
            while (_terminalAckCache.Count > 256)
            {
                string oldestKey = _terminalAckCache.Keys.First();
                _terminalAckCache.Remove(oldestKey);
            }
        }
    }

    private void OnPromptResponse(UserInterventionPromptResponse response)
    {
        PromptSessionState? promptSession;
        lock (_gate)
        {
            if (!_promptSessions.TryGetValue(response.InterventionId, out promptSession))
            {
                return;
            }

            if (_activePrompt != null
                && string.Equals(_activePrompt.Intervention.InterventionId, response.InterventionId, StringComparison.Ordinal)
                && response.Kind is not UserInterventionPromptResponseKind.OverlayRecovered
                and not UserInterventionPromptResponseKind.OverlayCompleted
                and not UserInterventionPromptResponseKind.OverlayAborted)
            {
                _activePrompt = null;
            }
        }

        string result = MapPromptResult(response.Kind);
        LocalApiInterventionAckRequest ack;
        DateTimeOffset nowUtc = _utcNowProvider();

        if (response.Kind == UserInterventionPromptResponseKind.SuppressedByPreference)
        {
            bool persisted = _preferenceStore.TrySuppressTip(response.TipKey, nowUtc);
            ack = BuildAck(
                promptSession!.Intervention,
                promptSession.SessionId,
                promptSession.AgentInstanceId,
                persisted ? result : "failed",
                persisted ? null : "tip_preference_persist_failed",
                promptSession.Prompt);
        }
        else if (response.Kind == UserInterventionPromptResponseKind.Snoozed)
        {
            DateTimeOffset snoozeUntilUtc = ResolvePromptSnoozeUntilUtc(promptSession!.Intervention.Action, nowUtc);
            bool persisted = _preferenceStore.TrySnoozeTip(response.TipKey, snoozeUntilUtc, nowUtc);
            ack = BuildAck(
                promptSession.Intervention,
                promptSession.SessionId,
                promptSession.AgentInstanceId,
                persisted ? result : "failed",
                persisted ? null : "tip_snooze_persist_failed",
                promptSession.Prompt);
        }
        else
        {
            ack = BuildAck(
                promptSession!.Intervention,
                promptSession.SessionId,
                promptSession.AgentInstanceId,
                result,
                errorCode: response.ErrorCode,
                promptSession.Prompt);
        }

        lock (_gate)
        {
            if (IsTerminalPromptResponse(response.Kind, promptSession!))
            {
                _promptSessions.Remove(promptSession.Intervention.InterventionId);
                RememberTerminalInterventionAck(promptSession.Intervention.InterventionId, ack);
            }

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
        (string defaultAccept, string defaultDismiss, string defaultSnooze) = ResolveDefaultPromptLabels(intervention.Action);
        prompt = new UserInterventionPrompt(
            InterventionId: intervention.InterventionId,
            TipKey: string.Empty,
            Title: string.Empty,
            Message: string.Empty,
            AcceptLabel: defaultAccept,
            DismissLabel: defaultDismiss,
            SnoozeLabel: defaultSnooze,
            SuppressLabel: "Turn off this tip",
            DurationSeconds: 120,
            InteractionKind: intervention.InteractionKind ?? ResolveDefaultInteractionKind(intervention.Action),
            DeliverySurface: intervention.DeliverySurface ?? ResolveDefaultDeliverySurface(intervention.Action),
            BreakKind: intervention.BreakKind ?? PortaBreakSemantics.ResolveBreakKind(intervention.Action),
            CountdownSeconds: intervention.CountdownSeconds);
        errorCode = string.Empty;

        if (!TryParseToastPayload(intervention.PayloadJson, out string title, out string message, out errorCode))
        {
            return false;
        }

        string tipKey = ResolveTipKey(intervention.Action);
        string acceptLabel = defaultAccept;
        string dismissLabel = defaultDismiss;
        string snoozeLabel = defaultSnooze;
        string suppressLabel = "Turn off this tip";
        int durationSeconds = 120;
        string interactionKind = string.IsNullOrWhiteSpace(intervention.InteractionKind)
            ? ResolveDefaultInteractionKind(intervention.Action)
            : intervention.InteractionKind.Trim();
        string deliverySurface = string.IsNullOrWhiteSpace(intervention.DeliverySurface)
            ? ResolveDefaultDeliverySurface(intervention.Action)
            : intervention.DeliverySurface.Trim();
        string? breakKind = string.IsNullOrWhiteSpace(intervention.BreakKind)
            ? PortaBreakSemantics.ResolveBreakKind(intervention.Action)
            : intervention.BreakKind.Trim();
        int? countdownSeconds = intervention.CountdownSeconds;

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
                    acceptLabel = NormalizeLabel(acceptLabelNode.GetString(), defaultAccept);
                }

                if (root.TryGetProperty("dismiss_label", out JsonElement dismissLabelNode) && dismissLabelNode.ValueKind == JsonValueKind.String)
                {
                    dismissLabel = NormalizeLabel(dismissLabelNode.GetString(), defaultDismiss);
                }

                if (root.TryGetProperty("snooze_label", out JsonElement snoozeLabelNode) && snoozeLabelNode.ValueKind == JsonValueKind.String)
                {
                    snoozeLabel = NormalizeLabel(snoozeLabelNode.GetString(), defaultSnooze);
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

                if (root.TryGetProperty("interaction_kind", out JsonElement interactionNode) && interactionNode.ValueKind == JsonValueKind.String)
                {
                    interactionKind = NormalizeLabel(interactionNode.GetString(), interactionKind);
                }

                if (root.TryGetProperty("delivery_surface", out JsonElement surfaceNode) && surfaceNode.ValueKind == JsonValueKind.String)
                {
                    deliverySurface = NormalizeLabel(surfaceNode.GetString(), deliverySurface);
                }

                if (root.TryGetProperty("break_kind", out JsonElement breakKindNode) && breakKindNode.ValueKind == JsonValueKind.String)
                {
                    breakKind = NormalizeLabel(breakKindNode.GetString(), breakKind ?? string.Empty);
                }

                if (root.TryGetProperty("countdown_seconds", out JsonElement countdownNode)
                    && countdownNode.TryGetInt32(out int parsedCountdownSeconds))
                {
                    countdownSeconds = Math.Max(30, parsedCountdownSeconds);
                }
            }
        }
        catch
        {
            errorCode = "payload_parse_failed";
            return false;
        }

        if (string.IsNullOrWhiteSpace(breakKind))
        {
            breakKind = PortaBreakSemantics.ResolveBreakKind(tipKey);
        }

        if (string.IsNullOrWhiteSpace(deliverySurface))
        {
            deliverySurface = breakKind != null ? "break_overlay" : "widget";
        }

        if (!countdownSeconds.HasValue && breakKind != null)
        {
            countdownSeconds = (int)PortaBreakSemantics.ResolveBreakCountdownDuration(breakKind).TotalSeconds;
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
            DurationSeconds: durationSeconds,
            InteractionKind: interactionKind,
            DeliverySurface: deliverySurface,
            BreakKind: breakKind,
            CountdownSeconds: countdownSeconds);
        return true;
    }

    private static (string Accept, string Dismiss, string Snooze) ResolveDefaultPromptLabels(string? action)
    {
        return PortaBreakSemantics.IsBreakSuggestionAction(action)
            ? ("Take break", "Skip", "Snooze")
            : ("Yes", "No", "Later");
    }

    private static string NormalizeErrorCode(string? errorCode, string fallback)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return fallback;
        }

        return errorCode.Trim().ToLowerInvariant();
    }

    private static bool ShouldSnoozePromptWhenUnavailable(string normalizedErrorCode)
    {
        return normalizedErrorCode is
            "widget_disabled_by_flag"
            or "widget_forced_toast_fallback"
            or "widget_hidden_by_user"
            or "widget_presenter_not_ready"
            or "widget_presenter_failed"
            or "widget_dispatch_failed"
            or "break_overlay_unavailable"
            or "break_overlay_not_ready"
            or "break_overlay_dispatch_failed"
            or "prompt_requires_widget";
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
            UserInterventionPromptResponseKind.OverlayStarted => "overlay_started",
            UserInterventionPromptResponseKind.Accepted => "accepted",
            UserInterventionPromptResponseKind.Skipped => "skipped",
            UserInterventionPromptResponseKind.Dismissed => "dismissed",
            UserInterventionPromptResponseKind.Snoozed => "snoozed",
            UserInterventionPromptResponseKind.SuppressedByPreference => "suppressed_by_preference",
            UserInterventionPromptResponseKind.OverlayRecovered => "overlay_recovered",
            UserInterventionPromptResponseKind.OverlayCompleted => "overlay_completed",
            UserInterventionPromptResponseKind.OverlayAborted => "overlay_aborted",
            _ => "dismissed"
        };
    }

    private static string ResolveTipKey(string? action)
    {
        return NormalizeTipKey(action, fallback: "general_tip");
    }

    private static int ResolvePromptPriority(string? action)
    {
        // Step 2 migration map:
        // break_suggestion and stretch_break_suggestion remain legacy transport IDs
        // even though break presentation now uses the dedicated Ari overlay.
        return (action ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "break_suggestion" => 30,
            "stretch_break_suggestion" => 20,
            "focus_lock_in_suggestion" => 10,
            _ => 0
        };
    }

    private static DateTimeOffset ResolvePromptSnoozeUntilUtc(string? action, DateTimeOffset nowUtc)
    {
        TimeSpan duration = (action ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "break_suggestion" or "stretch_break_suggestion" => TimeSpan.FromMinutes(30),
            "focus_lock_in_suggestion" => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromMinutes(12)
        };
        return nowUtc.Add(duration);
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

    private static string ResolveDefaultInteractionKind(string? action) =>
        PortaBreakSemantics.IsBreakSuggestionAction(action) || string.Equals(action, "focus_lock_in_suggestion", StringComparison.OrdinalIgnoreCase)
            ? "choice_prompt"
            : "status";

    private static string ResolveDefaultDeliverySurface(string? action) =>
        PortaBreakSemantics.IsBreakSuggestionAction(action)
            ? "break_overlay"
            : string.Equals(action, "focus_lock_in_suggestion", StringComparison.OrdinalIgnoreCase)
                ? "widget"
                : "toast";

    private LocalApiInterventionAckRequest? TryResumeBreakOverlay(
        LocalApiInterventionPendingResponse intervention,
        string sessionId,
        string agentInstanceId,
        UserInterventionPrompt prompt)
    {
        if (!_presentationGate.IsPresentationAvailable(out string sessionError))
        {
            return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", sessionError, prompt);
        }

        lock (_gate)
        {
            _promptSessions[intervention.InterventionId] = new PromptSessionState(intervention, sessionId, agentInstanceId, prompt, UsesBreakOverlay: true);
        }

        bool shown = _presenter.TryPresentPrompt(prompt, OnPromptResponse, out string renderError);
        if (shown)
        {
            return null;
        }

        lock (_gate)
        {
            _promptSessions.Remove(intervention.InterventionId);
        }

        string normalizedRenderError = NormalizeErrorCode(renderError, "break_overlay_recovery_failed");
        return BuildAck(intervention, sessionId, agentInstanceId, result: "failed", normalizedRenderError, prompt);
    }

    private static bool RequiresBreakOverlayLifecycle(UserInterventionPrompt prompt) =>
        string.Equals(prompt.DeliverySurface, "break_overlay", StringComparison.OrdinalIgnoreCase)
        || PortaBreakSemantics.IsBreakSuggestionAction(prompt.TipKey);

    private static bool IsTerminalAckResult(string? result)
    {
        string normalized = result?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is
            "applied"
            or "verified"
            or "failed"
            or "dismissed"
            or "skipped"
            or "snoozed"
            or "suppressed_by_preference"
            or "overlay_completed"
            or "overlay_aborted";
    }

    private static bool IsTerminalPromptResponse(UserInterventionPromptResponseKind kind, PromptSessionState promptSession)
    {
        if (promptSession.UsesBreakOverlay)
        {
            return kind is
                UserInterventionPromptResponseKind.Skipped
                or UserInterventionPromptResponseKind.Dismissed
                or UserInterventionPromptResponseKind.Snoozed
                or UserInterventionPromptResponseKind.SuppressedByPreference
                or UserInterventionPromptResponseKind.OverlayCompleted
                or UserInterventionPromptResponseKind.OverlayAborted;
        }

        return kind is
            UserInterventionPromptResponseKind.Accepted
            or UserInterventionPromptResponseKind.Skipped
            or UserInterventionPromptResponseKind.Dismissed
            or UserInterventionPromptResponseKind.Snoozed
            or UserInterventionPromptResponseKind.SuppressedByPreference;
    }

    private sealed record ActivePromptState(
        LocalApiInterventionPendingResponse Intervention,
        string SessionId,
        string AgentInstanceId,
        UserInterventionPrompt Prompt);

    private sealed record PromptSessionState(
        LocalApiInterventionPendingResponse Intervention,
        string SessionId,
        string AgentInstanceId,
        UserInterventionPrompt Prompt,
        bool UsesBreakOverlay);
}
