using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserInterventionAntiSpamGateTests
{
    [Fact]
    public void EvaluatePrompt_UsesGlobalPromptBudget()
    {
        var gate = new UserInterventionAntiSpamGate();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");

        Assert.True(gate.EvaluatePrompt("focus_lock_in_suggestion", nowUtc).Allowed);
        gate.MarkPromptShown("focus_lock_in_suggestion", nowUtc);

        UserInterventionGateDecision blocked = gate.EvaluatePrompt("break_suggestion", nowUtc.AddMinutes(5));

        Assert.False(blocked.Allowed);
        Assert.Equal("anti_spam_global_prompt_budget", blocked.ReasonCode);
    }

    [Fact]
    public void EvaluatePrompt_UsesFamilyBudgetAfterGlobalWindowExpires()
    {
        var gate = new UserInterventionAntiSpamGate();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");

        gate.MarkPromptShown("break_suggestion", nowUtc);

        UserInterventionGateDecision blocked = gate.EvaluatePrompt("stretch_break_suggestion", nowUtc.AddMinutes(20));

        Assert.False(blocked.Allowed);
        Assert.Equal("anti_spam_prompt_family_budget", blocked.ReasonCode);
    }

    [Fact]
    public void EvaluateToast_DeduplicatesSameSemanticToastWithinWindow()
    {
        var gate = new UserInterventionAntiSpamGate();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");

        gate.MarkToastShown("inform_overload_risk", nowUtc);

        UserInterventionGateDecision blocked = gate.EvaluateToast("inform_overload_risk", nowUtc.AddSeconds(30));
        UserInterventionGateDecision allowed = gate.EvaluateToast("inform_overload_risk", nowUtc.AddSeconds(61));

        Assert.False(blocked.Allowed);
        Assert.Equal("anti_spam_duplicate_semantic_toast", blocked.ReasonCode);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void EvaluateToast_SuppressesWeakerToastAfterStrongerFamilyToast()
    {
        var gate = new UserInterventionAntiSpamGate();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");

        gate.MarkToastShown("temporary_app_restriction_enabled", nowUtc);

        UserInterventionGateDecision blocked = gate.EvaluateToast("inform_overload_risk", nowUtc.AddMinutes(5));
        UserInterventionGateDecision allowed = gate.EvaluateToast("brightness_control_failed", nowUtc.AddMinutes(5));

        Assert.False(blocked.Allowed);
        Assert.Equal("anti_spam_weaker_family_toast", blocked.ReasonCode);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void EvaluatePromptAndToast_BlockWhenCriticalStateVisible()
    {
        var gate = new UserInterventionAntiSpamGate();
        gate.ObserveWidgetState(new UserDesktopWidgetState(
            Mode: UserDesktopWidgetMode.Limited,
            Title: "Tracking limited",
            StatusText: "Critical",
            MessageSeverity: UserDesktopWidgetMessageSeverity.Critical,
            FooterText: "Open System",
            PrimaryMetric: new UserDesktopWidgetMetric("Work today", "n/a"),
            SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "n/a"),
            TertiaryMetric: new UserDesktopWidgetMetric("Pressure", "n/a"),
            CanPause: false,
            CanResume: false,
            CriticalState: new UserDesktopWidgetCriticalState(
                Title: "Core service offline",
                Message: "Open System",
                PrimaryAction: UserDesktopWidgetAction.OpenHealth,
                PrimaryLabel: "Open System",
                SecondaryAction: UserDesktopWidgetAction.RetryCritical,
                SecondaryLabel: "Retry")));

        UserInterventionGateDecision promptBlocked = gate.EvaluatePrompt("break_suggestion", DateTimeOffset.Parse("2026-03-29T10:05:00Z"));
        UserInterventionGateDecision toastBlocked = gate.EvaluateToast("inform_overload_risk", DateTimeOffset.Parse("2026-03-29T10:05:00Z"));

        Assert.False(promptBlocked.Allowed);
        Assert.False(toastBlocked.Allowed);
        Assert.Equal("anti_spam_critical_state", promptBlocked.ReasonCode);
        Assert.Equal("anti_spam_critical_state", toastBlocked.ReasonCode);
    }

    [Fact]
    public void ObserveWidgetState_NonCriticalStateClearsCriticalVisibility()
    {
        var gate = new UserInterventionAntiSpamGate();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:05:00Z");

        gate.ObserveWidgetState(new UserDesktopWidgetState(
            Mode: UserDesktopWidgetMode.Limited,
            Title: "Tracking limited",
            StatusText: "Critical",
            MessageSeverity: UserDesktopWidgetMessageSeverity.Critical,
            FooterText: "Open System",
            PrimaryMetric: new UserDesktopWidgetMetric("Work today", "n/a"),
            SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "n/a"),
            TertiaryMetric: new UserDesktopWidgetMetric("Pressure", "n/a"),
            CanPause: false,
            CanResume: false,
            CriticalState: new UserDesktopWidgetCriticalState(
                Title: "Core service offline",
                Message: "Open System",
                PrimaryAction: UserDesktopWidgetAction.OpenHealth,
                PrimaryLabel: "Open System",
                SecondaryAction: UserDesktopWidgetAction.RetryCritical,
                SecondaryLabel: "Retry")));

        gate.ObserveWidgetState(new UserDesktopWidgetState(
            Mode: UserDesktopWidgetMode.Monitoring,
            Title: "Ari",
            StatusText: "Monitoring",
            MessageSeverity: UserDesktopWidgetMessageSeverity.Neutral,
            FooterText: "Ready",
            PrimaryMetric: new UserDesktopWidgetMetric("Work today", "0m"),
            SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "0m"),
            TertiaryMetric: new UserDesktopWidgetMetric("Pressure", "0%"),
            CanPause: true,
            CanResume: false,
            CriticalState: null));

        Assert.True(gate.EvaluatePrompt("break_suggestion", nowUtc).Allowed);
        Assert.True(gate.EvaluateToast("inform_overload_risk", nowUtc).Allowed);
    }
}
