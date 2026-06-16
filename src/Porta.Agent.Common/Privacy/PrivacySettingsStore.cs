using System.Text.Json;

namespace Porta.Agent.Common.Privacy;

/// <summary>
/// User-controllable privacy preferences. Currently a single toggle: whether the agent may
/// read browser window titles (in memory only) to classify browser content into work vs.
/// distraction categories. When OFF, browsers fall back to the neutral "Unknown" category —
/// exactly as if no title were read — so nothing about the page is observed at all.
///
/// Default is ON (opt-out): the agent already runs with the user's knowledge, only the derived
/// category is ever stored (never the title), and the toggle + disclosure give explicit control.
/// </summary>
public sealed record PrivacySettings(bool ReadBrowserTitles)
{
    public static PrivacySettings Default { get; } = new(ReadBrowserTitles: true);
}

/// <summary>
/// Reads/writes <see cref="PrivacySettings"/> to a small per-user JSON file
/// (<c>%LOCALAPPDATA%\PortaAgent\privacy.json</c>). Per-user so both the user-session capture
/// agent and the Control Center (both run as the signed-in user) can read and write it without
/// elevation. Fail-open to <see cref="PrivacySettings.Default"/> on any error.
/// </summary>
public static class PrivacySettingsStore
{
    private const string FileName = "privacy.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static PrivacySettings? _cached;
    private static long _cachedAtUtcTicks;

    /// <summary>Cheap, cached accessor for the hot capture path (refreshes at most every 30s).</summary>
    public static bool ReadBrowserTitlesEnabled() => LoadCached().ReadBrowserTitles;

    /// <summary>Cached load — safe to call frequently (e.g. once per sampling tick).</summary>
    public static PrivacySettings LoadCached()
    {
        long now = DateTimeOffset.UtcNow.UtcTicks;
        lock (Gate)
        {
            if (_cached is not null && (now - _cachedAtUtcTicks) < CacheTtl.Ticks)
            {
                return _cached;
            }

            _cached = LoadFromDisk();
            _cachedAtUtcTicks = now;
            return _cached;
        }
    }

    /// <summary>Uncached load straight from disk; returns <see cref="PrivacySettings.Default"/> if absent/invalid.</summary>
    public static PrivacySettings LoadFromDisk()
    {
        try
        {
            string path = GetPath();
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : PrivacySettings.Default;
        }
        catch
        {
            return PrivacySettings.Default;
        }
    }

    /// <summary>Persists settings and refreshes the cache. Best-effort (swallows IO errors).</summary>
    public static void Save(PrivacySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            string path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Serialize(settings));
            lock (Gate)
            {
                _cached = settings;
                _cachedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
            }
        }
        catch
        {
            // Best-effort: a failed write must never break capture or the UI.
        }
    }

    // ── Pure helpers (no IO) — unit-testable ────────────────────────────────
    public static PrivacySettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return PrivacySettings.Default;
        }

        try
        {
            Document? doc = JsonSerializer.Deserialize<Document>(json);
            return doc is null ? PrivacySettings.Default : new PrivacySettings(doc.ReadBrowserTitles);
        }
        catch
        {
            return PrivacySettings.Default;
        }
    }

    public static string Serialize(PrivacySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(
            new Document { ReadBrowserTitles = settings.ReadBrowserTitles },
            JsonOptions);
    }

    private static string GetPath()
    {
        // Fully qualified: "Environment" alone would bind to the sibling
        // Porta.Agent.Common.Environment namespace, not System.Environment.
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", FileName);
    }

    private sealed class Document
    {
        // Defaults to true so a missing/partial file means "enabled" (opt-out model).
        public bool ReadBrowserTitles { get; init; } = true;
    }
}
