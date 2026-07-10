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
        if (map.Count == 0)
        {
            return;
        }

        string tempPath = string.Empty;
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string path = Path.Combine(root, "PortaAgent", fileName);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(map);
            tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, json);

            // Atomic replace, retried. A concurrent reader (the service) or a Defender scan can
            // briefly hold the destination and make File.Move throw — historically this failed on
            // EVERY flush, leaving the real file stale and littering ProgramData with orphaned
            // .tmp files. These locks are transient, so retry; if it still won't swap, write the
            // target in place (readers already tolerate a short/partial read) so the labels — and
            // 2e's resolved domains — actually land instead of being blackholed.
            if (!TryAtomicReplace(tempPath, path))
            {
                File.WriteAllText(path, json);
            }
        }
        catch
        {
            // Best-effort only — never let map persistence interfere with capture/flush.
        }
        finally
        {
            // Never orphan the temp (whether the move succeeded, the fallback ran, or we threw).
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
