using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.Common.UserAgent;

namespace Porta.Agent.Service;

internal interface ICurrentSessionUserAgentRuntime
{
    DateTimeOffset UtcNow { get; }

    bool HasUserAgentBinary { get; }

    bool TryGetInteractiveSessionId(out int sessionId);

    IReadOnlyList<RunningUserAgentProcessSnapshot> EnumerateRunningProcesses();

    UserAgentSessionContextSnapshot? ReadSessionContext();

    int TerminateProcessesInSession(int sessionId, ILogger logger);

    bool HasRunningProcessInSession(int sessionId);

    bool TryLaunchInSession(int sessionId, string launchReason, ILogger logger);
}

internal sealed class CurrentSessionUserAgentWatchdogCoordinator
{
    private static readonly TimeSpan EnsureThrottle = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly ICurrentSessionUserAgentRuntime _runtime;
    private readonly ILogger _logger;
    private DateTimeOffset _lastEnsureUtc = DateTimeOffset.MinValue;
    private string? _lastLoggedDecisionSignature;

    public CurrentSessionUserAgentWatchdogCoordinator(
        ICurrentSessionUserAgentRuntime runtime,
        ILogger logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public void EnsureHealthy(bool force = false)
    {
        lock (_gate)
        {
            DateTimeOffset nowUtc = _runtime.UtcNow;
            if (!force && (nowUtc - _lastEnsureUtc) < EnsureThrottle)
            {
                return;
            }

            _lastEnsureUtc = nowUtc;

            if (!_runtime.HasUserAgentBinary)
            {
                LogPassiveStateIfChanged(force, "missing_binary");
                return;
            }

            if (!_runtime.TryGetInteractiveSessionId(out int currentSessionId))
            {
                LogPassiveStateIfChanged(force, "no_interactive_session");
                return;
            }

            IReadOnlyList<RunningUserAgentProcessSnapshot> runningProcesses = _runtime.EnumerateRunningProcesses();
            UserAgentSessionContextSnapshot? sessionContext = _runtime.ReadSessionContext();
            CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
                currentSessionId,
                runningProcesses,
                sessionContext,
                nowUtc);
            LogDecisionIfChanged(force, currentSessionId, runningProcesses, sessionContext, decision, nowUtc);

            if (decision.Action == CurrentSessionUserAgentAction.KeepExisting
                || decision.Action == CurrentSessionUserAgentAction.WaitForHeartbeat)
            {
                return;
            }

            if (decision.Action == CurrentSessionUserAgentAction.RestartExisting)
            {
                int terminated = _runtime.TerminateProcessesInSession(currentSessionId, _logger);
                _logger.LogWarning(
                    "Restarting Porta.UserAgent for session {SessionId}. Reason={Reason} Terminated={Terminated}",
                    currentSessionId,
                    decision.Reason,
                    terminated);
            }
            else
            {
                _logger.LogInformation(
                    "Launching Porta.UserAgent for session {SessionId}. Reason={Reason}",
                    currentSessionId,
                    decision.Reason);
            }

            if (_runtime.HasRunningProcessInSession(currentSessionId))
            {
                _logger.LogInformation(
                    "Skipped launching Porta.UserAgent because a process is already running in session {SessionId}.",
                    currentSessionId);
                return;
            }

            if (!_runtime.TryLaunchInSession(currentSessionId, decision.Reason, _logger))
            {
                _logger.LogError(
                    "Failed to launch Porta.UserAgent into session {SessionId}. Reason={Reason}",
                    currentSessionId,
                    decision.Reason);
            }
        }
    }

    private void LogPassiveStateIfChanged(bool force, string state)
    {
        string signature = string.Join('|', force ? "force" : "steady", state);
        if (string.Equals(signature, _lastLoggedDecisionSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedDecisionSignature = signature;
        _logger.LogInformation(
            "Current-session user-agent watchdog idle. State={State} Force={Force}",
            state,
            force);
    }

    private void LogDecisionIfChanged(
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
        _logger.LogInformation(
            "Current-session user-agent watchdog decision. Action={Action} Reason={Reason} Force={Force} Session={SessionId} RunningProcesses={RunningProcesses} SessionContext={SessionContext}",
            decision.Action,
            decision.Reason,
            force,
            currentSessionId,
            runningProcesses.Count,
            contextState);
    }
}

internal sealed class CurrentSessionUserAgentWatchdog : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly CurrentSessionUserAgentWatchdogCoordinator _coordinator;

    public CurrentSessionUserAgentWatchdog(CurrentSessionUserAgentWatchdogCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _coordinator.EnsureHealthy(force: true);

        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _coordinator.EnsureHealthy();
        }
    }
}

internal sealed class WindowsCurrentSessionUserAgentRuntime : ICurrentSessionUserAgentRuntime
{
    private static readonly IUserAgentSessionContextStore SessionContextStore = new UserAgentSessionContextStore();
    private readonly string _userAgentPath;

    public WindowsCurrentSessionUserAgentRuntime()
    {
        _userAgentPath = Path.Combine(AppContext.BaseDirectory, "Porta.UserAgent.exe");
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public bool HasUserAgentBinary => File.Exists(_userAgentPath);

    public bool TryGetInteractiveSessionId(out int sessionId)
    {
        sessionId = -1;

        int activeConsoleSessionId = WTSGetActiveConsoleSessionId();
        int? bestActiveSession = null;

        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr sessionBuffer, out int count))
        {
            try
            {
                int itemSize = Marshal.SizeOf<WtsSessionInfo>();
                for (int index = 0; index < count; index++)
                {
                    IntPtr itemPtr = sessionBuffer + (index * itemSize);
                    WtsSessionInfo info = Marshal.PtrToStructure<WtsSessionInfo>(itemPtr);
                    if (info.SessionId <= 0 || info.State != WtsConnectStateClass.WTSActive || !HasNamedUser(info.SessionId))
                    {
                        continue;
                    }

                    if (info.SessionId == activeConsoleSessionId)
                    {
                        sessionId = info.SessionId;
                        return true;
                    }

                    bestActiveSession ??= info.SessionId;
                }
            }
            finally
            {
                WTSFreeMemory(sessionBuffer);
            }
        }

