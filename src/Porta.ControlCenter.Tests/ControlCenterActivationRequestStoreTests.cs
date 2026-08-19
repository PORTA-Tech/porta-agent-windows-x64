namespace Porta.ControlCenter.Tests;

public sealed class ControlCenterActivationRequestStoreTests
{
    [Theory]
    [InlineData("--section=now", "Now")]
    [InlineData("--section=today", "Now")]
    [InlineData("--section=history", "History")]
    [InlineData("--section=actions", "Actions")]
    [InlineData("--section=tracking", "Tracking")]
    [InlineData("--section=settings", "Settings")]
    [InlineData("--section=health", "Health")]
    [InlineData("--section=system", "Health")]
    // Phase 4.1 (2026-08-18) — the three the widget menu gained. Without these the menu entries
    // would open Control Center on whatever page it last showed, which reads as a dead button.
    [InlineData("--section=tasks", "Tasks")]
    [InlineData("--section=projects", "Projects")]
    [InlineData("--section=reports", "Reports")]
    public void ParseArgs_UsesRequestedSection(string arg, string expected)
    {
        ControlCenterActivationTarget target = ControlCenterActivationRequestStore.ParseArgs([arg]);

        Assert.Equal(expected, target.ToString());
    }

    [Fact]
    public void ParseArgs_KeepsControlsAliasForSettings()
    {
        ControlCenterActivationTarget target = ControlCenterActivationRequestStore.ParseArgs(["--section=controls"]);

        Assert.Equal(ControlCenterActivationTarget.Settings, target);
    }

    [Theory]
    [InlineData("--section=actions", "Actions")]
    [InlineData("--section=tracking", "Tracking")]
    public void ParseArgs_StillAnswersTheSectionsTheWidgetMenuDropped(string arg, string expected)
    {
        // Phase 4.1 removed Actions and Tracking from the widget MENU, not from this parser.
        // During an upgrade an older widget is still running against a newer Control Center, and
        // it will keep asking for these by name until it is replaced.
        ControlCenterActivationTarget target = ControlCenterActivationRequestStore.ParseArgs([arg]);

        Assert.Equal(expected, target.ToString());
    }
}
