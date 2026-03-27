using Porta.ControlCenter.Identity;
using Porta.ControlCenter.Updates;

namespace Porta.ControlCenter.Tests;

public sealed class WindowsUpdatePresentationStateBuilderTests
{
    [Fact]
    public void Build_ShowsRequiredBadge_WhenReleaseIsBelowMinimumSupportedVersion()
    {
        ControlPlaneWindowsReleaseManifest release = new(
            Platform: "windows",
            Channel: "stable",
            Version: "1.1.0",
            MsiUrl: "https://example.test/AriAgentInstaller-1.1.0.msi",
            PublishedAtUtc: DateTimeOffset.Parse("2026-03-27T09:00:00Z"),
            MinimumSupportedVersion: "1.0.5",
            Required: false,
            Title: null,
            Summary: "Quality and recovery fixes.",
            Sha256: "abc123");

        WindowsUpdatePresentationState state = WindowsUpdatePresentationStateBuilder.Build(
            uiVersion: "1.0.4",
            release,
            statusText: null);

        Assert.True(state.HasUpdate);
        Assert.True(state.AccountBadgeVisible);
        Assert.True(state.GlobalButtonVisible);
        Assert.True(state.TaskbarOverlayVisible);
        Assert.Equal("Update required", state.GlobalButtonText);
        Assert.Contains("required because version 1.0.4 is below the supported floor", state.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update available", state.TaskbarDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ShowsReadyBadge_ForOptionalNewerRelease()
    {
        ControlPlaneWindowsReleaseManifest release = new(
            Platform: "windows",
            Channel: "stable",
            Version: "1.1.0",
            MsiUrl: "https://example.test/AriAgentInstaller-1.1.0.msi",
            PublishedAtUtc: DateTimeOffset.Parse("2026-03-27T09:00:00Z"),
            MinimumSupportedVersion: "1.0.0",
            Required: false,
            Title: null,
            Summary: "A calmer update experience.",
            Sha256: "abc123");

        WindowsUpdatePresentationState state = WindowsUpdatePresentationStateBuilder.Build(
            uiVersion: "1.0.9",
            release,
            statusText: null);

        Assert.Equal("Update ready", state.GlobalButtonText);
        Assert.Contains("Install 1.1.0 now", state.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("come back later", state.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveAvailableRelease_ReturnsNull_WhenReleaseIsNotNewer()
    {
        ControlPlaneWindowsReleaseManifest release = new(
            Platform: "windows",
            Channel: "stable",
            Version: "1.0.9",
            MsiUrl: "https://example.test/AriAgentInstaller-1.0.9.msi",
            PublishedAtUtc: DateTimeOffset.Parse("2026-03-27T09:00:00Z"),
            MinimumSupportedVersion: null,
            Required: false,
            Title: null,
            Summary: null,
            Sha256: "abc123");

        ControlPlaneWindowsReleaseManifest? resolved = WindowsUpdatePresentationStateBuilder.ResolveAvailableRelease("1.0.9", release);

        Assert.Null(resolved);
    }

    [Fact]
    public void BuildInstallerScript_ContainsSilentUpgradeVerificationAndRestartSteps()
    {
        string script = WindowsUpdateInstallScriptBuilder.Build();

        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("msiexec.exe", script, StringComparison.Ordinal);
        Assert.Contains("\"/qn\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/norestart\"", script, StringComparison.Ordinal);
        Assert.Contains("REBOOT=ReallySuppress", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Service -Name $script:Context.ServiceName", script, StringComparison.Ordinal);
        Assert.Contains("Start-Service -Name $script:Context.ServiceName", script, StringComparison.Ordinal);
        Assert.Contains("Write-UpdateStatus -Succeeded $true", script, StringComparison.Ordinal);
        Assert.Contains("Start-ControlCenter", script, StringComparison.Ordinal);
    }
}
