namespace Porta.UserAgent.Interventions;

using System.Runtime.InteropServices;

/// <summary>
/// Fleet §1 (USER-handle exhaustion, PEACH20/AYMENE): reads this process's live USER and GDI
/// object counts so the leak — and the fix — are provable from the log. The per-process USER
/// ceiling is ~10,000; capture died on both machines when the widget recreate loop reached it.
/// Read-only and cheap (two win32 calls); safe to log periodically and at every failure site.
/// </summary>
internal static class GuiResourceMeter
{
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Current (user, gdi) handle counts for this process; (0,0) if the call fails.</summary>
    public static (uint UserObjects, uint GdiObjects) Read()
    {
        try
        {
            IntPtr process = GetCurrentProcess();
            return (GetGuiResources(process, GrUserObjects), GetGuiResources(process, GrGdiObjects));
        }
        catch
        {
            return (0u, 0u);
        }
    }

    /// <summary>"user_handles=N gdi_handles=M" — the log fragment shared by every §1 log site.</summary>
    public static string Describe()
    {
        (uint user, uint gdi) = Read();
        return $"user_handles={user} gdi_handles={gdi}";
    }
}
