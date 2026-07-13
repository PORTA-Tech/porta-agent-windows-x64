namespace Porta.ControlCenter;

using System.IO;

/// <summary>
/// Workstream 3 — the ONLY permanent widget hide. The widget's own hide button hides for
/// today; this Settings toggle writes a flag file the user agent's visibility store treats
/// as "off until turned back on here". File presence = widget disabled.
/// </summary>
internal static class DesktopWidgetPreferenceStore
{
    private static readonly string FlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortaAgent",
        "user-agent.desktop-widget-disabled.v1.flag");

    internal static bool LoadIsWidgetEnabled()
    {
        try
        {
            return !File.Exists(FlagPath);
        }
        catch
        {
            return true;
        }
    }

    internal static void SaveWidgetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (File.Exists(FlagPath))
                {
                    File.Delete(FlagPath);
                }

                return;
            }

            string? dir = Path.GetDirectoryName(FlagPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FlagPath, "disabled");
        }
        catch
        {
        }
    }
}
