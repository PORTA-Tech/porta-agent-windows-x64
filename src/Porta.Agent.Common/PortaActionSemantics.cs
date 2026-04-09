namespace Porta.Agent.Common;

public static class PortaActionSemantics
{
    public static bool IsPolicyIntentAction(string? action)
    {
        string normalized = NormalizeAction(action);
        return normalized is "focus_mode_enabled"
            or "focus_assist_enabled"
            or "fatigue_relief_enabled"
            or "temporary_app_restriction_enabled";
    }

    public static bool IsUnsupportedAction(string? action)
    {
        string normalized = NormalizeAction(action);
        return normalized.Contains("brightness", StringComparison.Ordinal);
    }

    public static bool IsRealDesktopAction(string? action, string? commandType)
    {
        string normalized = NormalizeAction(action);
        if (PortaBreakSemantics.IsBreakSuggestionAction(normalized)
            || normalized is "focus_lock_in_suggestion"
            or "deep_work_notifications_suppressed"
            or "deep_work_notifications_restored"
            or "notification_snapshot_failed"
            or "notification_suppress_failed"
            or "notification_restore_failed")
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(commandType);
    }

    public static string ClassifyAction(string? action, string? commandType)
    {
        if (IsPolicyIntentAction(action))
        {
            return "Policy Intent";
        }

        if (IsUnsupportedAction(action))
        {
            return "Unsupported";
        }

        if (IsRealDesktopAction(action, commandType))
        {
            return "Real Action";
        }

        return "Decision Record";
    }

    public static string DescribePolicyIntent(string action, string lifecycleState)
    {
        string normalized = NormalizeAction(action);
        bool deactivated = string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase);

        return normalized switch
        {
            "focus_mode_enabled" when deactivated
                => "Focus mode policy intent ended. No concrete desktop restoration executor exists in this build.",
            "focus_mode_enabled"
                => "Focus mode policy intent was recorded. No concrete desktop executor exists in this build.",
            "focus_assist_enabled" when deactivated
                => "Focus Assist policy intent ended. No concrete desktop restoration executor exists in this build.",
            "focus_assist_enabled"
                => "Focus Assist policy intent was recorded. No concrete desktop executor exists in this build.",
            "fatigue_relief_enabled" when deactivated
                => "Fatigue relief policy intent ended. No concrete desktop restoration executor exists in this build.",
            "fatigue_relief_enabled"
                => "Fatigue relief policy intent was recorded. No concrete desktop executor exists in this build.",
            "temporary_app_restriction_enabled" when deactivated
                => "Temporary app restriction policy intent ended. No concrete desktop restoration executor exists in this build.",
            "temporary_app_restriction_enabled"
                => "Temporary app restriction policy intent was recorded. No concrete desktop executor exists in this build.",
            _ when deactivated
                => "Policy intent ended. No concrete desktop restoration executor exists in this build.",
            _
                => "Policy intent was recorded. No concrete desktop executor exists in this build."
        };
    }

    public static string DescribeBrightnessAction(string action)
    {
        string normalized = NormalizeAction(action);
        return normalized switch
        {
            "overload_brightness_reduced"
                => "Brightness reduction intent was recorded, but brightness control is unsupported on this device/build.",
            "overload_brightness_restored"
                => "Brightness restoration intent was recorded, but brightness control is unsupported on this device/build.",
            "brightness_unsupported"
                => "Brightness control is unsupported on this device/build. No desktop change was applied.",
            "brightness_control_failed"
                => "Brightness control attempt failed. No desktop change was applied.",
            _
                => "No brightness action change."
        };
    }

    private static string NormalizeAction(string? action) =>
        action?.Trim().ToLowerInvariant() ?? string.Empty;
}
