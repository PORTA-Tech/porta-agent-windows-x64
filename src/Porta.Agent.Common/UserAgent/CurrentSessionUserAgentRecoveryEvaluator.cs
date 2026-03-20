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
        DateTimeOffset nowUtc)
    {
        RunningUserAgentProcessSnapshot[] sameSessionProcesses = processes
            .Where(process => process.SessionId == currentSessionId)
            .ToArray();

        if (sameSessionProcesses.Length == 0)
        {
            return new(CurrentSessionUserAgentAction.LaunchNew, "missing_process");
        }

        if (HasHealthyHeartbeat(currentSessionId, sameSessionProcesses, sessionContext, nowUtc))
        {
            return new(CurrentSessionUserAgentAction.KeepExisting, "healthy");
        }

        if (IsAwaitingHeartbeat(sameSessionProcesses, nowUtc))
        {
            return new(CurrentSessionUserAgentAction.WaitForHeartbeat, "awaiting_heartbeat");
        }

        return new(CurrentSessionUserAgentAction.RestartExisting, ResolveRestartReason(currentSessionId, sameSessionProcesses, sessionContext, nowUtc));
    }

    private static bool HasHealthyHeartbeat(
        int currentSessionId,
        IReadOnlyList<RunningUserAgentProcessSnapshot> sameSessionProcesses,
        UserAgentSessionContextSnapshot? sessionContext,
        DateTimeOffset nowUtc)
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
        DateTimeOffset nowUtc)
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

        return "unhealthy_existing_process";
    }
}
