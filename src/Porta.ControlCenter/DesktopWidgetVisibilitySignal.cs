namespace Porta.ControlCenter;

using System.IO;

internal static class DesktopWidgetVisibilitySignal
{
    public static void ClearHidden()
    {
        try
        {
            string path = BuildPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string BuildPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "user-agent.desktop-widget-hidden.v1.flag");
    }
}
