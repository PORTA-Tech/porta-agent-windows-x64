namespace Porta.UserAgent.Interventions;

using Porta.Agent.LocalApi;
using Porta.UserAgent.Input;

internal sealed record UserCommunicationToast(
    string Key,
    string Title,
    string Message,
    long EventId = 0,
    string? SuppressionKey = null)
{
    public string DeduplicationKey => string.IsNullOrWhiteSpace(SuppressionKey)
        ? Key
        : SuppressionKey;
}

internal static class UserCommunicationRouter
{
    private static readonly TimeSpan RecentEventWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CriticalEventWindow = TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> DirectToastDeliveredActions = new(StringComparer.Ordinal)
    {
        "deep_work_notifications_restored",
        "overload_brightness_restored",
        "brightness_unsupported",
        "brightness_control_failed",
        "notification_snapshot_failed",
        "notification_suppress_failed",
        "notification_restore_failed"
    };
    private static readonly HashSet<string> PersistentCriticalEventActions = new(StringComparer.Ordinal)
    {
        "brightness_unsupported",
        "brightness_control_failed",
        "notification_snapshot_failed",
        "notification_suppress_failed",
        "notification_restore_failed"
    };

    public static UserCommunicationToast? ResolveRecentEventToast(
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc,
        long lastEventId)
    {
        foreach (LocalApiEventResponse recentEvent in recentEvents.OrderByDescending(static evt => evt.TsUtc))
        {
            if (recentEvent.Id <= 0 || recentEvent.Id == lastEventId)
            {
                continue;
            }

            if ((nowUtc - recentEvent.TsUtc) > RecentEventWindow)
            {
                continue;
            }

            string action = NormalizeAction(recentEvent.Action);
            if (DirectToastDeliveredActions.Contains(action))
            {
                // These actions already show a direct toast when the intervention executes.
                // Replaying them from recent events creates duplicate user-facing messages.
                continue;
            }

            if (!TryBuildRecentEventToast(recentEvent, out string title, out string message))
            {
                continue;
            }

            return new UserCommunicationToast(
                Key: $"event:{recentEvent.Id}",
                Title: title,
                Message: message,
                EventId: recentEvent.Id);
        }

        return null;
    }

