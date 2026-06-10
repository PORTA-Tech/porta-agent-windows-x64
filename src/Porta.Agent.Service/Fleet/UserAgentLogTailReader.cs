using System;
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
    private const int MaxTailBytes = 6 * 1024;
    private const int MaxTailChars = 4000;
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

            long start = Math.Max(0, stream.Length - MaxTailBytes);
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

            raw = raw.Trim();
            if (raw.Length == 0)
            {
                return null;
            }

            return raw.Length <= MaxTailChars ? raw : raw[^MaxTailChars..];
        }
        catch
        {
            return null;
        }
    }
}
