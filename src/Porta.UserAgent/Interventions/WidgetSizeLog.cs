namespace Porta.UserAgent.Interventions;

using System.IO;

/// <summary>
/// v1.2.113 (2.3) — rate limiter for the widget-size diagnostic log. The original
/// LogSizeEvent was disabled because it flooded the shared agent log; this fixes the
/// flooding instead of the logging. Pure and unit-testable: at most
/// <see cref="MaxLinesPerWindow"/> lines per rolling second; excess lines are dropped
/// silently but COUNTED, and the count is reported on the next allowed line so the
/// evidence never lies about gaps.
/// </summary>
internal sealed class WidgetSizeLogRateLimiter
{
    public const int MaxLinesPerWindow = 5;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private DateTimeOffset _windowStartUtc = DateTimeOffset.MinValue;
    private int _linesInWindow;
    private long _droppedSinceLastAllowed;

    /// <summary>
    /// Returns whether this line may be written, and how many lines were dropped since
    /// the previous allowed one (0 when none — append "dropped=N" only when > 0).
    /// </summary>
    public bool TryAcquire(DateTimeOffset nowUtc, out long droppedSinceLastAllowed)
    {
        if (nowUtc - _windowStartUtc >= Window)
        {
            _windowStartUtc = nowUtc;
            _linesInWindow = 0;
        }

        if (_linesInWindow >= MaxLinesPerWindow)
        {
            _droppedSinceLastAllowed++;
            droppedSinceLastAllowed = 0;
            return false;
        }

        _linesInWindow++;
        droppedSinceLastAllowed = _droppedSinceLastAllowed;
        _droppedSinceLastAllowed = 0;
        return true;
    }
}

/// <summary>
/// v1.2.113 (2.3) — the dedicated widget-size log file writer. Append-only; when the
/// file exceeds the cap it is truncated to empty (the useful window is always the last
/// few minutes before a strip appears, never history). Best-effort: any IO failure is
/// swallowed — diagnostics must never hurt the widget.
/// </summary>
internal static class WidgetSizeLog
{
    public const long DefaultMaxBytes = 1_048_576; // 1 MB

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortaAgent",
        "widget-size.log");

    // TEST SEAM — redirects the default path so window-level tests can assert on the
    // evidence lines without touching the real LocalAppData file. Never set in product code.
    internal static string? PathOverride;

    public static void Append(string line, string? path = null, long maxBytes = DefaultMaxBytes)
    {
        try
        {
            string target = string.IsNullOrWhiteSpace(path) ? (PathOverride ?? DefaultPath) : path;
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var info = new FileInfo(target);
            if (info.Exists && info.Length > maxBytes)
            {
                File.WriteAllText(target, string.Empty);
            }

            File.AppendAllText(target, line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
