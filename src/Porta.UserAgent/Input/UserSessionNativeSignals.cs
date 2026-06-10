using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Security;

namespace Porta.UserAgent.Input;

internal static class UserSessionNativeSignals
{
    private static readonly ConcurrentDictionary<uint, CacheEntry> ForegroundHashCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const uint DesktopSwitchDesktopAccess = 0x0100;

    public static string? TryGetForegroundAppHash()
    {
        // This method is called on every sampling tick and MUST be exception-safe:
        // HashIdentifier -> Secrets.GetInstallSecret() is fail-closed and throws when
        // the install secret can't be read in the user's context. An uncaught throw
        // here used to kill the entire capture loop while heartbeats kept flowing.
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return null;
            }

            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            if (ForegroundHashCache.TryGetValue(processId, out CacheEntry cached)
                && nowTicks <= cached.ExpiresAtUtcTicks)
            {
                return cached.AppHash;
            }

            string identifier = ResolveStableAppIdentifier(processId);
            AppCategory category = AppCategoryClassifier.ClassifyStableIdentifier(identifier);
            string hash = HashIdentifier(identifier);
            AppCategoryClassifier.RegisterHashCategory(hash, category);
            ForegroundHashCache[processId] = new CacheEntry(hash, DateTimeOffset.UtcNow.Add(CacheTtl).UtcTicks);
            return hash;
        }
        catch (Exception ex)
        {
            LogForegroundHashFailureOnce(ex);
            return null;
        }
    }

    private static int _foregroundHashFailureLogged;

    private static void LogForegroundHashFailureOnce(Exception ex)
    {
        if (System.Threading.Interlocked.Exchange(ref _foregroundHashFailureLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"foreground_app_hash_failed error={ex.GetType().Name}:{ex.Message}");
        }
    }

    public static double ReadIdleDurationSeconds()
    {
        LastInputInfo info = new()
        {
            CbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return 0d;
        }

        uint tickCount = unchecked((uint)Environment.TickCount64);
        uint elapsedMs = unchecked(tickCount - info.DwTime);
        return Math.Max(0d, elapsedMs / 1000d);
    }

    public static bool IsInterventionPresentationAvailable(out string errorCode)
    {
        errorCode = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            errorCode = "unsupported_os";
            return false;
        }

        if (!Environment.UserInteractive)
        {
            errorCode = "non_interactive_session";
            return false;
        }

        IntPtr desktop = NativeMethods.OpenInputDesktop(0, false, DesktopSwitchDesktopAccess);
        if (desktop == IntPtr.Zero)
        {
            errorCode = "user_session_locked";
            return false;
        }

        try
        {
            if (!NativeMethods.SwitchDesktop(desktop))
            {
                errorCode = "user_session_locked";
                return false;
            }

            return true;
        }
        finally
        {
            _ = NativeMethods.CloseDesktop(desktop);
        }
    }

    private static string ResolveStableAppIdentifier(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));

            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return $"exe:{path.Trim().ToLowerInvariant()}";
            }

            if (!string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return $"process:{process.ProcessName.Trim().ToLowerInvariant()}";
            }
        }
        catch
        {
        }

        return $"pid:{processId}";
    }

    private static string HashIdentifier(string stableIdentifier)
    {
        byte[] secret = Secrets.GetInstallSecret();
        byte[] payload = Encoding.UTF8.GetBytes(stableIdentifier);
        using var hmac = new HMACSHA256(secret);
        byte[] hash = hmac.ComputeHash(payload);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LastInputInfo plii);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SwitchDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(IntPtr hDesktop);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    private readonly record struct CacheEntry(string AppHash, long ExpiresAtUtcTicks);
}
