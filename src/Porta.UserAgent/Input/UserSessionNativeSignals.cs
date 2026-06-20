using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Common.Privacy;
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
            uint processId = 0;
            if (hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            }

            if (hwnd == IntPtr.Zero || processId == 0)
            {
                // FALLBACK (A2 — fix for "focus channel missing forever" on RDP/remote/relaunched
                // sessions): GetForegroundWindow returns 0 when THIS thread is attached to a
                // different desktop than the active input desktop. Keyboard/mouse still flow
                // (their low-level hooks are session-wide, not desktop-bound), but the focus
                // channel goes empty. Re-read the foreground from a short-lived dedicated thread
                // attached to the input desktop. This path ONLY runs when the direct read already
                // failed, so machines where GetForegroundWindow works (the healthy majority) never
                // execute any of it. Per-machine kill switch: set the environment variable
                // PORTA_DISABLE_FOREGROUND_DESKTOP_REATTACH=1 and restart the agent.
                uint probedProcessId = 0;
                IntPtr probedHwnd = IntPtr.Zero;
                bool recovered = !IsDesktopReattachDisabled()
                    && TryProbeForegroundOnInputDesktop(out probedProcessId, out probedHwnd)
                    && probedHwnd != IntPtr.Zero
                    && probedProcessId != 0;

                // Log the running probe outcome (throttled) so the console log tail shows exactly
                // which step is failing — open desktop / attach / read — without a buried one-time line.
                MaybeLogForegroundStatus();

                if (recovered)
                {
                    hwnd = probedHwnd;
                    processId = probedProcessId;
                }
                else
                {
                    RecordForegroundUnavailable(hwnd == IntPtr.Zero ? "no_foreground_window" : "zero_process_id");
                    return null;
                }
            }

            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            string exeIdentifier;

            if (ForegroundHashCache.TryGetValue(processId, out CacheEntry cached)
                && nowTicks <= cached.ExpiresAtUtcTicks)
            {
                // Non-browser apps have a single stable identity -> fully cached.
                if (!cached.IsBrowser)
                {
                    return cached.NonBrowserHash;
                }

                exeIdentifier = cached.ExeIdentifier;
            }
            else
            {
                exeIdentifier = ResolveStableAppIdentifier(processId);
                bool isBrowser = BrowserTitleClassifier.IsBrowserExe(ExtractExeName(exeIdentifier));
                long expiresAt = DateTimeOffset.UtcNow.Add(CacheTtl).UtcTicks;

                if (!isBrowser)
                {
                    AppCategory category = AppCategoryClassifier.ClassifyStableIdentifier(exeIdentifier);
                    string hash = HashIdentifier(exeIdentifier);
                    AppCategoryClassifier.RegisterHashCategory(hash, category);
                    ForegroundHashCache[processId] = new CacheEntry(exeIdentifier, false, hash, expiresAt);
                    return hash;
                }

                // Browser: cache the resolved identity, but recompute the per-site hash on every
                // tick below — one browser process shows many sites over time, so it cannot have a
                // single cached category.
                ForegroundHashCache[processId] = new CacheEntry(exeIdentifier, true, null, expiresAt);
            }

            // ── Browser content classification ───────────────────────────────────
            // Read the foreground window TITLE in memory only, derive a coarse site token +
            // category, and fold the site into the app identity so each site is tracked as its
            // own "app" (e.g. YouTube vs GitHub). The raw title is NEVER stored or returned —
            // only the hashed identity and the category enum leave this method.
            // Honor the user's privacy setting: when title reading is disabled, no title is
            // read at all and the browser stays in the neutral Unknown category.
            string? title = PrivacySettingsStore.ReadBrowserTitlesEnabled()
                ? TryReadForegroundTitle(hwnd)
                : null;
            (string siteToken, AppCategory siteCategory) = BrowserTitleClassifier.ClassifyTitle(title);
            string siteHash = HashIdentifier($"{exeIdentifier}|site:{siteToken}");
            AppCategoryClassifier.RegisterHashCategory(siteHash, siteCategory);
            return siteHash;
        }
        catch (Exception ex)
        {
            LogForegroundHashFailureOnce(ex);
            return null;
        }
    }

    private static int _foregroundHashFailureLogged;

    // Running count of ticks where the foreground window could not be resolved (silent-null
    // path). Read by the host to surface a fleet-visible signal; logged on a throttle so a
    // persistently-blind session is obvious in the log tail without flooding it.
    private static long _foregroundUnavailableCount;
    private const long ForegroundUnavailableLogEvery = 240; // ~20 min at 5s sampling.

    private static void RecordForegroundUnavailable(string reason)
    {
        long count = System.Threading.Interlocked.Increment(ref _foregroundUnavailableCount);
        if (count == 1 || count % ForegroundUnavailableLogEvery == 0)
        {
            Console.Error.WriteLine($"foreground_window_unavailable reason={reason} count={count}");
        }
    }

    /// <summary>
    /// Total number of sampling ticks since process start where the foreground window was
    /// unavailable (locked/secure desktop, disconnected session, or desktop mismatch). A
    /// large and growing value indicates the focus channel is being starved on this machine.
    /// </summary>
    public static long ReadForegroundUnavailableCount()
        => System.Threading.Interlocked.Read(ref _foregroundUnavailableCount);

    private static void LogForegroundHashFailureOnce(Exception ex)
    {
        if (System.Threading.Interlocked.Exchange(ref _foregroundHashFailureLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"foreground_app_hash_failed error={ex.GetType().Name}:{ex.Message}");
        }
    }

    // ── A2: input-desktop re-attach fallback ────────────────────────────────────────────
    // Only used when the direct GetForegroundWindow read fails. Reads the foreground from a
    // dedicated short-lived thread attached to the active input desktop, so the main capture
    // thread is never reassigned to a different desktop and nothing leaks across ticks.

    private const uint MaximumAllowed = 0x02000000;
    private static readonly TimeSpan ProbeJoinTimeout = TimeSpan.FromMilliseconds(300);
    private static int _reattachDisabled = -1; // -1 unknown, 0 enabled, 1 disabled

    // A2 probe outcome counters. Surfaced via the periodic `foreground_status` log line so we can
    // read — remotely, from the truncated log tail — exactly which step of the input-desktop probe
    // is failing on a given machine, without relying on a single one-time line that scrolls away.
    private static long _probeRecoveredCount;       // attached + read a real foreground (success)
    private static long _probeOpenDesktopFailCount; // OpenInputDesktop returned 0
    private static long _probeAttachFailCount;       // SetThreadDesktop returned false
    private static long _probeNoForegroundCount;     // attached, but GetForegroundWindow still 0
    private static long _probeTimeoutCount;          // probe thread did not finish within the join window
    private static long _foregroundStatusLastLogTicks;
    private static readonly long ForegroundStatusLogEveryTicks = TimeSpan.FromSeconds(15).Ticks;

    private static bool IsDesktopReattachDisabled()
    {
        int cached = Volatile.Read(ref _reattachDisabled);
        if (cached >= 0)
        {
            return cached == 1;
        }

        bool disabled = false;
        try
        {
            string? value = System.Environment.GetEnvironmentVariable("PORTA_DISABLE_FOREGROUND_DESKTOP_REATTACH");
            disabled = !string.IsNullOrWhiteSpace(value)
                && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            disabled = false;
        }

        Volatile.Write(ref _reattachDisabled, disabled ? 1 : 0);
        return disabled;
    }

    private static bool TryProbeForegroundOnInputDesktop(out uint processId, out IntPtr hwnd)
    {
        processId = 0;
        hwnd = IntPtr.Zero;

        uint capturedProcessId = 0;
        IntPtr capturedHwnd = IntPtr.Zero;

        var probe = new Thread(() =>
        {
            // OpenInputDesktop returns a handle to the desktop currently receiving user input.
            IntPtr desktop = NativeMethods.OpenInputDesktop(0, false, MaximumAllowed);
            if (desktop == IntPtr.Zero)
            {
                System.Threading.Interlocked.Increment(ref _probeOpenDesktopFailCount);
                return;
            }

            bool attached = false;
            try
            {
                // Attach THIS dedicated thread (not the capture loop thread) to the input desktop.
                // SetThreadDesktop succeeds because this fresh thread owns no windows or hooks.
                attached = NativeMethods.SetThreadDesktop(desktop);
                if (!attached)
                {
                    System.Threading.Interlocked.Increment(ref _probeAttachFailCount);
                }
                else
                {
                    IntPtr foreground = NativeMethods.GetForegroundWindow();
                    if (foreground != IntPtr.Zero)
                    {
                        _ = NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
                        capturedHwnd = foreground;
                        capturedProcessId = pid;
                        System.Threading.Interlocked.Increment(ref _probeRecoveredCount);
                    }
                    else
                    {
                        System.Threading.Interlocked.Increment(ref _probeNoForegroundCount);
                    }
                }
            }
            catch
            {
                // Never let the probe throw.
            }
            finally
            {
                // If we failed to attach, the desktop is not bound to this thread, so close it to
                // avoid a handle leak. If we DID attach, the system closes it when this thread
                // exits (closing an attached desktop here would just fail), so leave it.
                if (!attached)
                {
                    _ = NativeMethods.CloseDesktop(desktop);
                }
            }
        })
        {
            IsBackground = true,
            Name = "porta-fg-desktop-probe"
        };

        bool completed;
        try
        {
            probe.Start();
            completed = probe.Join(ProbeJoinTimeout);
        }
        catch
        {
            return false;
        }

        // Only read the captured values once the probe thread has fully finished — Join provides
        // the memory barrier. If it timed out, abandon it (it will exit on its own) and skip.
        if (!completed)
        {
            System.Threading.Interlocked.Increment(ref _probeTimeoutCount);
            return false;
        }

        processId = capturedProcessId;
        hwnd = capturedHwnd;
        return capturedHwnd != IntPtr.Zero;
    }

    // Emitted (throttled) whenever the foreground fallback runs, so the truncated log tail in the
    // console always carries a recent line showing exactly how the input-desktop probe is doing:
    //   recovered = times it attached and read a real foreground (the win)
    //   openFail  = OpenInputDesktop returned 0 (agent not on the interactive window station)
    //   attachFail= SetThreadDesktop failed
    //   noFg      = attached fine but the input desktop still had no foreground window
    //   timeout   = probe thread didn't finish in time
    private static void MaybeLogForegroundStatus()
    {
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        long last = System.Threading.Interlocked.Read(ref _foregroundStatusLastLogTicks);
        if (nowTicks - last < ForegroundStatusLogEveryTicks)
        {
            return;
        }

        if (System.Threading.Interlocked.CompareExchange(ref _foregroundStatusLastLogTicks, nowTicks, last) != last)
        {
            return;
        }

        Console.Error.WriteLine(
            "foreground_status"
            + " unavailable=" + System.Threading.Interlocked.Read(ref _foregroundUnavailableCount)
            + " recovered=" + System.Threading.Interlocked.Read(ref _probeRecoveredCount)
            + " openFail=" + System.Threading.Interlocked.Read(ref _probeOpenDesktopFailCount)
            + " attachFail=" + System.Threading.Interlocked.Read(ref _probeAttachFailCount)
            + " noFg=" + System.Threading.Interlocked.Read(ref _probeNoForegroundCount)
            + " timeout=" + System.Threading.Interlocked.Read(ref _probeTimeoutCount)
            + " reattachDisabled=" + (IsDesktopReattachDisabled() ? 1 : 0));
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

    /// <summary>
    /// Reads the foreground window title into memory for in-process classification only.
    /// The returned string is handed to <see cref="BrowserTitleClassifier"/> and discarded;
    /// it is never persisted, logged, or transmitted.
    /// </summary>
    private static string? TryReadForegroundTitle(IntPtr hwnd)
    {
        try
        {
            int length = NativeMethods.GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return null;
            }

            int capacity = Math.Min(length, 512) + 1;
            var buffer = new StringBuilder(capacity);
            int copied = NativeMethods.GetWindowText(hwnd, buffer, capacity);
            return copied > 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the lowercase exe base name (no extension) from a stable identifier.</summary>
    private static string ExtractExeName(string identifier)
    {
        string value;
        if (identifier.StartsWith("exe:", StringComparison.Ordinal))
        {
            value = identifier["exe:".Length..];
        }
        else if (identifier.StartsWith("process:", StringComparison.Ordinal))
        {
            value = identifier["process:".Length..];
        }
        else
        {
            return string.Empty;
        }

        int slash = value.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

        int dot = value.LastIndexOf('.');
        if (dot > 0)
        {
            value = value[..dot];
        }

        return value;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LastInputInfo plii);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadDesktop(IntPtr hDesktop);

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

    private readonly record struct CacheEntry(
        string ExeIdentifier,
        bool IsBrowser,
        string? NonBrowserHash,
        long ExpiresAtUtcTicks);
}
