namespace Porta.UserAgent.Interventions;

using System.IO;
using System.Text.Json;

/// <summary>
/// Workstream 3 — widget visibility. The widget's own hide button means "hide for TODAY":
/// the store records when the hide happened and reports hidden only until the next 5:00
/// local after that moment. Crossing the boundary (or a session unlock after it) brings
/// the widget back — no user can put the widget into a permanently-invisible state from
/// the widget itself. Permanent hide exists only as the Control Center Settings toggle,
/// which writes a separate flag file this store also respects.
/// </summary>
internal sealed class UserDesktopWidgetVisibilityStore
{
    // The day boundary. 5:00 local, not midnight: a hide at 23:30 should last through the
    // whole night, and late-night workers still count "tonight" as today.
    private const int DayBoundaryHour = 5;

    private readonly string _path;
    private readonly string _legacyPath;
    private readonly string _permanentPath;

    public UserDesktopWidgetVisibilityStore(
        string? path = null, string? legacyPath = null, string? permanentPath = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? BuildPath() : path;
        _legacyPath = string.IsNullOrWhiteSpace(legacyPath) ? BuildLegacyPath() : legacyPath;
        _permanentPath = string.IsNullOrWhiteSpace(permanentPath) ? BuildPermanentPath() : permanentPath;
    }

    public bool IsHidden() => IsHidden(DateTimeOffset.Now);

    public bool IsHidden(DateTimeOffset nowLocal)
    {
        try
        {
            if (File.Exists(_permanentPath))
            {
                return true;
            }

            MigrateLegacyFlagIfPresent(nowLocal);

            if (!File.Exists(_path))
            {
                return false;
            }

            DateTimeOffset? hiddenAtUtc = ReadHiddenAtUtc();
            if (hiddenAtUtc is not { } hiddenAt)
            {
                // Unreadable state must fail toward VISIBLE — a corrupt file must never
                // become a permanent hide.
                TryDelete(_path);
                return false;
            }

            DateTimeOffset hiddenAtLocal = hiddenAt.ToOffset(nowLocal.Offset);
            if (nowLocal >= NextBoundary(hiddenAtLocal))
            {
                TryDelete(_path); // lazy cleanup — the store IS the daily reset
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Hide for today: records the moment; visibility returns at the next 5:00 local.</summary>
    public void Hide() => Hide(DateTimeOffset.Now);

    public void Hide(DateTimeOffset nowLocal)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(new HiddenDocument
            {
                HiddenAtUtc = nowLocal.ToUniversalTime().ToString("O"),
                Mode = "today"
            });
            File.WriteAllText(_path, json);
        }
        catch
        {
        }
    }

    public void Show()
    {
        TryDelete(_path);
        TryDelete(_legacyPath);
    }

    /// <summary>
    /// First 5:00 local strictly after <paramref name="hiddenAtLocal"/> — a hide at 02:00
    /// already ends at 05:00 the same morning; a hide at 14:00 ends at 05:00 tomorrow.
    /// </summary>
    private static DateTimeOffset NextBoundary(DateTimeOffset hiddenAtLocal)
    {
        DateTimeOffset boundary = new(
            hiddenAtLocal.Year, hiddenAtLocal.Month, hiddenAtLocal.Day,
            DayBoundaryHour, 0, 0, hiddenAtLocal.Offset);
        return hiddenAtLocal < boundary ? boundary : boundary.AddDays(1);
    }

    // The pre-Workstream-3 flag file meant "hidden forever". Treat an existing one as
    // "hidden since now" exactly once: every widget hidden under the old contract comes
    // back the morning after this build lands.
    private void MigrateLegacyFlagIfPresent(DateTimeOffset nowLocal)
    {
        if (!File.Exists(_legacyPath))
        {
            return;
        }

        if (!File.Exists(_path))
        {
            Hide(nowLocal);
        }

        TryDelete(_legacyPath);
    }

    private DateTimeOffset? ReadHiddenAtUtc()
    {
        try
        {
            HiddenDocument? doc = JsonSerializer.Deserialize<HiddenDocument>(File.ReadAllText(_path));
            if (doc is null || string.IsNullOrWhiteSpace(doc.HiddenAtUtc))
            {
                return null;
            }

            return DateTimeOffset.TryParse(doc.HiddenAtUtc, out DateTimeOffset parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
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
        return Path.Combine(root, "PortaAgent", "user-agent.desktop-widget-hidden.v2.json");
    }

    private static string BuildLegacyPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "user-agent.desktop-widget-hidden.v1.flag");
    }

    // Written/deleted by Control Center's "Show desktop widget" setting; never by the widget.
    internal static string BuildPermanentPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "user-agent.desktop-widget-disabled.v1.flag");
    }

    private sealed class HiddenDocument
    {
        public string HiddenAtUtc { get; set; } = string.Empty;
        public string Mode { get; set; } = "today";
    }
}
