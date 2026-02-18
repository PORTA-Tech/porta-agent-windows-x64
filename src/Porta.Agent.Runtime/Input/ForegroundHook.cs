using System;
using System.Threading;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Win32;

namespace Porta.Agent.Runtime.Input;

internal sealed class ForegroundHook : IDisposable
{
    private IntPtr _hookHandle = IntPtr.Zero;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private NativeMethods.WinEventProc? _hookProc;
    private readonly ManualResetEventSlim _installedSignal = new(false);
    private readonly object _gate = new();
    private volatile bool _installSucceeded;
    private int _lastInstallError;

    public event Action<uint, IntPtr> ForegroundChanged = delegate { };

    public bool Install(int maxAttempts = 3, int retryDelayMs = 200)
    {
        lock (_gate)
        {
            if (_hookThread != null && _hookThread.IsAlive && _hookHandle != IntPtr.Zero)
            {
                return true;
            }
        }

        for (int attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            _installedSignal.Reset();
            _installSucceeded = false;
            _lastInstallError = 0;

            var thread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "ForegroundHookThread"
            };

            lock (_gate)
            {
                _hookThread = thread;
            }

            thread.Start();

            bool signaled = _installedSignal.Wait(TimeSpan.FromSeconds(2));
            if (signaled && _installSucceeded)
            {
                Logger.Event("foreground_hook_install_succeeded", ("attempt", attempt));
                return true;
            }

            Logger.Warn($"Foreground hook install attempt {attempt} failed (signaled={signaled}, error=0x{_lastInstallError:X8}).");
            CleanupFailedInstallThread();

            if (attempt < maxAttempts)
            {
                Thread.Sleep(Math.Max(0, retryDelayMs));
            }
        }

        Logger.Error("Foreground hook installation failed after retries.");
        return false;
    }

    public void Uninstall()
    {
        Thread? thread;
        uint threadId;

        lock (_gate)
        {
            thread = _hookThread;
            threadId = _hookThreadId;
        }

        if (thread == null)
        {
            return;
        }

        if (threadId != 0 && !NativeMethods.PostThreadMessage(threadId, Constants.WmQuit, UIntPtr.Zero, IntPtr.Zero))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Logger.Warn($"Foreground hook uninstall PostThreadMessage failed: 0x{error:X8}.");
        }

        if (!thread.Join(2000))
        {
            Logger.Warn("Foreground hook thread did not stop within timeout.");
        }

        lock (_gate)
        {
            _hookThread = null;
            _hookThreadId = 0;
            _hookProc = null;
            _hookHandle = IntPtr.Zero;
        }
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            _hookProc = HookCallback;
            _hookHandle = NativeMethods.SetWinEventHook(
                Constants.EventSystemForeground,
                Constants.EventSystemForeground,
                IntPtr.Zero,
                _hookProc,
                0,
                0,
                Constants.WineventOutofcontext | Constants.WineventSkipownprocess);

            if (_hookHandle == IntPtr.Zero)
            {
                _lastInstallError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _installSucceeded = false;
                return;
            }

            _installSucceeded = true;
            _installedSignal.Set();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _installSucceeded = false;
            Logger.Error(ex, "Foreground hook thread crashed.");
        }
        finally
        {
            _installedSignal.Set();

            if (_hookHandle != IntPtr.Zero)
            {
                if (!NativeMethods.UnhookWinEvent(_hookHandle))
                {
                    int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    Logger.Warn($"UnhookWinEvent failed: 0x{error:X8}.");
                }

                _hookHandle = IntPtr.Zero;
            }
        }
    }

    private void HookCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint eventTime)
    {
        if (eventType != Constants.EventSystemForeground || hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return;
        }

        try
        {
            ForegroundChanged(processId, hwnd);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Foreground hook callback listener failed.");
        }
    }

    private void CleanupFailedInstallThread()
    {
        Thread? thread;
        uint threadId;

        lock (_gate)
        {
            thread = _hookThread;
            threadId = _hookThreadId;
        }

        if (thread != null && threadId != 0)
        {
            NativeMethods.PostThreadMessage(threadId, Constants.WmQuit, UIntPtr.Zero, IntPtr.Zero);
            thread.Join(500);
        }

        lock (_gate)
        {
            _hookThread = null;
            _hookThreadId = 0;
            _hookProc = null;
            _hookHandle = IntPtr.Zero;
        }
    }

    public void Dispose() => Uninstall();
}
