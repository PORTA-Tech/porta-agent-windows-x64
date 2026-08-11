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

    // B3 (v1.2.112) — persistence must stay best-effort but can no longer fail SILENTLY:
    // the Jul 9–13 label freeze on Yadin's machine was invisible until a user noticed
    // "web" rows in the digest. Counters follow the ReadForegroundUnavailableCount()
    // pattern: consecutive failures + last exception type, throttled stderr line, and
    // surfaced through the session-context heartbeat so the ops console can see
    // "label persistence failing" per machine.
    private const int PersistFailureLogEvery = 50;
    private static long _consecutivePersistFailures;
    private static string _lastPersistErrorType = string.Empty;

    /// <summary>Consecutive Persist() write failures; resets to 0 on any success.</summary>
    public static long ReadPersistFailureCount()
        => System.Threading.Interlocked.Read(ref _consecutivePersistFailures);

    /// <summary>Exception type of the most recent persist failure (empty when healthy).</summary>
    public static string ReadLastPersistErrorType()
        => _lastPersistErrorType;

    /// <summary>
    /// Test seam: the directory the maps are written to. Null = the real ProgramData\PortaAgent.
    /// CommonApplicationData is a Windows known folder and cannot be redirected by environment
    /// variable, so the merge behaviour would otherwise be untestable without touching the live map.
    /// </summary>
    internal static string? DirectoryOverrideForTests;

    /// <summary>Writes the current hash → label and hash → category snapshots. Safe to call every flush.</summary>
    public static void Persist()
    {
        bool labelsOk = WriteMap(LabelsFileName, AppCategoryClassifier.SnapshotHashLabels());
        bool categoriesOk = WriteMap(CategoriesFileName, AppCategoryClassifier.SnapshotHashCategories());
        if (labelsOk && categoriesOk)
        {
            System.Threading.Interlocked.Exchange(ref _consecutivePersistFailures, 0);
            _lastPersistErrorType = string.Empty;
        }
    }

    /// <summary>
    /// Upper bound on retained identities, so merging cannot grow the file without limit. A hash
    /// plus a coarse label is ~50 bytes, so this is a few hundred KB at worst. On overflow the
    /// IN-MEMORY (most recently seen) entries are kept in preference to older file ones.
    /// </summary>
    private const int MaxRetainedIdentities = 5000;

    private static bool WriteMap<T>(string fileName, IReadOnlyDictionary<string, T> map)
    {
        if (map.Count == 0)
        {
            return true; // nothing to write is not a failure
        }

        string tempPath = string.Empty;
        try
        {
            string path = DirectoryOverrideForTests is { Length: > 0 } overrideDir
                ? Path.Combine(overrideDir, fileName)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "PortaAgent",
                    fileName);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Complaint 7 (2026-08-11) — MERGE with what is already on disk instead of replacing it.
            //
            // This write used to serialize the in-memory registry over the top of the file. The
            // registry is per-process and starts EMPTY, and the user agent — the process that writes
            // here — never seeds itself from the file (CategoryRegistrySeeder exists, but only the
            // SERVICE calls it). So every user-agent restart, including every watchdog relaunch,
            // truncated the map to whatever had been seen since that restart and destroyed every
            // label learned before it.
            //
            // Measured on the founder's machine 2026-08-11: 5 labels total on the whole machine,
            // while 2026-08-10 alone had 19 distinct foreground identities and 14 of them unnamed.
            // The classifier was working; its output was being erased.
            //
            // Merging (rather than only seeding at startup) also makes this safe against the two
            // processes racing on the same file: a stale writer can no longer delete another's work.
            string json = JsonSerializer.Serialize(MergeWithExisting(path, map));
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

            return true;
        }
        catch (Exception ex)
        {
            // Best-effort only — never let map persistence interfere with capture/flush.
            // But COUNT it: silent failure here starved the digest/console of names for days.
            long failures = System.Threading.Interlocked.Increment(ref _consecutivePersistFailures);
            _lastPersistErrorType = ex.GetType().Name;
            if (failures == 1 || failures % PersistFailureLogEvery == 0)
            {
                Console.Error.WriteLine(
                    $"focus_app_label_persist_failed file={fileName} error={ex.GetType().Name} consecutive={failures}");
            }

            return false;
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

    /// <summary>
    /// Returns the union of what is already stored and what this process has learned, with the
    /// in-memory entry winning on conflict (it is the fresher classification).
    ///
    /// Fails toward the in-memory map: if the existing file is missing, empty or corrupt, this
    /// returns exactly what would have been written before, so a bad file can never block the
    /// write or lose the current session's labels.
    /// </summary>
    private static IReadOnlyDictionary<string, T> MergeWithExisting<T>(
        string path,
        IReadOnlyDictionary<string, T> current)
    {
        Dictionary<string, T> merged;
        try
        {
            if (!File.Exists(path))
            {
                return current;
            }

            string existingJson = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                return current;
            }

            merged = JsonSerializer.Deserialize<Dictionary<string, T>>(existingJson)
                ?? new Dictionary<string, T>(StringComparer.Ordinal);
        }
        catch
        {
            // Unreadable or malformed file: keep this session's labels rather than failing the write.
            return current;
        }

        // Current wins on conflict — a re-seen identity may have been reclassified.
        foreach (KeyValuePair<string, T> entry in current)
        {
            merged[entry.Key] = entry.Value;
        }

        if (merged.Count <= MaxRetainedIdentities)
        {
            return merged;
        }

        // Over the cap: keep everything this process has seen, then top up from the file until full.
        var capped = new Dictionary<string, T>(current, StringComparer.Ordinal);
        foreach (KeyValuePair<string, T> entry in merged)
        {
            if (capped.Count >= MaxRetainedIdentities)
            {
                break;
            }

            capped.TryAdd(entry.Key, entry.Value);
        }

        return capped;
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
