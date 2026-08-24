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
            catch (Exception ex)
            {
                // Still fails toward NOT muted, per the class contract above — but it must not
                // vanish. This write is the ONLY thing stopping a check-in card firing twice in a
                // day, and the most likely reason it fails is a full disk (measured: 36 MB free on
                // 2026-08-11). A silent catch here turns "the card asked me twice" into an
                // unexplainable field report, which is exactly the kind of bug that costs a day.
                UserAgentFileLog.Info(
                    $"mute_write_failed class={Normalize(classKey)} err={ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Phase 5 — releases one class's mute before the 5:00 boundary would.
    ///
    /// The mute is written when a card is SHOWN, not when it is answered, which is what stops a
    /// card firing twice. That is right for every card that got an answer — and wrong for the one
    /// case where the user's answer was "you were wrong, ask me later". Without this, "not done
    /// yet" could not do what its label says.
    ///
    /// Deliberately narrow: it clears one named class and nothing else, and re-arming is still
    /// gated by the scheduler (a delay, and a limit of one). This is not a general un-mute and must
    /// not become one — a mute that anything may clear stops being a guarantee.
    /// </summary>
    public void AllowAgainToday(string classKey)
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
                if (map.Remove(Normalize(classKey)))
                {
                    Save(map);
                }
            }
            catch (Exception ex)
            {
                // Fails toward STILL MUTED, which is the quiet outcome: the card simply does not
                // come back today. Logged because a correction that silently bought nothing is
                // exactly the kind of thing that reads as "the button does nothing".
                UserAgentFileLog.Info(
                    $"mute_clear_failed class={Normalize(classKey)} err={ex.GetType().Name}");
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
