using System;
using System.Threading;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Win32;

namespace Porta.Agent.Runtime.Input
{
    public sealed class KeyboardHook : IDisposable
    {
        private IntPtr _hookId = IntPtr.Zero;
        private Thread? _hookThread;
        private uint _hookThreadId;
        private NativeMethods.LowLevelKeyboardProc? _hookProc;
        private readonly ManualResetEventSlim _installedSignal = new(false);
        private readonly object _gate = new();
        private volatile bool _installSucceeded;
        private int _lastInstallError;

        // Privacy contract: no key content is emitted; only keydown timing + backspace flag.
        public event Action<bool, bool, bool> KeyChanged = delegate { };

        public bool Install(int maxAttempts = 3, int retryDelayMs = 200)
        {
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
                    Name = "KeyboardHookThread"
                };

                lock (_gate)
                {
                    _hookThread = thread;
                }

                thread.Start();

                bool signaled = _installedSignal.Wait(TimeSpan.FromSeconds(2));
                if (signaled && _installSucceeded)
                {
                    Logger.Event("keyboard_hook_install_succeeded", ("attempt", attempt));
                    return true;
                }

                Logger.Warn($"Keyboard hook install attempt {attempt} failed (signaled={signaled}, error=0x{_lastInstallError:X8}).");
                CleanupFailedInstallThread();

                if (attempt < maxAttempts)
                {
                    Thread.Sleep(Math.Max(0, retryDelayMs));
                }
            }

            Logger.Error("Keyboard hook installation failed after retries.");
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
                Logger.Warn($"Keyboard hook uninstall PostThreadMessage failed: 0x{error:X8}.");
            }

            if (!thread.Join(2000))
            {
                Logger.Warn("Keyboard hook thread did not stop within timeout.");
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
                _hookId = NativeMethods.SetWindowsHookExKeyboard(Constants.WhKeyboardLl, _hookProc, IntPtr.Zero, 0);

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
                Logger.Error(ex, "Keyboard hook thread crashed.");
            }
            finally
            {
                _installedSignal.Set();

                if (_hookId != IntPtr.Zero)
                {
                    if (!NativeMethods.UnhookWindowsHookEx(_hookId))
                    {
                        int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        Logger.Warn($"Keyboard unhook failed: 0x{error:X8}.");
                    }

                    _hookId = IntPtr.Zero;
                }
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint messageId = unchecked((uint)wParam.ToInt64());
                bool isKeyDown = messageId == Constants.WmKeyDown || messageId == Constants.WmSysKeyDown;
                bool isKeyUp = messageId == Constants.WmKeyUp || messageId == Constants.WmSysKeyUp;

                if (isKeyDown || isKeyUp)
                {
                    int vkCode = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, Constants.KbdVkCodeOffset);
                    int flags = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, Constants.KbdFlagsOffset);
                    bool isInjected = (flags & Constants.LlkhfInjected) != 0;
                    bool isBackspace = vkCode == Constants.VkBack;

                    try
                    {
                        KeyChanged(isKeyDown, isBackspace, isInjected);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Keyboard hook callback listener failed.");
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
            }
        }

        public void Dispose() => Uninstall();
    }
}
