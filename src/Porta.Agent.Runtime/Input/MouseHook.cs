using System;
using System.Runtime.InteropServices;

namespace Porta.Agent.Runtime.Input
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public class MouseHook : IDisposable
    {
        private IntPtr _hookId = IntPtr.Zero;
        private HookProc? _hookProc;

        public event Action<int> ScrollDeltaChanged = delegate { };

        public void Install()
        {
            if (_hookId != IntPtr.Zero)
                return;

            _hookProc = HookCallback;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, IntPtr.Zero, 0);
            if (_hookId == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Console.WriteLine($"[ERROR] SetWindowsHookEx failed: 0x{error:X8}");
                _hookProc = null;
            }
        }

        public void Uninstall()
        {
            if (_hookId == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _hookProc = null;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (uint)wParam == WM_MOUSEWHEEL)
            {
                MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                ScrollDeltaChanged(delta);
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose() => Uninstall();

        private const int WH_MOUSE_LL = 14;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            HookProc lpfn,
            IntPtr hMod,
            uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk,
            int nCode,
            IntPtr wParam,
            IntPtr lParam);
    }
}
