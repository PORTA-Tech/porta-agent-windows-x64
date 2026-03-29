namespace Porta.UserAgent.Interventions;

using System.IO;
using System.Text.Json;

internal interface IUserInterventionPreferenceStore
{
    bool IsTipSuppressed(string tipKey);
    bool TrySuppressTip(string tipKey, DateTimeOffset updatedAtUtc);
}

internal sealed class UserInterventionPreferenceStore : IUserInterventionPreferenceStore
{
    private readonly string _filePath;
    private readonly object _gate = new();

    public UserInterventionPreferenceStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PortaAgent",
                "widget-tip-preferences.json")
            : filePath;
    }

    public bool IsTipSuppressed(string tipKey)
    {
        string normalizedTipKey = NormalizeTipKey(tipKey);
        if (string.IsNullOrWhiteSpace(normalizedTipKey))
        {
            return false;
        }

        lock (_gate)
        {
            PreferenceDocument document = LoadUnsafe();
            return document.Tips.TryGetValue(normalizedTipKey, out TipPreferenceEntry? entry)
                && entry.Suppressed;
        }
    }

    public bool TrySuppressTip(string tipKey, DateTimeOffset updatedAtUtc)
    {
        string normalizedTipKey = NormalizeTipKey(tipKey);
        if (string.IsNullOrWhiteSpace(normalizedTipKey))
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                PreferenceDocument document = LoadUnsafe();
                document.Tips[normalizedTipKey] = new TipPreferenceEntry(
                    Suppressed: true,
                    UpdatedAtUtc: updatedAtUtc.ToUniversalTime().ToString("O"));

                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(document);
                File.WriteAllText(_filePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private PreferenceDocument LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return PreferenceDocument.CreateDefault();
            }

            string json = File.ReadAllText(_filePath);
            PreferenceDocument? document = JsonSerializer.Deserialize<PreferenceDocument>(json);
            return document ?? PreferenceDocument.CreateDefault();
        }
        catch
        {
            return PreferenceDocument.CreateDefault();
        }
    }

    private static string NormalizeTipKey(string? tipKey)
    {
        return string.IsNullOrWhiteSpace(tipKey)
            ? string.Empty
            : tipKey.Trim().ToLowerInvariant();
    }

    private sealed record PreferenceDocument(
        int SchemaVer,
        Dictionary<string, TipPreferenceEntry> Tips)
    {
        public static PreferenceDocument CreateDefault()
            => new(
                SchemaVer: 1,
                Tips: new Dictionary<string, TipPreferenceEntry>(StringComparer.Ordinal));
    }

    private sealed record TipPreferenceEntry(
        bool Suppressed,
        string UpdatedAtUtc);
}
