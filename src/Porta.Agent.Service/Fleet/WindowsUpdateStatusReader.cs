using System;
using System.IO;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Reads the auto-update outcome file (written by the install script) so machine
/// summaries can carry update progress/results to the operations console.
/// </summary>
internal static class WindowsUpdateStatusReader
{
    private const int MaxChars = 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly object Gate = new();
    private static string? _cached;
    private static DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

    public static string? ReadStatus()
    {
        lock (Gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if ((nowUtc - _cachedAtUtc) < CacheTtl)
            {
                return _cached;
            }

            _cachedAtUtc = nowUtc;
            _cached = TryReadUnlocked();
            return _cached;
        }
    }

    private static string? TryReadUnlocked()
    {
        try
        {
            string path = AgentUpdateWorker.GetStatusPath();
            if (!File.Exists(path))
            {
                return null;
            }

            string raw = File.ReadAllText(path).Trim();
            if (raw.Length == 0)
            {
                return null;
            }

            return raw.Length <= MaxChars ? raw : raw[..MaxChars];
        }
        catch
        {
            return null;
        }
    }
}
