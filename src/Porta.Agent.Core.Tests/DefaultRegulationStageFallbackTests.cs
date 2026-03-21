using System.Reflection;

namespace Porta.Agent.Core.Tests;

public sealed class DefaultRegulationStageFallbackTests
{
    [Fact]
    public void ApplyUserCategoryGates_OverloadBreakSuggestion_DowngradesToInform()
    {
        string action = InvokeApplyUserCategoryGates(
            RegulationDomain.Overload,
            "break_suggestion",
            RegulationEscalationLevel.Suggest,
            BuildControl());

        Assert.Equal("inform_overload_risk", action);
    }

    [Fact]
    public void ApplyUserCategoryGates_FatigueStretchSuggestion_DowngradesToInform()
    {
        string action = InvokeApplyUserCategoryGates(
            RegulationDomain.Fatigue,
            "stretch_break_suggestion",
            RegulationEscalationLevel.Suggest,
            BuildControl());

        Assert.Equal("inform_fatigue_prevention", action);
    }

    [Fact]
    public void ApplyUserCategoryGates_OverloadRestrictWithoutEnabledCategories_DowngradesToInform()
    {
        string action = InvokeApplyUserCategoryGates(
            RegulationDomain.Overload,
            "temporary_app_restriction_enabled",
            RegulationEscalationLevel.Restrict,
            BuildControl());

        Assert.Equal("inform_overload_risk", action);
    }

    [Fact]
    public void ApplyUserCategoryGates_FocusSuggestion_RemainsSuggestion()
    {
        string action = InvokeApplyUserCategoryGates(
            RegulationDomain.Focus,
            "focus_lock_in_suggestion",
            RegulationEscalationLevel.Suggest,
            BuildControl());

        Assert.Equal("focus_lock_in_suggestion", action);
    }

    private static AgentControlState BuildControl(
        bool notificationReductionEnabled = false,
        bool focusModeEnabled = false,
        bool animationReductionEnabled = false,
        bool temporaryAppRestrictionEnabled = false)
    {
        return new AgentControlState(
            Paused: false,
            ConsentActive: true,
            PauseUntilUtc: null,
            RegulationIntensity: "Balanced",
            NotificationReductionEnabled: notificationReductionEnabled,
            FocusModeEnabled: focusModeEnabled,
            AnimationReductionEnabled: animationReductionEnabled,
            TemporaryAppRestrictionEnabled: temporaryAppRestrictionEnabled);
    }

    private static string InvokeApplyUserCategoryGates(
        RegulationDomain domain,
        string action,
        RegulationEscalationLevel level,
        AgentControlState control)
    {
        MethodInfo method = typeof(DefaultRegulationStage).GetMethod(
            "ApplyUserCategoryGates",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ApplyUserCategoryGates was not found.");

        return (string)(method.Invoke(null, [domain, action, level, control])
            ?? throw new InvalidOperationException("ApplyUserCategoryGates returned null."));
    }
}
