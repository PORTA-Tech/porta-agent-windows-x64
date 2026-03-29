namespace Porta.UserAgent.Interventions;

using System.IO;
using System.Text.Json;

internal interface IUserInterventionPreferenceStore
{
    bool IsTipSuppressed(string tipKey);
    bool IsTipSnoozed(string tipKey, DateTimeOffset nowUtc);
    bool TrySuppressTip(string tipKey, DateTimeOffset updatedAtUtc);
    bool TrySnoozeTip(string tipKey, DateTimeOffset snoozeUntilUtc, DateTimeOffset updatedAtUtc);
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
                TipPreferenceEntry entry = document.GetOrCreateEntry(normalizedTipKey);
                entry.Suppressed = true;
                entry.UpdatedAtUtc = updatedAtUtc.ToUniversalTime().ToString("O");
                document.Tips[normalizedTipKey] = entry;

                return SaveUnsafe(document);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsTipSnoozed(string tipKey, DateTimeOffset nowUtc)
    {
        string normalizedTipKey = NormalizeTipKey(tipKey);
        if (string.IsNullOrWhiteSpace(normalizedTipKey))
        {
            return false;
        }

        lock (_gate)
        {
            PreferenceDocument document = LoadUnsafe();
            if (!document.Tips.TryGetValue(normalizedTipKey, out TipPreferenceEntry? entry))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(entry.SnoozeUntilUtc))
            {
                return false;
            }

            return DateTimeOffset.TryParse(entry.SnoozeUntilUtc, out DateTimeOffset snoozeUntilUtc)
                && snoozeUntilUtc > nowUtc.ToUniversalTime();
        }
    }

    public bool TrySnoozeTip(string tipKey, DateTimeOffset snoozeUntilUtc, DateTimeOffset updatedAtUtc)
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
                TipPreferenceEntry entry = document.GetOrCreateEntry(normalizedTipKey);
                entry.SnoozeUntilUtc = snoozeUntilUtc.ToUniversalTime().ToString("O");
                entry.UpdatedAtUtc = updatedAtUtc.ToUniversalTime().ToString("O");
                document.Tips[normalizedTipKey] = entry;

                return SaveUnsafe(document);
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

    private bool SaveUnsafe(PreferenceDocument document)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(document);
        File.WriteAllText(_filePath, json);
        return true;
    }

    private static string NormalizeTipKey(string? tipKey)
    {
        return string.IsNullOrWhiteSpace(tipKey)
            ? string.Empty
            : tipKey.Trim().ToLowerInvariant();
    }

    private sealed class PreferenceDocument
    {
        public int SchemaVer { get; set; } = 2;
        public Dictionary<string, TipPreferenceEntry> Tips { get; set; } = new(StringComparer.Ordinal);

        public TipPreferenceEntry GetOrCreateEntry(string tipKey)
        {
            if (Tips.TryGetValue(tipKey, out TipPreferenceEntry? entry))
            {
                return entry;
            }

            return new TipPreferenceEntry();
        }

        public static PreferenceDocument CreateDefault()
            => new();
    }

    private sealed class TipPreferenceEntry
    {
        public bool Suppressed { get; set; }
        public string UpdatedAtUtc { get; set; } = string.Empty;
        public string SnoozeUntilUtc { get; set; } = string.Empty;
    }
}
