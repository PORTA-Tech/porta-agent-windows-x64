namespace Porta.ControlCenter;

internal static class UserAgentRecoveryCopy
{
    internal const string HeartbeatWaitingWarningText =
        "Waiting for a fresh user-agent heartbeat from the Ari Service managed interactive session.";

    internal static string BuildCaptureFailureWarningText(string captureFailureDetail)
    {
        return $"Live capture is not trusted: {captureFailureDetail}. Ari Service manages Porta.UserAgent recovery in the interactive session.";
    }

    internal static string FormatStartupSourceLabel(string? startupSource, string notAvailableText)
    {
        return startupSource?.Trim().ToLowerInvariant() switch
        {
            "run-key" => "Windows Run key",
            "windows-restart" => "Windows restart recovery",
            "service-watchdog" => "Ari Service watchdog",
            "control-center-recovery" => "Legacy Control Center recovery",
            "" or null => notAvailableText,
            _ => startupSource!
        };
    }
}
