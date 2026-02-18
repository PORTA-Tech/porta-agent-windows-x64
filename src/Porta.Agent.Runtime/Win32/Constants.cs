namespace Porta.Agent.Runtime.Win32;

internal static class Constants
{
    public const int WhMouseLl = 14;
    public const int WhKeyboardLl = 13;
    public const int MouseDataOffset = 8;
    public const uint WmMouseWheel = 0x020A;
    public const uint WmKeyDown = 0x0100;
    public const uint WmKeyUp = 0x0101;
    public const uint WmSysKeyDown = 0x0104;
    public const uint WmSysKeyUp = 0x0105;
    public const uint WmQuit = 0x0012;
    public const uint MouseEventfWheel = 0x0800;
    public const uint EventSystemForeground = 0x0003;
    public const uint WineventOutofcontext = 0x0000;
    public const uint WineventSkipownprocess = 0x0002;
    public const int KbdVkCodeOffset = 0;
    public const int KbdFlagsOffset = 8;
    public const int LlkhfInjected = 0x10;
    public const int DwExtraInfoOffsetX86 = 20;
    public const int DwExtraInfoOffsetX64 = 24;
    public const uint InjectedScrollSignature = 0x50545241; // "PTRA"
}
