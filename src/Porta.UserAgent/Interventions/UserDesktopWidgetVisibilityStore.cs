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
///
/// <para>Phase 4.2 (2026-08-18) adds fixed-duration hides (1h, 2h). Same invariant, tighter:
/// a duration hide stores the moment it ends, so it is bounded by construction and cannot
/// outlive its own clock even across a restart. The "today" mode is unchanged, and a file
/// written by an older build has no <c>UntilUtc</c>, so it keeps reading as "today".</para>
/// </summary>
internal sealed class UserDesktopWidgetVisibilityStore
{
    // The day boundary. 5:00 local, not midnight: a hide at 23:30 should last through the
    // whole night, and late-night workers still count "tonight" as today.
    private const int DayBoundaryHour = 5;

    internal const string TodayMode = "today";
    internal const string DurationMode = "duration";

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

    public bool IsHidden(DateTimeOffset nowLocal) => HiddenUntil(nowLocal) is not null;

    /// <summary>
    /// The moment the widget comes back, or null when it is not hidden. This is the single read
    /// both visibility and the "Hidden until 9:15 PM" copy go through, so the time the user is
    /// told can never disagree with the time the widget actually returns. Permanently disabled
    /// reports <see cref="DateTimeOffset.MaxValue"/> — hidden, with no return to name.
    /// </summary>
    public DateTimeOffset? HiddenUntil(DateTimeOffset nowLocal)
    {
        try
        {
            if (File.Exists(_permanentPath))
            {
                return DateTimeOffset.MaxValue;
            }

            MigrateLegacyFlagIfPresent(nowLocal);

            if (!File.Exists(_path))
            {
                return null;
            }

            HiddenDocument? doc = ReadDocument();
            if (doc is null || !DateTimeOffset.TryParse(doc.HiddenAtUtc, out DateTimeOffset hiddenAt))
            {
                // Unreadable state must fail toward VISIBLE — a corrupt file must never
                // become a permanent hide.
                TryDelete(_path);
                return null;
            }

            DateTimeOffset hiddenAtLocal = hiddenAt.ToOffset(nowLocal.Offset);

            // A duration hide carries its own end. It is only honoured when that end parses AND
            // is not further out than the "today" boundary would have been — so a hand-edited or
            // corrupted UntilUtc cannot buy more silence than the widget's own worst case.
            DateTimeOffset boundary = NextBoundary(hiddenAtLocal);
            DateTimeOffset until = boundary;
            if (string.Equals(doc.Mode, DurationMode, StringComparison.Ordinal)
                && DateTimeOffset.TryParse(doc.UntilUtc, out DateTimeOffset parsedUntil))
            {
                DateTimeOffset untilLocal = parsedUntil.ToOffset(nowLocal.Offset);
                until = untilLocal < boundary ? untilLocal : boundary;
            }

            if (nowLocal >= until)
            {
                TryDelete(_path); // lazy cleanup — the store IS the reset, daily or timed
                return null;
            }

            return until;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The mode of the hide currently in force: "today", "duration", or "" when visible.</summary>
    public string ReadMode(DateTimeOffset nowLocal)
    {
        if (HiddenUntil(nowLocal) is null)
        {
            return string.Empty;
        }

        return ReadDocument()?.Mode is DurationMode ? DurationMode : TodayMode;
    }

    /// <summary>Hide for today: records the moment; visibility returns at the next 5:00 local.</summary>
    public void Hide() => Hide(DateTimeOffset.Now);

    public void Hide(DateTimeOffset nowLocal) => Write(nowLocal, TodayMode, until: null);

    /// <summary>
    /// Phase 4.2 — hide for a fixed span. Returns the moment the widget comes back, which is the
    /// span from now, or the "today" boundary if that arrives first: a 2-hour hide at 04:30 ends
    /// at 05:00 with the rest of the morning, never at 06:30.
    /// </summary>
    public DateTimeOffset HideFor(TimeSpan duration, DateTimeOffset nowLocal)
    {
        DateTimeOffset boundary = NextBoundary(nowLocal);
        DateTimeOffset requested = nowLocal + duration;
        DateTimeOffset until = requested < boundary ? requested : boundary;
        Write(nowLocal, DurationMode, until);
        return until;
    }

    private void Write(DateTimeOffset nowLocal, string mode, DateTimeOffset? until)
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
                Mode = mode,
                UntilUtc = until?.ToUniversalTime().ToString("O") ?? string.Empty
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

    private HiddenDocument? ReadDocument()
    {
        try
        {
            HiddenDocument? doc = JsonSerializer.Deserialize<HiddenDocument>(File.ReadAllText(_path));
            return doc is null || string.IsNullOrWhiteSpace(doc.HiddenAtUtc) ? null : doc;
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
        public string Mode { get; set; } = TodayMode;

        // Phase 4.2 — set only for a duration hide. Absent in files written by earlier builds,
        // which therefore keep reading as "today".
        public string UntilUtc { get; set; } = string.Empty;
    }
}
