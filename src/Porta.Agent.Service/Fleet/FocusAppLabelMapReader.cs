using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Reads the coarse hash → label map that the user-agent persists to ProgramData
/// (focus-app-labels.v1.json), so the service can turn stored focus hashes into human names
/// ("youtube", "code") for the digest. Best-effort: returns an empty map on any error.
/// </summary>
internal static class FocusAppLabelMapReader
{
    public static IReadOnlyDictionary<string, string> Read()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string path = Path.Combine(root, "PortaAgent", "focus-app-labels.v1.json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            string json = File.ReadAllText(path);
            Dictionary<string, string>? map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
