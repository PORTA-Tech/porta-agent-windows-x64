namespace Porta.UserAgent.Interventions;

using System.IO;

internal sealed class UserDesktopWidgetVisibilityStore
{
    private readonly string _path;

    public UserDesktopWidgetVisibilityStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? BuildPath() : path;
    }

    public bool IsHidden()
    {
        try
        {
            return File.Exists(_path);
        }
        catch
        {
            return false;
        }
    }

    public void Hide()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, "hidden");
        }
        catch
        {
        }
    }

    public void Show()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
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
