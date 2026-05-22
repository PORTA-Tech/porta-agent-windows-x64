namespace Porta.UserAgent.Interventions;

using Porta.Agent.Common;

internal readonly record struct UserInterventionGateDecision(
    bool Allowed,
    string ReasonCode);

internal sealed class UserInterventionAntiSpamGate
{
    private static readonly TimeSpan GlobalPromptWindow = TimeSpan.FromMinutes(12);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastPromptFamilyShownUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastToastSemanticShownUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToastFamilyReceipt> _lastToastFamilyShownUtc = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPromptShownUtc = DateTimeOffset.MinValue;
    private bool _criticalStateVisible;

    public void ObserveWidgetState(UserDesktopWidgetState state)
    {
        lock (_gate)
        {
            _criticalStateVisible = state.CriticalState != null;
        }
    }

    public bool IsCriticalStateVisible()
    {
        lock (_gate)
        {
            return _criticalStateVisible;
        }
    }

    public UserInterventionGateDecision EvaluatePrompt(string? action, DateTimeOffset nowUtc)
    {
        string family = ResolvePromptFamily(action);

        lock (_gate)
        {
            if (_criticalStateVisible)
            {
                return new UserInterventionGateDecision(false, "anti_spam_critical_state");
            }

            if (_lastPromptShownUtc != DateTimeOffset.MinValue
                && (nowUtc - _lastPromptShownUtc) < GlobalPromptWindow)
            {
                return new UserInterventionGateDecision(false, "anti_spam_global_prompt_budget");
            }

            if (_lastPromptFamilyShownUtc.TryGetValue(family, out DateTimeOffset lastFamilyShownUtc)
                && (nowUtc - lastFamilyShownUtc) < ResolvePromptFamilyWindow(family))
            {
                return new UserInterventionGateDecision(false, "anti_spam_prompt_family_budget");
            }

            return new UserInterventionGateDecision(true, string.Empty);
        }
    }

    public void MarkPromptShown(string? action, DateTimeOffset nowUtc)
    {
        string family = ResolvePromptFamily(action);

        lock (_gate)
        {
            _lastPromptShownUtc = nowUtc;
            _lastPromptFamilyShownUtc[family] = nowUtc;
        }
    }

    public UserInterventionGateDecision EvaluateToast(string? action, DateTimeOffset nowUtc)
    {
        string semanticKey = ResolveToastSemanticKey(action);
        string family = ResolveToastFamily(action);
        int priority = ResolveToastPriority(action);

        lock (_gate)
        {
            if (_criticalStateVisible)
            {
                return new UserInterventionGateDecision(false, "anti_spam_critical_state");
            }

            if (_lastToastSemanticShownUtc.TryGetValue(semanticKey, out DateTimeOffset lastSemanticShownUtc)
                && (nowUtc - lastSemanticShownUtc) < ResolveToastSemanticWindow(action))
            {
                return new UserInterventionGateDecision(false, "anti_spam_duplicate_semantic_toast");
            }

            if (_lastToastFamilyShownUtc.TryGetValue(family, out ToastFamilyReceipt familyReceipt)
                && (nowUtc - familyReceipt.ShownAtUtc) < ResolveToastFamilyWindow(action)
                && familyReceipt.Priority > priority)
            {
                return new UserInterventionGateDecision(false, "anti_spam_weaker_family_toast");
            }

            return new UserInterventionGateDecision(true, string.Empty);
        }
    }

    public void MarkToastShown(string? action, DateTimeOffset nowUtc)
    {
        string semanticKey = ResolveToastSemanticKey(action);
        string family = ResolveToastFamily(action);
        int priority = ResolveToastPriority(action);

        lock (_gate)
        {
            _lastToastSemanticShownUtc[semanticKey] = nowUtc;
            _lastToastFamilyShownUtc[family] = new ToastFamilyReceipt(priority, nowUtc);
        }
    }

    private static string ResolvePromptFamily(string? action)
    {
        // Break prompts (short_break, long_break, and legacy IDs) all share the
        // recovery family. Anti-spam uses this to enforce the 30-minute family window.
        string normalized = NormalizeAction(action);
        if (PortaBreakSemantics.IsBreakSuggestionAction(normalized)) return "prompt:recovery";
        return normalized == "focus_lock_in_suggestion" ? "prompt:focus" : "prompt:general";
    }

    private static TimeSpan ResolvePromptFamilyWindow(string family)
    {
        return family switch
        {
            "prompt:recovery" => TimeSpan.FromMinutes(30),
            "prompt:focus" => TimeSpan.FromMinutes(15),
            _ => GlobalPromptWindow
        };
    }

