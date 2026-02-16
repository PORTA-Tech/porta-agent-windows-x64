using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Porta.Agent.Runtime.Input;

public sealed class MouseHook : IDisposable
{
    private readonly Action<int> _onVerticalScroll;
    private readonly Action<int> _onHorizontalScroll;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;

    public MouseHook(Action<int> onVerticalScroll, Action<int>? onHorizontalScroll = null)
    {
        _onVerticalScroll = onVerticalScroll ?? throw new ArgumentNullException(nameof(onVerticalScroll));
        _onHorizontalScroll = onHorizontalScroll ?? (_ => { });
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        _hookId = NativeMethods.SetWindowsHookEx(
            WH_MOUSE_LL,
            _proc,
            IntPtr.Zero,
            0
        );

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Mouse hook installation failed.");
        }
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;

        if (!NativeMethods.UnhookWindowsHookEx(_hookId))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Mouse hook removal failed.");
        }

        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            if (message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int delta = unchecked((short)((hookStruct.mouseData >> 16) & 0xFFFF));

                if (message == WM_MOUSEWHEEL)
                    _onVerticalScroll(delta);
                else
                    _onHorizontalScroll(delta);
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Uninstall();
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public int mouseData;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    }
}
