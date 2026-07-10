using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Reads the coarse focus-identity maps the user-agent persists to ProgramData
/// (hash → label and hash → category), so the service can turn stored focus hashes into human
/// names ("youtube", "code") and REAL per-category time for the digest. Best-effort: returns an
/// empty map on any error.
/// </summary>
internal static class FocusAppLabelMapReader
{
    public static IReadOnlyDictionary<string, string> ReadLabels()
        => ReadMap<string>("focus-app-labels.v1.json");

    public static IReadOnlyDictionary<string, int> ReadCategories()
        => ReadMap<int>("focus-app-categories.v1.json");

    private static IReadOnlyDictionary<string, T> ReadMap<T>(string fileName)
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string path = Path.Combine(root, "PortaAgent", fileName);
            if (!File.Exists(path))
            {
                return new Dictionary<string, T>(StringComparer.Ordinal);
            }

            // Share ReadWrite + Delete so this read never blocks the user-agent's atomic replace
            // of the file (File.ReadAllText's default FileShare.Read would make the writer's
            // File.Move throw — the root cause of the stale label map and orphaned .tmp files).
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var streamReader = new StreamReader(stream))
            {
                json = streamReader.ReadToEnd();
            }

            Dictionary<string, T>? map = JsonSerializer.Deserialize<Dictionary<string, T>>(json);
            return map ?? new Dictionary<string, T>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, T>(StringComparer.Ordinal);
        }
    }
}
