using Porta.Agent.Common;

namespace Porta.Agent.Core.Tests;

public sealed class PortaActionSemanticsTests
{
    [Theory]
    [InlineData("break_suggestion")]
    [InlineData("focus_lock_in_suggestion")]
    [InlineData("stretch_break_suggestion")]
    [InlineData("deep_work_notifications_suppressed")]
    [InlineData("deep_work_notifications_restored")]
    public void ClassifyAction_RealActions_AreClassifiedHonestly(string action)
    {
        string category = PortaActionSemantics.ClassifyAction(action, commandType: null);
        Assert.Equal("Real Action", category);
        Assert.True(PortaActionSemantics.IsRealDesktopAction(action, commandType: null));
    }

    [Theory]
    [InlineData("focus_mode_enabled")]
    [InlineData("focus_assist_enabled")]
    [InlineData("fatigue_relief_enabled")]
    [InlineData("temporary_app_restriction_enabled")]
    public void ClassifyAction_PolicyIntentActions_AreClassifiedHonestly(string action)
    {
        string category = PortaActionSemantics.ClassifyAction(action, commandType: null);
        Assert.Equal("Policy Intent", category);
        Assert.True(PortaActionSemantics.IsPolicyIntentAction(action));
        Assert.False(PortaActionSemantics.IsRealDesktopAction(action, commandType: null));
    }

    [Theory]
    [InlineData("overload_brightness_reduced")]
    [InlineData("overload_brightness_restored")]
    [InlineData("brightness_unsupported")]
    [InlineData("brightness_control_failed")]
    public void ClassifyAction_BrightnessActions_AreUnsupported(string action)
    {
        string category = PortaActionSemantics.ClassifyAction(action, commandType: null);
        Assert.Equal("Unsupported", category);
        Assert.True(PortaActionSemantics.IsUnsupportedAction(action));
    }

    [Theory]
    [InlineData("focus_mode_enabled", "activated")]
    [InlineData("focus_mode_enabled", "deactivated")]
    [InlineData("focus_assist_enabled", "activated")]
    [InlineData("fatigue_relief_enabled", "activated")]
    [InlineData("temporary_app_restriction_enabled", "activated")]
    public void DescribePolicyIntent_DoesNotClaimDesktopExecution(string action, string lifecycleState)
    {
        string text = PortaActionSemantics.DescribePolicyIntent(action, lifecycleState);
        Assert.Contains("policy intent", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("applied", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enabled", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("activated", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("overload_brightness_reduced")]
    [InlineData("overload_brightness_restored")]
    [InlineData("brightness_unsupported")]
    [InlineData("brightness_control_failed")]
    public void DescribeBrightnessAction_DoesNotClaimSupportedDesktopExecution(string action)
    {
        string text = PortaActionSemantics.DescribeBrightnessAction(action);
        Assert.Contains("brightness", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entered reduced mode", text, StringComparison.OrdinalIgnoreCase);
    }
}
