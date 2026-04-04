namespace Porta.ControlCenter.Tests;

public sealed class UserAgentRecoveryCopyTests
{
    [Fact]
    public void FormatStartupSourceLabel_UsesServiceOwnedLabels()
    {
        Assert.Equal(
            "Ari Service watchdog",
            UserAgentRecoveryCopy.FormatStartupSourceLabel("service-watchdog", "n/a"));
        Assert.Equal(
            "Legacy Control Center recovery",
            UserAgentRecoveryCopy.FormatStartupSourceLabel("control-center-recovery", "n/a"));
        Assert.Equal(
            "n/a",
            UserAgentRecoveryCopy.FormatStartupSourceLabel(null, "n/a"));
    }

    [Fact]
    public void RecoveryWarnings_PointToAriServiceOwnership()
    {
        string captureFailure = UserAgentRecoveryCopy.BuildCaptureFailureWarningText("user-agent heartbeat stopped updating");

        Assert.Contains("Ari Service manages Porta.UserAgent recovery", captureFailure, StringComparison.Ordinal);
        Assert.Contains("user-agent heartbeat stopped updating", captureFailure, StringComparison.Ordinal);
        Assert.Contains("Ari Service managed interactive session", UserAgentRecoveryCopy.HeartbeatWaitingWarningText, StringComparison.Ordinal);
    }
}
