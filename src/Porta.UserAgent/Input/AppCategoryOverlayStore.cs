using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Porta.Agent.Common.Analysis;

namespace Porta.UserAgent.Input;

/// <summary>
/// 2d: reads the server-resolved category overlay (coarse token → category name) that the service
/// writes to ProgramData after the control plane categorizes tokens the local curated rules could
/// not, and feeds it into <see cref="AppCategoryClassifier"/>. Consulted only to fill Unknowns, so
/// local curated classification always wins.
///
/// Hot-reloaded: the file is re-read only when its last-write time changes, so this is cheap to
/// call on every flush. Best-effort — any error leaves the previous overlay in place and unresolved
/// tokens simply stay Unknown until the next successful read.
///
/// PRIVACY: the overlay contains only coarse tokens and a category enum name — no titles or URLs.
/// </summary>
internal static class AppCategoryOverlayStore
{
    public const string OverlayFileName = "category-overlay.v1.json";

    private static readonly object Gate = new();
    private static long _lastWriteTicks = -1;

    /// <summary>Re-applies the overlay to the classifier if the file changed since the last read.</summary>
    public static void RefreshIfChanged()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string path = Path.Combine(root, "PortaAgent", OverlayFileName);
            if (!File.Exists(path))
            {
                return;
            }

            long writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
            lock (Gate)
            {
                if (writeTicks == _lastWriteTicks)
                {
                    return;
                }

                _lastWriteTicks = writeTicks;
            }

            // Share ReadWrite + Delete so this read never blocks the service's atomic replace of
            // the file (mirrors FocusAppLabelMapReader).
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                json = reader.ReadToEnd();
            }

            Dictionary<string, string>? raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            var map = new Dictionary<string, AppCategory>(StringComparer.Ordinal);
            if (raw != null)
            {
                foreach (KeyValuePair<string, string> entry in raw)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                    {
                        continue;
                    }

                    if (Enum.TryParse(entry.Value.Trim(), ignoreCase: true, out AppCategory category)
                        && Enum.IsDefined(typeof(AppCategory), category)
                        && category != AppCategory.Unknown)
                    {
                        map[entry.Key.Trim().ToLowerInvariant()] = category;
                    }
                }
            }

            AppCategoryClassifier.ApplyServerCategoryOverlay(map);
        }
        catch
        {
            // Best-effort only — a missing/partial overlay just means unresolved tokens stay
            // Unknown until the next successful read. Never disrupt capture/flush.
        }
    }
}
