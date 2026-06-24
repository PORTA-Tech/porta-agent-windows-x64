using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Porta.Agent.Common.Analysis;

namespace Porta.UserAgent.Input;

/// <summary>
/// Persists the coarse focus-identity maps (hash → label and hash → category) that
/// AppCategoryClassifier builds during capture to shared files under ProgramData, so the
/// service/core — a different process — can turn stored focus hashes into human names and
/// REAL category time for the digest and console.
///
/// PRIVACY: coarse labels only (browser site token or exe base name) and a category enum.
/// The raw window title / URL is never captured here, so nothing identifying about a specific
/// page is written.
///
/// Best-effort: any failure is swallowed. Persisting these maps must never disrupt capture/flush.
/// </summary>
internal static class FocusAppLabelStore
{
    public const string LabelsFileName = "focus-app-labels.v1.json";
    public const string CategoriesFileName = "focus-app-categories.v1.json";

    /// <summary>Writes the current hash → label and hash → category snapshots. Safe to call every flush.</summary>
    public static void Persist()
    {
        WriteMap(LabelsFileName, AppCategoryClassifier.SnapshotHashLabels());
        WriteMap(CategoriesFileName, AppCategoryClassifier.SnapshotHashCategories());
    }

    private static void WriteMap<T>(string fileName, IReadOnlyDictionary<string, T> map)
    {
        try
        {
            if (map.Count == 0)
            {
                return;
            }

            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string path = Path.Combine(root, "PortaAgent", fileName);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(map);
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Best-effort only — never let map persistence interfere with capture/flush.
        }
    }
}
