using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Porta.Agent.Runtime.Input;

public sealed class MouseHook : IDisposable
{
    private readonly Action<int> _onVerticalScroll;
    private readonly Action<int> _onHorizontalScroll;

    private readonly LowLevelMouseProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    private ScrollWindow? _window;

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_INPUT = 0x00FF;

    public MouseHook(Action<int> onVerticalScroll, Action<int>? onHorizontalScroll = null)
    {
        _onVerticalScroll = onVerticalScroll ?? throw new ArgumentNullException(nameof(onVerticalScroll));
        _onHorizontalScroll = onHorizontalScroll ?? (_ => { });
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        // Install low-level hook
        _hookId = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        if (_hookId == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        // Create hidden message window
        _window = new ScrollWindow(ProcessRawInput);
        _window.CreateHandle(new CreateParams());

        RegisterRawInput(_window.Handle);
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _window?.DestroyHandle();
        _window = null;
    }

    private void RegisterRawInput(IntPtr hwnd)
    {
        var rid = new NativeMethods.RAWINPUTDEVICE[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        if (!NativeMethods.RegisterRawInputDevices(
                rid,
                (uint)rid.Length,
                (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void ProcessRawInput(IntPtr lParam)
    {
        uint size = 0;
        NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT,
            IntPtr.Zero, ref size,
            (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>());

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT,
                    buffer, ref size,
                    (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>()) == size)
            {
                var raw = Marshal.PtrToStructure<NativeMethods.RAWINPUT>(buffer);

                if (raw.header.dwType == 0)
                {
                    int delta = (short)raw.mouse.usButtonData;

                    if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_WHEEL) != 0)
                        _onVerticalScroll(delta);

                    if ((raw.mouse.usButtonFlags & NativeMethods.RI_MOUSE_HWHEEL) != 0)
                        _onHorizontalScroll(delta);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int delta = unchecked((short)((hookStruct.mouseData >> 16) & 0xFFFF));

                if (msg == WM_MOUSEWHEEL)
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

    private sealed class ScrollWindow : NativeWindow
    {
        private readonly Action<IntPtr> _onInput;

        public ScrollWindow(Action<IntPtr> onInput)
        {
            _onInput = onInput;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
                _onInput(m.LParam);

            base.WndProc(ref m);
        }
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
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDEV_INPUTSINK = 0x00000100;

        public const ushort RI_MOUSE_WHEEL = 0x0400;
        public const ushort RI_MOUSE_HWHEEL = 0x0800;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWMOUSE
        {
            public ushort usFlags;
            public uint ulButtons;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWMOUSE mouse;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);
    }
}
