using System;
using System.Runtime.InteropServices;

namespace Porta.Agent.Runtime.Input
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x; public int y; }

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
        private HookProc _hookProc;

        public event Action<int> ScrollDeltaChanged = delegate { };

        public MouseHook() => _hookProc = HookCallback;

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;
            _hookId = SetHook(_hookProc);
        }

        public void Uninstall()
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        private IntPtr SetHook(HookProc proc)
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                const int WM_MOUSEWHEEL = 0x020A;
                if ((uint)wParam == WM_MOUSEWHEEL)
                {
                    // Marshal lParam into MSLLHOOKSTRUCT
                    MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    // High-order word of mouseData is the scroll delta
                    int delta = (short)((hookStruct.mouseData >> 16) & 0xffff);
                    ScrollDeltaChanged?.Invoke(delta);
                }
            }

            return CallNextHookEx(_hookId!, nCode, wParam, lParam);
        }

        public void Dispose() => Uninstall();

        #region Native Methods
        private const int WH_MOUSE_LL = 14;
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        #endregion
    }
}
