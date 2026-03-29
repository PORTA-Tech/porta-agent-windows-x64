namespace Porta.Agent.Common.UserAgent;

public enum CurrentSessionUserAgentAction
{
    KeepExisting,
    WaitForHeartbeat,
    LaunchNew,
    RestartExisting
}

public sealed record RunningUserAgentProcessSnapshot(
    int ProcessId,
    int SessionId,
    DateTimeOffset? StartedAtUtc = null);

public sealed record CurrentSessionUserAgentDecision(
    CurrentSessionUserAgentAction Action,
    string Reason);

public static class CurrentSessionUserAgentRecoveryEvaluator
{
    public static readonly TimeSpan HeartbeatFreshnessWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LaunchGraceWindow = TimeSpan.FromSeconds(12);

    public static CurrentSessionUserAgentDecision Evaluate(
        int currentSessionId,
        IReadOnlyList<RunningUserAgentProcessSnapshot> processes,
        UserAgentSessionContextSnapshot? sessionContext,
        DateTimeOffset nowUtc,
        string? expectedBinaryVersion = null)
    {
        RunningUserAgentProcessSnapshot[] sameSessionProcesses = processes
            .Where(process => process.SessionId == currentSessionId)
            .ToArray();

        if (sameSessionProcesses.Length == 0)
        {
            return new(CurrentSessionUserAgentAction.LaunchNew, "missing_process");
        }

        if (HasHealthyHeartbeat(currentSessionId, sameSessionProcesses, sessionContext, nowUtc, expectedBinaryVersion))
        {
            return new(CurrentSessionUserAgentAction.KeepExisting, "healthy");
        }

        if (IsAwaitingHeartbeat(sameSessionProcesses, nowUtc))
        {
            return new(CurrentSessionUserAgentAction.WaitForHeartbeat, "awaiting_heartbeat");
        }

        return new(CurrentSessionUserAgentAction.RestartExisting, ResolveRestartReason(currentSessionId, sameSessionProcesses, sessionContext, nowUtc, expectedBinaryVersion));
    }

    private static bool HasHealthyHeartbeat(
        int currentSessionId,
        IReadOnlyList<RunningUserAgentProcessSnapshot> sameSessionProcesses,
        UserAgentSessionContextSnapshot? sessionContext,
        DateTimeOffset nowUtc,
        string? expectedBinaryVersion)
    {
        if (sessionContext is null)
        {
            return false;
        }

        if (sessionContext.SessionId != currentSessionId)
        {
            return false;
        }

        if (!string.Equals(sessionContext.CaptureContext, UserAgentCaptureContexts.UserSession, StringComparison.Ordinal))
        {
            return false;
        }

        if (sessionContext.ProcessId <= 0)
        {
            return false;
        }

        if (sessionContext.DesktopWidgetReady == false)
        {
            return false;
        }

        if (IsVersionMismatch(sessionContext.BinaryVersion, expectedBinaryVersion))
        {
            return false;
        }

        DateTimeOffset updatedAtUtc = sessionContext.UpdatedAtUtc.ToUniversalTime();
        if (updatedAtUtc > nowUtc || (nowUtc - updatedAtUtc) > HeartbeatFreshnessWindow)
        {
            return false;
        }

        return sameSessionProcesses.Any(process => process.ProcessId == sessionContext.ProcessId);
    }

    private static bool IsAwaitingHeartbeat(
        IReadOnlyList<RunningUserAgentProcessSnapshot> sameSessionProcesses,
        DateTimeOffset nowUtc)
    {
        return sameSessionProcesses.Any(process =>
            process.StartedAtUtc.HasValue
            && process.StartedAtUtc.Value <= nowUtc
            && (nowUtc - process.StartedAtUtc.Value) <= LaunchGraceWindow);
    }

    private static string ResolveRestartReason(
        int currentSessionId,
        IReadOnlyList<RunningUserAgentProcessSnapshot> sameSessionProcesses,
        UserAgentSessionContextSnapshot? sessionContext,
        DateTimeOffset nowUtc,
        string? expectedBinaryVersion)
    {
        if (sessionContext is null)
        {
            return "missing_heartbeat";
        }

        if (sessionContext.SessionId != currentSessionId)
        {
            return "session_mismatch";
        }

        if (!string.Equals(sessionContext.CaptureContext, UserAgentCaptureContexts.UserSession, StringComparison.Ordinal))
        {
            return "invalid_capture_context";
        }

        DateTimeOffset updatedAtUtc = sessionContext.UpdatedAtUtc.ToUniversalTime();
        if (updatedAtUtc > nowUtc || (nowUtc - updatedAtUtc) > HeartbeatFreshnessWindow)
        {
            return "stale_heartbeat";
        }

        if (sessionContext.ProcessId <= 0 || !sameSessionProcesses.Any(process => process.ProcessId == sessionContext.ProcessId))
        {
            return "process_mismatch";
        }

        if (IsVersionMismatch(sessionContext.BinaryVersion, expectedBinaryVersion))
        {
            return "version_mismatch";
        }

        if (sessionContext.DesktopWidgetReady == false)
        {
            return "widget_unavailable";
        }

        return "unhealthy_existing_process";
    }

    private static bool IsVersionMismatch(string? actualVersion, string? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(actualVersion))
        {
            return true;
        }

        return !string.Equals(actualVersion.Trim(), expectedVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
