namespace Porta.UserAgent.Interventions;

using System.IO;
using System.Text.Json;

internal sealed class UserDesktopWidgetPlacementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public UserDesktopWidgetPlacementStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? BuildPath() : path;
    }

    public UserDesktopWidgetPlacement? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            PersistedPlacementEnvelope? envelope = JsonSerializer.Deserialize<PersistedPlacementEnvelope>(
                File.ReadAllText(_path),
                JsonOptions);
            if (envelope == null
                || double.IsNaN(envelope.Left)
                || double.IsInfinity(envelope.Left)
                || double.IsNaN(envelope.Top)
                || double.IsInfinity(envelope.Top))
            {
                return null;
            }

            return new UserDesktopWidgetPlacement(envelope.Left, envelope.Top);
        }
        catch
        {
            return null;
        }
    }

    public void Save(UserDesktopWidgetPlacement placement)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PersistedPlacementEnvelope envelope = new(placement.Left, placement.Top);
            File.WriteAllText(_path, JsonSerializer.Serialize(envelope, JsonOptions));
        }
        catch
        {
        }
    }

    public void Clear()
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
        return Path.Combine(root, "PortaAgent", "user-agent.desktop-widget-placement.v1.json");
    }

    private sealed record PersistedPlacementEnvelope(double Left, double Top);
}

internal sealed record UserDesktopWidgetPlacement(double Left, double Top);
