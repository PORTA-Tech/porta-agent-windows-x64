namespace Porta.ControlCenter;

using System.IO;
using System.Text.Json;

internal static class AppearancePreferenceStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortaAgent",
        "appearance-preferences.json");

    internal static bool LoadIsLightTheme()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return false;
            }

            string json = File.ReadAllText(FilePath);
            AppearanceDocument? doc = JsonSerializer.Deserialize<AppearanceDocument>(json);
            return doc?.IsLightTheme ?? false;
        }
        catch
        {
            return false;
        }
    }

    internal static void Save(bool isLightTheme)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(new AppearanceDocument { IsLightTheme = isLightTheme });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
        }
    }

    private sealed class AppearanceDocument
    {
        public bool IsLightTheme { get; set; }
    }
}
