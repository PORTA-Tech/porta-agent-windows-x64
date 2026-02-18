using System;
using System.Runtime.InteropServices;

namespace Porta.Agent.Runtime.Win32;

internal static class NativeMethods
{
    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMessage(out Message lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage([In] ref Message lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage([In] ref Message lpMsg);

    [DllImport("user32.dll")]
    internal static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
}
