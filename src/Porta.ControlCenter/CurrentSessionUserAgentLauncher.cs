using System.Diagnostics;
using System.IO;
using Porta.Agent.Common.UserAgent;

namespace Porta.ControlCenter;

internal static class CurrentSessionUserAgentLauncher
{
    private static readonly object Gate = new();
    private static readonly IUserAgentSessionContextStore SessionContextStore = new UserAgentSessionContextStore();
    private static readonly TimeSpan EnsureThrottle = TimeSpan.FromSeconds(5);
    private static DateTimeOffset _lastEnsureUtc = DateTimeOffset.MinValue;
    private static string? _lastLoggedDecisionSignature;

    public static void EnsureHealthy(bool force = false)
    {
        lock (Gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if (!force && (nowUtc - _lastEnsureUtc) < EnsureThrottle)
            {
                return;
            }

            _lastEnsureUtc = nowUtc;

            string userAgentPath = Path.Combine(AppContext.BaseDirectory, "Porta.UserAgent.exe");
            if (!File.Exists(userAgentPath))
            {
                ControlCenterFileLog.Info("user_agent_launch_skipped_missing_binary");
                return;
            }

            int currentSessionId = Process.GetCurrentProcess().SessionId;
            List<RunningUserAgentProcessSnapshot> runningProcesses = EnumerateRunningProcesses();
            UserAgentSessionContextSnapshot? sessionContext = SessionContextStore.ReadCurrent();
            string? expectedBinaryVersion = TryReadBinaryVersion(userAgentPath);
            CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
                currentSessionId,
                runningProcesses,
                sessionContext,
                nowUtc,
                expectedBinaryVersion);
            LogDecisionIfChanged(force, currentSessionId, runningProcesses, sessionContext, decision, nowUtc);

            if (decision.Action == CurrentSessionUserAgentAction.KeepExisting
                || decision.Action == CurrentSessionUserAgentAction.WaitForHeartbeat)
            {
                return;
            }

            if (decision.Action == CurrentSessionUserAgentAction.RestartExisting)
            {
                int terminated = TerminateSameSessionProcesses(currentSessionId);
                ControlCenterFileLog.Info($"user_agent_restart_requested reason={decision.Reason} terminated={terminated}");
            }
            else
            {
                ControlCenterFileLog.Info($"user_agent_launch_requested reason={decision.Reason}");
            }

            if (HasRunningProcessInSession(currentSessionId))
            {
                ControlCenterFileLog.Info("user_agent_launch_skipped_session_process_still_running");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = userAgentPath,
                    Arguments = $"--startup-source=control-center-recovery --launch-reason={decision.Reason}",
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ControlCenterFileLog.Error("user_agent_launch_failed", ex);
            }
        }
    }

    private static List<RunningUserAgentProcessSnapshot> EnumerateRunningProcesses()
    {
        var snapshots = new List<RunningUserAgentProcessSnapshot>();
        foreach (Process process in Process.GetProcessesByName("Porta.UserAgent"))
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                DateTimeOffset? startedAtUtc = null;
                try
                {
                    startedAtUtc = process.StartTime.ToUniversalTime();
                }
                catch
                {
                }

                snapshots.Add(new RunningUserAgentProcessSnapshot(
                    ProcessId: process.Id,
                    SessionId: process.SessionId,
                    StartedAtUtc: startedAtUtc));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return snapshots;
    }

    private static int TerminateSameSessionProcesses(int currentSessionId)
    {
        int terminated = 0;
        foreach (Process process in Process.GetProcessesByName("Porta.UserAgent"))
        {
            try
            {
                if (process.HasExited || process.SessionId != currentSessionId)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                terminated++;
            }
            catch (Exception ex)
            {
                ControlCenterFileLog.Error($"user_agent_terminate_failed pid={SafeProcessId(process)}", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        return terminated;
    }

    private static bool HasRunningProcessInSession(int currentSessionId)
    {
        foreach (Process process in Process.GetProcessesByName("Porta.UserAgent"))
        {
            try
            {
                if (!process.HasExited && process.SessionId == currentSessionId)
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private static string? TryReadBinaryVersion(string executablePath)
    {
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(version.FileVersion)
                ? null
                : version.FileVersion.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void LogDecisionIfChanged(
        bool force,
        int currentSessionId,
        IReadOnlyList<RunningUserAgentProcessSnapshot> runningProcesses,
        UserAgentSessionContextSnapshot? sessionContext,
        CurrentSessionUserAgentDecision decision,
        DateTimeOffset nowUtc)
    {
        string contextState = sessionContext is null
            ? "none"
            : $"pid={sessionContext.ProcessId},session={sessionContext.SessionId},context={sessionContext.CaptureContext},age_seconds={Math.Max(0d, (nowUtc - sessionContext.UpdatedAtUtc.ToUniversalTime()).TotalSeconds):F1}";
        string signature = string.Join(
            '|',
            force ? "force" : "steady",
            currentSessionId,
            runningProcesses.Count,
            decision.Action,
            decision.Reason,
            contextState);

        if (string.Equals(signature, _lastLoggedDecisionSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedDecisionSignature = signature;
        ControlCenterFileLog.Info(
            $"user_agent_health_decision action={decision.Action} reason={decision.Reason} force={force} current_session={currentSessionId} running_processes={runningProcesses.Count} session_context={contextState}");
    }
}
