using System;
using System.Runtime.InteropServices;

namespace Porta.Agent.Runtime.Input
{
    public sealed class MouseHook : IDisposable
    {
        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelMouseProc? _proc;

        public event Action<int>? ScrollCaptured;

        public void Install()
        {
            _proc = HookCallback;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);

            if (_hookId == IntPtr.Zero)
                throw new Exception("Failed to install WH_MOUSE_LL hook.");

            Console.WriteLine($"WH_MOUSE_LL installed. HookId: {_hookId}");
        }

        public void Uninstall()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int delta = (short)((hookStruct.mouseData >> 16) & 0xffff);
                Console.WriteLine($"Scroll delta: {delta}");
                ScrollCaptured?.Invoke(delta);
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose() => Uninstall();

        // ================= WIN32 =================

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEWHEEL = 0x020A;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
