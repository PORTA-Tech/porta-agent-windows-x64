using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// 2d: writes the server-resolved category overlay (coarse token → category name) to ProgramData,
/// where the user-agent reads it (<c>AppCategoryOverlayStore</c>) to fill categories the local
/// curated rules left Unknown. Merges with any existing overlay so previously-resolved tokens are
/// retained across syncs and agent restarts.
///
/// Best-effort and atomic (temp + replace, retried), mirroring FocusAppLabelStore. PRIVACY: coarse
/// tokens and a category enum name only — no titles or URLs.
/// </summary>
internal static class AppCategoryOverlayWriter
{
    public const string OverlayFileName = "category-overlay.v1.json";

    private static string OverlayPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", OverlayFileName);
    }

    /// <summary>Merges the given token → category-name pairs into the overlay file (new values win).</summary>
    public static void MergeAndWrite(IReadOnlyDictionary<string, string> resolved)
    {
        if (resolved == null || resolved.Count == 0)
        {
            return;
        }

        string tempPath = string.Empty;
        try
        {
            string path = OverlayPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var merged = ReadExisting(path);
            foreach (KeyValuePair<string, string> entry in resolved)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                merged[entry.Key.Trim().ToLowerInvariant()] = entry.Value.Trim();
            }

            string json = JsonSerializer.Serialize(merged);
            tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, json);

            if (!TryAtomicReplace(tempPath, path))
            {
                File.WriteAllText(path, json);
            }
        }
        catch
        {
            // Best-effort only — a failed overlay write just means unresolved tokens stay Unknown
            // until the next sync. Never disrupt the reporter loop.
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static Dictionary<string, string> ReadExisting(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            Dictionary<string, string>? existing = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return existing ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool TryAtomicReplace(string tempPath, string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return true;
            }
            catch when (attempt < 4)
            {
                System.Threading.Thread.Sleep(15);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