    public static UserCommunicationToast? ResolveCaptureDegradedState(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (status == null)
        {
            return null;
        }

        if (!status.ServiceRunning)
        {
            return new UserCommunicationToast(
                Key: "capture:service_unavailable",
                Title: "Tracking limited",
                Message: "Ari cannot regulate right now because the core service is unavailable. It will reconnect automatically when the service returns.",
                SuppressionKey: "capture:service");
        }

        if (!status.ConsentActive)
        {
            return new UserCommunicationToast(
                Key: "capture:consent_required",
                Title: "Tracking limited",
                Message: "Ari is waiting for consent before it can regulate or explain live states in this session.",
                SuppressionKey: "capture:consent");
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return new UserCommunicationToast(
                Key: "capture:non_user_session",
                Title: "Tracking limited",
                Message: "Ari is not attached to your live Windows session yet. It will keep trying to reconnect in the background.",
                SuppressionKey: "capture:session_attach");
        }

        string captureFailureReason = (status.CaptureFailureReason ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(captureFailureReason))
        {
            return captureFailureReason switch
            {
                "USER_AGENT_HEARTBEAT_STALE" => new UserCommunicationToast(
                    Key: "capture:heartbeat_stale",
                    Title: "Tracking limited",
                    Message: "Ari is running, but the live session heartbeat stopped updating. It is trying to recover in the background.",
                    SuppressionKey: "capture:heartbeat"),
                "USER_AGENT_SESSION_SWITCHED" or "USER_AGENT_INSTANCE_MISMATCH" => new UserCommunicationToast(
                    Key: "capture:session_handoff",
                    Title: "Tracking limited",
                    Message: "Ari detected a session handoff and is reconnecting live input capture. It should recover automatically.",
                    SuppressionKey: "capture:session_handoff"),
                "USER_AGENT_INPUT_SELF_TEST_FAILED" => new UserCommunicationToast(
                    Key: "capture:self_test_failed",
                    Title: "Tracking limited",
                    Message: "Ari is connected, but live keyboard or mouse input is not reaching the core yet. It is retrying in the background.",
                    SuppressionKey: "capture:input_pipeline"),
                "USER_AGENT_KEYBOARD_SIGNAL_MISSING" => new UserCommunicationToast(
                    Key: "capture:keyboard_missing",
                    Title: "Tracking limited",
                    Message: "Ari is only seeing mouse activity right now. Keyboard capture is limited while it retries.",
                    SuppressionKey: "capture:keyboard_limited"),
                "USER_AGENT_MOUSE_SIGNAL_MISSING" => new UserCommunicationToast(
                    Key: "capture:mouse_missing",
                    Title: "Tracking limited",
                    Message: "Ari is only seeing keyboard activity right now. Mouse capture is limited while it retries.",
                    SuppressionKey: "capture:mouse_limited"),
                _ => new UserCommunicationToast(
                    Key: $"capture:{captureFailureReason.ToLowerInvariant()}",
                    Title: "Tracking limited",
                    Message: "Ari is connected, but live input capture is degraded right now. It will keep trying to recover automatically.",
                    SuppressionKey: "capture:degraded")
            };
        }

        if (!string.IsNullOrWhiteSpace(status.OwnershipWarning))
        {
            return new UserCommunicationToast(
                Key: "capture:ownership_warning",
                Title: "Tracking limited",
                Message: "Ari detected a mismatch between the active session heartbeat and the latest accepted input. It is reconciling the live session now.",
                SuppressionKey: "capture:session_handoff");
        }

        if (snapshot?.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            string missing = string.Join("/", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            string missingKey = string.Join("-", snapshot.MissingMajorChannels.Select(static channel =>
                string.IsNullOrWhiteSpace(channel)
                    ? "unknown"
                    : channel.Trim().ToLowerInvariant()));
            return new UserCommunicationToast(
                Key: $"capture:partial:{missingKey}",
                Title: "Tracking limited",
                Message: $"Ari is receiving partial activity only. Missing {missing}. It will keep trying to restore full capture.",
                SuppressionKey: "capture:partial");
        }

        return null;
    }

    public static UserDesktopWidgetCriticalState? ResolveCriticalWidgetState(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        IReadOnlyList<LocalApiEventResponse>? recentEvents,
        DateTimeOffset nowUtc,
        int? statusHttpCode = null)
    {
        _ = snapshot;
        if (status == null)
        {
            if (statusHttpCode is 401 or 403)
            {
                return BuildCriticalState(
                    title: "System access needs attention",
                    message: "Ari can see the local runtime, but this session needs refreshed local service access. Open System or retry.",
                    primaryAction: UserDesktopWidgetAction.OpenHealth,
                    primaryLabel: "Open System",
                    secondaryAction: UserDesktopWidgetAction.RetryCritical,
                    secondaryLabel: "Retry",
                    tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                    tertiaryLabel: "Settings");
            }

            return null;
        }

        if (!status.LocalApiAuthReady)
        {
            return BuildCriticalState(
                title: "System access needs attention",
                message: "Ari is alive, but this session cannot read the local service yet. Open System for details or retry the runtime check.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings");
        }

        if (!status.ServiceRunning)
        {
            return BuildCriticalState(
                title: "Core service offline",
                message: "The desktop service is offline. Open System to inspect the runtime, or retry the connection.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings");
        }

        if (status.DbMigrationFailed)
        {
            return BuildCriticalState(
                title: "Database needs attention",
                message: "Ari could not finish a required database migration. Open System to inspect the failure before relying on live regulation.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings");
        }

        if (!status.ConsentActive)
        {
            return BuildCriticalState(
                title: "Tracking limited",
                message: "Ari is waiting for consent before it can regulate or explain live states in this session.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.OpenSettings,
                secondaryLabel: "Settings");
        }

        return ResolveCriticalEventState(recentEvents ?? [], nowUtc);
    }

    /// <summary>
    /// Evaluates all 8 Ari behavioral toast triggers in priority order.
    /// Priority: Recovery nudge > Pressure rising > Milestone > Break prompt > Weak hour > First focus > Session wrap.
    /// Respects 8-minute global suppression via sessionState.CanShowToast.
    /// </summary>
    public static UserCommunicationToast? TryBuildAriToast(
        LocalApiCurrentSnapshotResponse snapshot,
        UserAgentSessionState sessionState,
        LocalApiAriContextResponse? context,
        DateTimeOffset nowUtc)
    {
        double focusSec = snapshot.FocusEpisodeSeconds;
        bool inActiveFocus = focusSec >= 60 && !snapshot.DistractionEpisodeActive;
        string? name = string.IsNullOrWhiteSpace(context?.FirstName) ? null : context!.FirstName!.Trim();

        // Track distraction state transitions
        bool distractionJustEnded = sessionState.InDistractionEpisode && !snapshot.DistractionEpisodeActive;
        bool distractionJustStarted = !sessionState.InDistractionEpisode && snapshot.DistractionEpisodeActive;

        if (distractionJustStarted)
        {
            sessionState.MarkDistractionStarted(focusSec, nowUtc);
        }

        // --- Priority 1: Recovery nudge ("you were close") ---
        // Fires when a real focus run (>= 10 min, per Launch 2.2) ends by distraction and the
        // user returns. Capped to a few per day and never twice within an hour (a nag is worse
        // than nothing) on top of the global 8-minute toast gate.
        if (distractionJustEnded)
        {
            sessionState.MarkDistractionEnded();
            int preMin = (int)(sessionState.PreDistractionFocusSeconds / 60);
            if (preMin >= 10 && sessionState.CanShowToast(nowUtc) && sessionState.CanShowRecoveryNudge(nowUtc))
            {
                string msg = preMin >= 30
                    ? $"You were {preMin}min into your best run today. Pick it back up."
                    : name is not null
                        ? $"{name}, {preMin}min of strong focus before that drift. Recapture it."
                        : $"You were {preMin}min into a strong run before that drift. Pick it back up.";
                sessionState.RecordToastShown(nowUtc);
                sessionState.RecordRecoveryNudgeShown(nowUtc);
                return new UserCommunicationToast(Key: "ari:recovery", Title: "Ari", Message: msg);
            }
        }

        // All remaining triggers respect the 8-minute gate
        if (!sessionState.CanShowToast(nowUtc)) return null;

        // --- Priority 2: Pressure rising ---
        if (snapshot.OverloadActive && sessionState.TryFireMilestone(999)) // 999 = pressure sentinel
        {
            string msg = name is not null
                ? $"{name}, slow down. Pressure is spiking — close the extra tabs."
                : "Ari sees the drift starting. Close the extra tabs.";
            sessionState.RecordToastShown(nowUtc);
            return new UserCommunicationToast(Key: "ari:pressure", Title: "Ari", Message: msg);
        }

        // --- Priority 3: Focus milestones (highest milestone wins) ---
        if (inActiveFocus)
        {
            int[] milestones = { 90, 60, 45, 30, 15 };
            foreach (int milestone in milestones)
            {
                if (focusSec < milestone * 60) continue;
                if (!sessionState.TryFireMilestone(milestone)) continue;

                int? avgStreak = context?.AvgStreakMin;
                string msg = milestone switch
                {
                    15 => name is not null
                        ? $"{name}, 15 minutes clean. Ari is watching."
                        : "15 minutes clean. Ari is watching.",
                    30 => "30 minutes. You're in it now.",
                    45 => avgStreak is > 0
                        ? $"45min uninterrupted. Above your average streak of {avgStreak}min."
                        : "45 minutes uninterrupted. Keep going.",
                    60 => name is not null
                        ? $"{name} — 60 minutes. Your best run today. Don't stop."
                        : "60 minutes. Your best run today. Don't stop.",
                    90 => "90min. That's a strong session.",
                    _ => string.Empty
                };
                if (string.IsNullOrEmpty(msg)) continue;

                sessionState.RecordToastShown(nowUtc);
                return new UserCommunicationToast(Key: $"ari:milestone_{milestone}", Title: "Ari", Message: msg);
            }
        }

        // --- Priority 4: Long session / no break warning (3h+ with no break) ---
        if (!sessionState.LongSessionToastSent)
        {
            double sessionHours = focusSec / 3600d;
            if (sessionHours >= 3.0)
            {
                sessionState.MarkLongSessionToastSent();
                string msg = name is not null
                    ? $"{name}, {(int)sessionHours}h with no break detected. Your next focus window will be stronger with one."
                    : "3 hours without a break. That's above your normal range. Consider a short stop.";
                sessionState.RecordToastShown(nowUtc);
                return new UserCommunicationToast(Key: "ari:long_session_no_break", Title: "Ari", Message: msg);
            }
        }

        // --- Priority 5: Weak hour nudge (in active focus during personal weak hour) ---
        int? weakHour = context?.WeakHour;
        if (weakHour.HasValue && inActiveFocus)
        {
            int localHour = nowUtc.ToLocalTime().Hour;
            if (localHour == weakHour.Value)
            {
                int weakHourKey = 500 + weakHour.Value;
                if (sessionState.TryFireMilestone(weakHourKey))
                {
                    string msg = $"{weakHour}:00 is usually your soft spot. You're still focused. Keep it.";
                    sessionState.RecordToastShown(nowUtc);
                    return new UserCommunicationToast(Key: "ari:weak_hour", Title: "Ari", Message: msg);
                }
            }
        }

        // --- Priority 6: First focus of the day ---
        if (!sessionState.FirstFocusMessageSent && inActiveFocus && focusSec >= 120)
        {
            sessionState.MarkFirstFocusMessageSent();
            string timeLabel = nowUtc.ToLocalTime().ToString("h:mm tt");
            string msg = name is not null
                ? $"Deep work started. {name}, Ari has you."
                : $"First focus block of the day — {timeLabel}. Ari is watching.";
            sessionState.RecordToastShown(nowUtc);
            return new UserCommunicationToast(Key: "ari:first_focus", Title: "Ari", Message: msg);
        }

        // --- Priority 7: Session wrap (idle after meaningful work) ---
        if (!sessionState.SessionWrapToastSent && !inActiveFocus && !snapshot.DistractionEpisodeActive)
        {
            double idleSec = (nowUtc - sessionState.LastActivityUtc).TotalSeconds;
            if (idleSec >= 1200 && sessionState.TotalFocusSecondsToday >= 1800) // 20min idle, 30min focus today
            {
                sessionState.MarkSessionWrapToastSent();
                int focusMin = (int)(sessionState.TotalFocusSecondsToday / 60);
                string msg = $"Session wrapped. {focusMin}min of focus today. Ari will send the digest tonight.";
                sessionState.RecordToastShown(nowUtc);
                return new UserCommunicationToast(Key: "ari:session_wrap", Title: "Ari", Message: msg);
            }
        }

        return null;
    }

    private static bool TryBuildRecentEventToast(
        LocalApiEventResponse recentEvent,
        out string title,
        out string message)
    {
        title = "Ari";
        message = string.Empty;
        return false;
    }

    private static string ToDisplayChannelName(string channel)
    {
        return channel.Trim().ToLowerInvariant() switch
        {
            "keyboard" => "keyboard",
            "mouse" => "mouse",
            _ => channel.Trim()
        };
    }

    private static string NormalizeAction(string? action)
    {
        return string.IsNullOrWhiteSpace(action)
            ? string.Empty
            : action.Trim().ToLowerInvariant();
    }

    private static UserDesktopWidgetCriticalState? ResolveCriticalEventState(
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc)
    {
        bool notificationsRecovered = false;
        bool brightnessRecovered = false;

        foreach (LocalApiEventResponse recentEvent in recentEvents.OrderByDescending(static evt => evt.TsUtc))
        {
            if ((nowUtc - recentEvent.TsUtc) > CriticalEventWindow)
            {
                continue;
            }

            string action = NormalizeAction(recentEvent.Action);
            if (action == "deep_work_notifications_restored")
            {
                notificationsRecovered = true;
                continue;
            }

            if (action == "overload_brightness_restored")
            {
                brightnessRecovered = true;
                continue;
            }

            if (!PersistentCriticalEventActions.Contains(action))
            {
                continue;
            }

            if (notificationsRecovered
                && action is "notification_snapshot_failed" or "notification_suppress_failed" or "notification_restore_failed")
            {
                continue;
            }

            if (brightnessRecovered
                && action is "brightness_unsupported" or "brightness_control_failed")
            {
                continue;
            }

            return BuildCriticalStateForEvent(action);
        }

        return null;
    }

    private static UserDesktopWidgetCriticalState BuildCriticalStateForEvent(string action)
    {
        return action switch
        {
            "notification_restore_failed" => BuildCriticalState(
                title: "Notifications need attention",
                message: "Ari could not restore Windows notification settings cleanly. Open Settings to verify banners, or inspect System before continuing.",
                primaryAction: UserDesktopWidgetAction.OpenSettings,
                primaryLabel: "Open Settings",
                secondaryAction: UserDesktopWidgetAction.OpenHealth,
                secondaryLabel: "Open System",
                tertiaryAction: UserDesktopWidgetAction.RetryCritical,
                tertiaryLabel: "Retry"),
            "notification_suppress_failed" => BuildCriticalState(
                title: "Notifications stayed on",
                message: "Ari could not mute Windows notification banners for deep work. Open Settings to adjust them manually, or inspect System for the failure.",
                primaryAction: UserDesktopWidgetAction.OpenSettings,
                primaryLabel: "Open Settings",
                secondaryAction: UserDesktopWidgetAction.OpenHealth,
                secondaryLabel: "Open System",
                tertiaryAction: UserDesktopWidgetAction.RetryCritical,
                tertiaryLabel: "Retry"),
            "notification_snapshot_failed" => BuildCriticalState(
                title: "Notification state unavailable",
                message: "Ari could not read the current Windows notification state. Open System to inspect the runtime, or retry the connection.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings"),
            "brightness_control_failed" => BuildCriticalState(
                title: "Brightness control failed",
                message: "Ari could not adjust screen brightness automatically. Open System to inspect the failure, or retry the device check.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings"),
            "brightness_unsupported" => BuildCriticalState(
                title: "Brightness control unavailable",
                message: "This device does not expose the controls Ari needs for automatic brightness changes. Open Settings or System for details.",
                primaryAction: UserDesktopWidgetAction.OpenSettings,
                primaryLabel: "Open Settings",
                secondaryAction: UserDesktopWidgetAction.OpenHealth,
                secondaryLabel: "Open System",
                tertiaryAction: UserDesktopWidgetAction.RetryCritical,
                tertiaryLabel: "Retry"),
            _ => BuildCriticalState(
                title: "Tracking limited",
                message: "Ari hit a runtime issue that needs attention. Open System for details or retry the connection.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings")
        };
    }

    private static UserDesktopWidgetCriticalState BuildCriticalState(
        string title,
        string message,
        UserDesktopWidgetAction primaryAction,
        string primaryLabel,
        UserDesktopWidgetAction secondaryAction,
        string secondaryLabel,
        UserDesktopWidgetAction? tertiaryAction = null,
        string tertiaryLabel = "")
    {
        return new UserDesktopWidgetCriticalState(
            Title: title,
            Message: message,
            PrimaryAction: primaryAction,
            PrimaryLabel: primaryLabel,
            SecondaryAction: secondaryAction,
            SecondaryLabel: secondaryLabel,
            TertiaryAction: tertiaryAction,
            TertiaryLabel: tertiaryLabel);
    }
}