        if (bestActiveSession.HasValue)
        {
            sessionId = bestActiveSession.Value;
            return true;
        }

        if (activeConsoleSessionId > 0 && HasNamedUser(activeConsoleSessionId))
        {
            sessionId = activeConsoleSessionId;
            return true;
        }

        return false;
    }

    public IReadOnlyList<RunningUserAgentProcessSnapshot> EnumerateRunningProcesses()
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

    public UserAgentSessionContextSnapshot? ReadSessionContext()
    {
        return SessionContextStore.ReadCurrent();
    }

    public int TerminateProcessesInSession(int sessionId, ILogger logger)
    {
        int terminated = 0;
        foreach (Process process in Process.GetProcessesByName("Porta.UserAgent"))
        {
            try
            {
                if (process.HasExited || process.SessionId != sessionId)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                terminated++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to terminate Porta.UserAgent process {ProcessId} in session {SessionId}.", SafeProcessId(process), sessionId);
            }
            finally
            {
                process.Dispose();
            }
        }

        return terminated;
    }

    public bool HasRunningProcessInSession(int sessionId)
    {
        foreach (Process process in Process.GetProcessesByName("Porta.UserAgent"))
        {
            try
            {
                if (!process.HasExited && process.SessionId == sessionId)
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

    public bool TryLaunchInSession(int sessionId, string launchReason, ILogger logger)
    {
        if (!HasUserAgentBinary)
        {
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken) || userToken == IntPtr.Zero)
        {
            logger.LogError(
                "WTSQueryUserToken failed for session {SessionId}. Error={ErrorCode}",
                sessionId,
                Marshal.GetLastWin32Error());
            return false;
        }

        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            if (!CreateEnvironmentBlock(out environmentBlock, userToken, false))
            {
                logger.LogWarning(
                    "CreateEnvironmentBlock failed for session {SessionId}. Continuing without environment block. Error={ErrorCode}",
                    sessionId,
                    Marshal.GetLastWin32Error());
                environmentBlock = IntPtr.Zero;
            }

            string normalizedReason = NormalizeLaunchReason(launchReason);
            string commandLine = $"\"{_userAgentPath}\" --startup-source=service-watchdog --launch-reason={normalizedReason}";
            var startupInfo = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"winsta0\default"
            };

            bool created = CreateProcessAsUser(
                userToken,
                _userAgentPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateUnicodeEnvironment,
                environmentBlock,
                AppContext.BaseDirectory,
                ref startupInfo,
                out ProcessInformation processInfo);
            if (!created)
            {
                logger.LogError(
                    "CreateProcessAsUser failed for session {SessionId}. Error={ErrorCode}",
                    sessionId,
                    Marshal.GetLastWin32Error());
                return false;
            }

            processHandle = processInfo.hProcess;
            threadHandle = processInfo.hThread;
            logger.LogInformation(
                "Launched Porta.UserAgent into session {SessionId}. ProcessId={ProcessId} Reason={Reason}",
                sessionId,
                processInfo.dwProcessId,
                normalizedReason);
            return true;
        }
        finally
        {
            if (threadHandle != IntPtr.Zero)
            {
                CloseHandle(threadHandle);
            }

            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environmentBlock);
            }

            CloseHandle(userToken);
        }
    }

    private bool HasNamedUser(int sessionId)
    {
        return !string.IsNullOrWhiteSpace(QuerySessionInfo(sessionId, WtsInfoClass.WTSUserName));
    }

    private static string? QuerySessionInfo(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out IntPtr buffer, out int bytesReturned)
            || buffer == IntPtr.Zero
            || bytesReturned <= 1)
        {
            return null;
        }

        try
        {
            string? value = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
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

    private static string NormalizeLaunchReason(string launchReason)
    {
        if (string.IsNullOrWhiteSpace(launchReason))
        {
            return "service_watchdog";
        }

        Span<char> buffer = stackalloc char[launchReason.Length];
        int count = 0;
        foreach (char ch in launchReason.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                buffer[count++] = char.ToLowerInvariant(ch);
            }
            else if (count == 0 || buffer[count - 1] != '_')
            {
                buffer[count++] = '_';
            }
        }

        string normalized = count == 0 ? "service_watchdog" : new string(buffer[..count]).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "service_watchdog" : normalized;
    }

    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WtsConnectStateClass State;
    }

    private enum WtsConnectStateClass
    {
        WTSActive = 0,
        WTSConnected = 1,
        WTSConnectQuery = 2,
        WTSShadow = 3,
        WTSDisconnected = 4,
        WTSIdle = 5,
        WTSListen = 6,
        WTSReset = 7,
        WTSDown = 8,
        WTSInit = 9
    }

    private enum WtsInfoClass
    {
        WTSInitialProgram,
        WTSApplicationName,
        WTSWorkingDirectory,
        WTSOEMId,
        WTSSessionId,
        WTSUserName,
        WTSWinStationName,
        WTSDomainName
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr phToken);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int reserved,
        int version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("Wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        WtsInfoClass wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
