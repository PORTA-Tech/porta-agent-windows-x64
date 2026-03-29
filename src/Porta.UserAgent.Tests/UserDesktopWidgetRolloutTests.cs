using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetRolloutTests
{
    [Fact]
    public void FromEnvironment_DefaultsToWidgetSurface()
    {
        UserDesktopWidgetRollout rollout = UserDesktopWidgetRollout.FromEnvironment(_ => null);

        Assert.True(rollout.WidgetEnabled);
        Assert.False(rollout.ForceToastFallback);
        Assert.True(rollout.SupportsWidgetSurface);
        Assert.Equal(string.Empty, rollout.UnavailableReasonCode);
    }

    [Fact]
    public void FromEnvironment_DisablesWidgetWhenFlagIsOff()
    {
        UserDesktopWidgetRollout rollout = UserDesktopWidgetRollout.FromEnvironment(name => name switch
        {
            "PORTA_DESKTOP_WIDGET_ENABLED" => "false",
            _ => null
        });

        Assert.False(rollout.WidgetEnabled);
        Assert.False(rollout.SupportsWidgetSurface);
        Assert.Equal("widget_disabled_by_flag", rollout.UnavailableReasonCode);
    }

    [Fact]
    public void FromEnvironment_ForcesToastFallbackWhenRequested()
    {
        UserDesktopWidgetRollout rollout = UserDesktopWidgetRollout.FromEnvironment(name => name switch
        {
            "PORTA_DESKTOP_WIDGET_FORCE_TOAST_FALLBACK" => "1",
            _ => null
        });

        Assert.True(rollout.WidgetEnabled);
        Assert.True(rollout.ForceToastFallback);
        Assert.False(rollout.SupportsWidgetSurface);
        Assert.Equal("widget_forced_toast_fallback", rollout.UnavailableReasonCode);
    }
}
