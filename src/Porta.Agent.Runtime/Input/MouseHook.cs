using System;
using System.Threading;
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

        public event Action<int> ScrollDeltaChanged = delegate { };

        public void Install()
        {
            if (_hookThread != null)
                return;

            _installedSignal.Reset();
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "MouseHookThread"
            };
            _hookThread.Start();

            if (!_installedSignal.Wait(TimeSpan.FromSeconds(2)) || _hookId == IntPtr.Zero)
            {
                Console.WriteLine("[ERROR] Mouse hook installation timed out or failed.");
            }
        }

        public void Uninstall()
        {
            if (_hookThread == null)
                return;

            NativeMethods.PostThreadMessage(_hookThreadId, Constants.WmQuit, UIntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(2000);
            _hookThread = null;
            _hookThreadId = 0;
            _hookProc = null;
        }

        private void HookThreadMain()
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            _hookProc = HookCallback;
            _hookId = NativeMethods.SetWindowsHookEx(Constants.WhMouseLl, _hookProc, IntPtr.Zero, 0);

            if (_hookId == IntPtr.Zero)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Console.WriteLine($"[ERROR] SetWindowsHookEx failed: 0x{error:X8}");
                _installedSignal.Set();
                return;
            }

            _installedSignal.Set();

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (uint)wParam == Constants.WmMouseWheel)
            {
                int mouseData = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, Constants.MouseDataOffset);
                int delta = (short)(mouseData >> 16);
                ScrollDeltaChanged(delta);
            }

            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose() => Uninstall();
    }
}