    private static string ResolveToastSemanticKey(string? action)
    {
        return NormalizeAction(action) switch
        {
            "inform_overload_risk" => "toast:overload:inform",
            "focus_mode_enabled" => "toast:overload:guidance",
            "temporary_app_restriction_enabled" => "toast:overload:restrict",
            "overload_brightness_reduced" => "toast:overload:brightness_reduced",
            "overload_brightness_restored" => "toast:overload:brightness_restored",
            "overload_recovered" => "toast:overload:recovered",
            "inform_focus_protection" => "toast:focus:inform",
            "focus_assist_enabled" => "toast:focus:guidance",
            "deep_work_notifications_suppressed" => "toast:focus:notifications_suppressed",
            "deep_work_notifications_restored" => "toast:focus:notifications_restored",
            "inform_fatigue_prevention" => "toast:fatigue:inform",
            "fatigue_relief_enabled" => "toast:fatigue:guidance",
            "brightness_unsupported" => "toast:brightness:unsupported",
            "brightness_control_failed" => "toast:brightness:failed",
            "notification_snapshot_failed" => "toast:notifications:snapshot_failed",
            "notification_suppress_failed" => "toast:notifications:suppress_failed",
            "notification_restore_failed" => "toast:notifications:restore_failed",
            _ => string.IsNullOrWhiteSpace(action)
                ? "toast:general"
                : $"toast:{NormalizeAction(action)}"
        };
    }

    private static string ResolveToastFamily(string? action)
    {
        return NormalizeAction(action) switch
        {
            "inform_overload_risk"
                or "focus_mode_enabled"
                or "temporary_app_restriction_enabled"
                or "overload_brightness_reduced"
                or "overload_brightness_restored"
                or "overload_recovered"
                or "brightness_unsupported"
                or "brightness_control_failed"
                => "toast-family:overload",
            "inform_focus_protection"
                or "focus_assist_enabled"
                or "deep_work_notifications_suppressed"
                or "deep_work_notifications_restored"
                or "notification_snapshot_failed"
                or "notification_suppress_failed"
                or "notification_restore_failed"
                => "toast-family:focus",
            "inform_fatigue_prevention"
                or "fatigue_relief_enabled"
                => "toast-family:fatigue",
            _ => "toast-family:general"
        };
    }

    private static int ResolveToastPriority(string? action)
    {
        return NormalizeAction(action) switch
        {
            "notification_snapshot_failed"
                or "notification_suppress_failed"
                or "notification_restore_failed"
                or "brightness_unsupported"
                or "brightness_control_failed"
                => 90,
            "temporary_app_restriction_enabled" => 80,
            "overload_brightness_reduced"
                or "deep_work_notifications_suppressed"
                or "focus_mode_enabled"
                or "focus_assist_enabled"
                or "fatigue_relief_enabled"
                => 60,
            "deep_work_notifications_restored"
                or "overload_brightness_restored"
                or "overload_recovered"
                => 30,
            "inform_overload_risk"
                or "inform_focus_protection"
                or "inform_fatigue_prevention"
                => 20,
            _ => 10
        };
    }

    private static TimeSpan ResolveToastSemanticWindow(string? action)
    {
        return NormalizeAction(action) switch
        {
            "inform_overload_risk" => TimeSpan.FromMinutes(15),
            "inform_focus_protection"
                or "inform_fatigue_prevention"
                => TimeSpan.FromSeconds(60),
            "focus_mode_enabled"
                or "focus_assist_enabled"
                or "fatigue_relief_enabled"
                or "temporary_app_restriction_enabled"
                => TimeSpan.FromMinutes(15),
            "deep_work_notifications_suppressed"
                or "deep_work_notifications_restored"
                or "overload_brightness_reduced"
                or "overload_brightness_restored"
                or "overload_recovered"
                => TimeSpan.FromMinutes(10),
            "notification_snapshot_failed"
                or "notification_suppress_failed"
                or "notification_restore_failed"
                or "brightness_unsupported"
                or "brightness_control_failed"
                => TimeSpan.FromMinutes(20),
            _ => TimeSpan.FromMinutes(5)
        };
    }

    private static TimeSpan ResolveToastFamilyWindow(string? action)
    {
        return NormalizeAction(action) switch
        {
            "notification_snapshot_failed"
                or "notification_suppress_failed"
                or "notification_restore_failed"
                or "brightness_unsupported"
                or "brightness_control_failed"
                => TimeSpan.FromMinutes(20),
            "deep_work_notifications_suppressed"
                or "deep_work_notifications_restored"
                or "overload_brightness_reduced"
                or "overload_brightness_restored"
                or "overload_recovered"
                => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(15)
        };
    }

    private static string NormalizeAction(string? action)
    {
        return string.IsNullOrWhiteSpace(action)
            ? string.Empty
            : action.Trim().ToLowerInvariant();
    }

    private readonly record struct ToastFamilyReceipt(
        int Priority,
        DateTimeOffset ShownAtUtc);
}
