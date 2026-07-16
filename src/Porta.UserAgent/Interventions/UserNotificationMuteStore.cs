namespace Porta.UserAgent.Interventions;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Workstream B2 — per-class "muted for today" store. A class muted here stays muted until the
/// next 5:00 local (the SAME day boundary as the widget-hide store, so late-night work still
/// counts as "today"), then auto-clears on the next read. Backs the break "Skip today" button
/// and any class-level daily mute (e.g. the recovery nudge). Persisted so a skip survives a
/// restart within the day. Every failure path fails toward NOT muted — a mute must never become
/// a permanent silence.
/// </summary>
internal sealed class UserNotificationMuteStore
{
    // 5:00 local, matching UserDesktopWidgetVisibilityStore — a skip at 23:30 lasts the night.
    private const int DayBoundaryHour = 5;

    private readonly object _gate = new();
    private readonly string _path;

    public UserNotificationMuteStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? BuildPath() : path;
    }

    public void MuteForToday(string classKey) => MuteForToday(classKey, DateTimeOffset.Now);

    public void MuteForToday(string classKey, DateTimeOffset nowLocal)
    {
        if (string.IsNullOrWhiteSpace(classKey))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                Dictionary<string, string> map = Load();
                map[Normalize(classKey)] = nowLocal.ToUniversalTime().ToString("O");
                Save(map);
            }
            catch
            {
            }
        }
    }

    public bool IsMutedForToday(string classKey) => IsMutedForToday(classKey, DateTimeOffset.Now);

    public bool IsMutedForToday(string classKey, DateTimeOffset nowLocal)
    {
        if (string.IsNullOrWhiteSpace(classKey))
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                Dictionary<string, string> map = Load();
                if (!map.TryGetValue(Normalize(classKey), out string? mutedAtRaw)
                    || !DateTimeOffset.TryParse(mutedAtRaw, out DateTimeOffset mutedAtUtc))
                {
                    return false;
                }

                DateTimeOffset mutedAtLocal = mutedAtUtc.ToOffset(nowLocal.Offset);
                if (nowLocal >= NextBoundary(mutedAtLocal))
                {
                    map.Remove(Normalize(classKey)); // lazy daily reset — the store IS the reset
                    Save(map);
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string Normalize(string classKey) => classKey.Trim().ToLowerInvariant();

    // First 5:00 local strictly after the mute moment (same rule as the widget-hide store).
    private static DateTimeOffset NextBoundary(DateTimeOffset mutedAtLocal)
    {
        DateTimeOffset boundary = new(
            mutedAtLocal.Year, mutedAtLocal.Month, mutedAtLocal.Day,
            DayBoundaryHour, 0, 0, mutedAtLocal.Offset);
        return mutedAtLocal < boundary ? boundary : boundary.AddDays(1);
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string>? map =
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
        return map ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> map)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (map.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return;
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(map));
    }

    private static string BuildPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "user-agent.notification-mute.v1.json");
    }
}
