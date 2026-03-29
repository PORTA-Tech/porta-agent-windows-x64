namespace Porta.ControlCenter.Tests;

public sealed class ControlCenterActivationRequestStoreTests
{
    [Theory]
    [InlineData("--section=now", "Now")]
    [InlineData("--section=actions", "Actions")]
    [InlineData("--section=settings", "Settings")]
    [InlineData("--section=health", "Health")]
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
}
