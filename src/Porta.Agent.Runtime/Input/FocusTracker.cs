using System;
using System.Diagnostics;
using System.Threading;
using Porta.Agent.Runtime.Win32;

namespace Porta.Agent.Runtime.Input;

internal sealed class FocusTracker
{
    private readonly string? _targetProcessName;
    private long _lastResolvedTimestamp;
    private int _targetProcessId;

    public FocusTracker(string? targetProcessName)
    {
        _targetProcessName = NormalizeProcessName(targetProcessName);
        _lastResolvedTimestamp = 0;
        _targetProcessId = 0;
    }

    public bool IsEnabled => _targetProcessName != null;

    public string? TargetProcessName => _targetProcessName;

    public bool IsTargetInForeground()
    {
        if (!IsEnabled)
        {
            return true;
        }

        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcessId);
        if (foregroundProcessId == 0)
        {
            return false;
        }

        int targetProcessId = GetOrResolveTargetProcessId();
        return targetProcessId != 0 && foregroundProcessId == (uint)targetProcessId;
    }

    private int GetOrResolveTargetProcessId()
    {
        const double refreshSeconds = 1.0;

        long now = Stopwatch.GetTimestamp();
        int cachedProcessId = Volatile.Read(ref _targetProcessId);
        long lastResolved = Interlocked.Read(ref _lastResolvedTimestamp);
        bool isFresh = lastResolved != 0 &&
            ((now - lastResolved) / (double)Stopwatch.Frequency) < refreshSeconds;

        if (cachedProcessId != 0 && isFresh && IsProcessAlive(cachedProcessId))
        {
            return cachedProcessId;
        }

        int resolved = ResolveProcessIdByName(_targetProcessName!);
        Volatile.Write(ref _targetProcessId, resolved);
        Interlocked.Exchange(ref _lastResolvedTimestamp, now);
        return resolved;
    }

    private static int ResolveProcessIdByName(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length == 0 ? 0 : processes[0].Id;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalized = name.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.Length == 0 ? null : normalized;
    }
}
