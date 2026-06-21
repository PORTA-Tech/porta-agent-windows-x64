using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Reads the tail of the user-agent log so machine summaries can carry it to the
/// control plane. This lets operators diagnose capture failures from the operations
/// console without ever asking a user to locate or send a file.
/// </summary>
internal static class UserAgentLogTailReader
{
    // Scan a large window of the file so that, even when high-frequency widget-surface lines
    // dominate it, enough real signal lines remain after filtering to fill the returned tail.
    private const int MaxScanBytes = 512 * 1024;
    private const int MaxTailChars = 6000;

    // High-frequency, low-value widget-surface lifecycle lines. On machines where the widget
    // can't stay healthy these repeat several times a second and bury the capture diagnostics
    // (foreground_status, host_run_begin, capture failures) that operators actually need. They
    // are dropped from the transmitted tail only — the on-disk log is untouched.
    private static readonly string[] NoiseMarkers =
    {
        "widget_size",
        "desktop_widget_thread_starting",
        "desktop_widget_window_initializing",
        "desktop_widget_window_shown",
    };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly object Gate = new();
    private static string? _cachedTail;
    private static DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    public static string? ReadTail()
    {
        lock (Gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if ((nowUtc - _cachedAtUtc) < CacheTtl)
            {
                return _cachedTail;
            }

            _cachedAtUtc = nowUtc;
            _cachedTail = TryReadTailUnlocked();
            return _cachedTail;
        }
    }

    private static string? TryReadTailUnlocked()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string path = Path.Combine(root, "PortaAgent", "user-agent.log");
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            long start = Math.Max(0, stream.Length - MaxScanBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string raw = reader.ReadToEnd();

            // Drop the (possibly partial) first line when we started mid-file.
            if (start > 0)
            {
                int firstNewline = raw.IndexOf('\n');
                if (firstNewline >= 0 && firstNewline + 1 < raw.Length)
                {
                    raw = raw[(firstNewline + 1)..];
                }
            }

            // Keep only signal lines: drop the high-frequency widget-surface noise so the
            // limited tail we transmit carries the diagnostics operators need.
            var kept = new List<string>();
            foreach (string line in raw.Split('\n'))
            {
                if (IsNoise(line))
                {
                    continue;
                }

                kept.Add(line.TrimEnd('\r'));
            }

            string filtered = string.Join("\n", kept).Trim();
            if (filtered.Length == 0)
            {
                return null;
            }

            return filtered.Length <= MaxTailChars ? filtered : filtered[^MaxTailChars..];
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNoise(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        foreach (string marker in NoiseMarkers)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
