using System;
using System.Runtime.InteropServices;

namespace Porta.Agent.Runtime.Win32;

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Message
{
    public IntPtr Hwnd;
    public uint MessageId;
    public UIntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public Point Pt;
    public uint LPrivate;
}
