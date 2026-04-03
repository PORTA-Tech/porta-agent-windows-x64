namespace Porta.UserAgent.Interventions;

using Porta.Agent.LocalApi;

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
        if (status == null)
        {
            if (statusHttpCode is 401 or 403)
            {
                return BuildCriticalState(
                    title: "Unavailable",
                    message: "Ari can see the local runtime, but this session needs refreshed local service access. Open System or retry.",
                    primaryAction: UserDesktopWidgetAction.OpenHealth,
                    primaryLabel: "Open System",
                    secondaryAction: UserDesktopWidgetAction.RetryCritical,
                    secondaryLabel: "Retry",
                    tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                    tertiaryLabel: "Settings");
            }

            return BuildCriticalState(
                title: "Unavailable",
                message: "Ari could not complete the local API request. Open System to check the runtime, or retry the connection.",
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings");
        }

        if (!status.LocalApiAuthReady)
        {
            return BuildCriticalState(
                title: "Unavailable",
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
                title: "Unavailable",
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

        UserCommunicationToast? degradedToast = ResolveCaptureDegradedState(status, snapshot);
        if (degradedToast != null)
        {
            return BuildCriticalState(
                title: degradedToast.Title,
                message: degradedToast.Message,
                primaryAction: UserDesktopWidgetAction.OpenHealth,
                primaryLabel: "Open System",
                secondaryAction: UserDesktopWidgetAction.RetryCritical,
                secondaryLabel: "Retry",
                tertiaryAction: UserDesktopWidgetAction.OpenSettings,
                tertiaryLabel: "Settings");
        }

        return ResolveCriticalEventState(recentEvents ?? [], nowUtc);
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
