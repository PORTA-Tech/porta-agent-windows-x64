namespace Porta.Agent.Runtime.Win32;

internal static class Constants
{
    public const int WhMouseLl = 14;
    public const int MouseDataOffset = 8;
    public const uint WmMouseWheel = 0x020A;
    public const uint WmQuit = 0x0012;
    public const uint MouseEventfWheel = 0x0800;
    public const int DwExtraInfoOffsetX86 = 20;
    public const int DwExtraInfoOffsetX64 = 24;
    public const uint InjectedScrollSignature = 0x50545241; // "PTRA"
}
