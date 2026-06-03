namespace Porta.UserAgent.Interventions;

using System.IO;
using System.Text.Json;

internal static class AppearancePreferenceStore
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortaAgent");

    internal static readonly string FilePath = Path.Combine(DirectoryPath, "appearance-preferences.json");

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

    private sealed class AppearanceDocument
    {
        public bool IsLightTheme { get; set; }
    }
}
