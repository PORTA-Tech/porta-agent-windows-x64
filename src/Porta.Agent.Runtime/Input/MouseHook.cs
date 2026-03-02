using System;
using System.Threading;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Win32;

namespace Porta.Agent.Runtime.Input
{
    public class MouseHook : IDisposable
    {
        private IntPtr _hookId = IntPtr.Zero;
        private Thread? _hookThread;
        private uint _hookThreadId;
        private NativeMethods.LowLevelMouseProc? _hookProc;
        private readonly ManualResetEventSlim _installedSignal = new(false);
        private readonly object _gate = new();
        private volatile bool _installSucceeded;
        private int _lastInstallError;
        private bool _hasLastMovePoint;
        private int _lastMoveX;
        private int _lastMoveY;

        public Func<int, bool>? ShouldSuppressScroll { get; set; }
        public event Action<int, bool> ScrollDeltaChanged = delegate { };
        public event Action<int, int> MouseMoved = delegate { };

        public bool Install(int maxAttempts = 3, int retryDelayMs = 200)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("PORTA_FORCE_HOOK_INSTALL_FAIL"), "1", StringComparison.Ordinal))
            {
                Logger.Warn("Mouse hook install forced to fail by PORTA_FORCE_HOOK_INSTALL_FAIL=1.");
                return false;
            }

            lock (_gate)
            {
                if (_hookThread != null && _hookThread.IsAlive && _hookId != IntPtr.Zero)
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
                    Name = "MouseHookThread"
                };

                lock (_gate)
                {
                    _hookThread = thread;
                }

                thread.Start();

                bool signaled = _installedSignal.Wait(TimeSpan.FromSeconds(2));
                if (signaled && _installSucceeded)
                {
                    Logger.Event("mouse_hook_install_succeeded", ("attempt", attempt));
                    return true;
                }

                Logger.Warn(
                    $"Mouse hook install attempt {attempt} failed (signaled={signaled}, error=0x{_lastInstallError:X8}).");
                CleanupFailedInstallThread();

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(Math.Max(0, retryDelayMs));
                }
            }

            Logger.Error("Mouse hook installation failed after retries.");
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
                Logger.Warn($"Mouse hook uninstall PostThreadMessage failed: 0x{error:X8}.");
            }

            if (!thread.Join(2000))
            {
                Logger.Warn("Mouse hook thread did not stop within timeout.");
            }

            lock (_gate)
            {
                _hookThread = null;
                _hookThreadId = 0;
                _hookProc = null;
                _hookId = IntPtr.Zero;
            }
        }

        private void HookThreadMain()
        {
            try
            {
                _hookThreadId = NativeMethods.GetCurrentThreadId();
                _hookProc = HookCallback;
                _hookId = NativeMethods.SetWindowsHookEx(Constants.WhMouseLl, _hookProc, IntPtr.Zero, 0);

                if (_hookId == IntPtr.Zero)
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
                Logger.Error(ex, "Mouse hook thread crashed.");
            }
            finally
            {
                _installedSignal.Set();

                if (_hookId != IntPtr.Zero)
                {
                    if (!NativeMethods.UnhookWindowsHookEx(_hookId))
                    {
                        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        Logger.Warn($"UnhookWindowsHookEx failed: 0x{error:X8}.");
                    }

                    _hookId = IntPtr.Zero;
                }
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (uint)wParam == Constants.WmMouseWheel)
            {
                int extraInfoOffset = IntPtr.Size == 8 ? Constants.DwExtraInfoOffsetX64 : Constants.DwExtraInfoOffsetX86;
                uint extraInfo = unchecked((uint)(nuint)System.Runtime.InteropServices.Marshal.ReadIntPtr(lParam, extraInfoOffset));
                if (extraInfo == Constants.InjectedScrollSignature)
                {
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                int mouseData = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, Constants.MouseDataOffset);
                int delta = (short)(mouseData >> 16);
                bool suppress = false;

                var shouldSuppress = ShouldSuppressScroll;
                if (shouldSuppress != null)
                {
                    try
                    {
                        suppress = shouldSuppress(delta);
                    }
                    catch
                    {
                        suppress = false;
                    }
                }

                try
                {
                    ScrollDeltaChanged(delta, suppress);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Mouse hook callback listener failed.");
                }

                if (suppress)
                {
                    return (IntPtr)1;
                }
            }
            else if (nCode >= 0 && (uint)wParam == Constants.WmMouseMove)
            {
                int x = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 0);
                int y = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);
                int dx = 0;
                int dy = 0;

                lock (_gate)
                {
                    if (_hasLastMovePoint)
                    {
                        dx = x - _lastMoveX;
                        dy = y - _lastMoveY;
                    }

                    _lastMoveX = x;
                    _lastMoveY = y;
                    _hasLastMovePoint = true;
                }

                if (dx != 0 || dy != 0)
                {
                    try
                    {
                        MouseMoved(dx, dy);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Mouse move listener failed.");
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
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
                _hookId = IntPtr.Zero;
                _hasLastMovePoint = false;
                _lastMoveX = 0;
                _lastMoveY = 0;
            }
        }

        public void Dispose() => Uninstall();
    }
}
