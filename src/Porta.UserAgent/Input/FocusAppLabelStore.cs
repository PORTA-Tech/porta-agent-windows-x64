using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Porta.Agent.Common.Analysis;

namespace Porta.UserAgent.Input;

/// <summary>
/// Persists the coarse focus-identity hash → label map (maintained by AppCategoryClassifier during
/// capture) to a shared file under ProgramData, so the service/core — a different process — can join
/// a stored focus hash back to a human-readable name ("youtube", "code") for the digest and console.
///
/// PRIVACY: coarse labels only (browser site token or exe base name). The raw window title / URL is
/// never captured here, so nothing identifying about a specific page is written.
///
/// Best-effort: any failure is swallowed. Persisting labels must never disrupt capture or flush.
/// </summary>
internal static class FocusAppLabelStore
{
    private static string ResolveFilePath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "focus-app-labels.v1.json");
    }

    /// <summary>Writes the current hash → label snapshot atomically. Safe to call every flush.</summary>
    public static void Persist()
    {
        try
        {
            IReadOnlyDictionary<string, string> labels = AppCategoryClassifier.SnapshotHashLabels();
            if (labels.Count == 0)
            {
                return;
            }

            string path = ResolveFilePath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(labels);
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Best-effort only — never let label persistence interfere with capture/flush.
        }
    }
}
