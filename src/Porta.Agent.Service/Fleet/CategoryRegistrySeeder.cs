using System;
using Porta.Agent.Common.Analysis;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Category bridge (v1.2.112), belt-and-braces half: pre-warms the SERVICE process's
/// AppCategoryClassifier registries from the ProgramData label/category files the
/// user-agent persists. The live bridge (ingest registration) is the primary source;
/// this seed covers hashes stored in old buckets right after a service restart, before
/// they re-arrive on the wire. Meeting flags are not in the files — they re-arrive live.
/// Best-effort by design: any failure is swallowed and logged once, never blocking.
/// </summary>
internal static class CategoryRegistrySeeder
{
    private static readonly TimeSpan ReseedInterval = TimeSpan.FromHours(1);
    private static readonly object Gate = new();
    private static DateTimeOffset _lastSeedUtc = DateTimeOffset.MinValue;
    private static bool _failureLogged;

    /// <summary>Seeds at most hourly unless forced (startup). Thread-safe, best-effort.</summary>
    public static void SeedFromFiles(bool force = false)
    {
        lock (Gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if (!force && nowUtc - _lastSeedUtc < ReseedInterval)
            {
                return;
            }

            _lastSeedUtc = nowUtc;
        }

        try
        {
            foreach (var entry in FocusAppLabelMapReader.ReadLabels())
            {
                AppCategoryClassifier.RegisterHashLabel(entry.Key, entry.Value);
            }

            foreach (var entry in FocusAppLabelMapReader.ReadCategories())
            {
                if (Enum.IsDefined(typeof(AppCategory), entry.Value) && entry.Value != (int)AppCategory.Unknown)
                {
                    AppCategoryClassifier.RegisterHashCategory(entry.Key, (AppCategory)entry.Value);
                }
            }

            _failureLogged = false;
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                Console.Error.WriteLine($"Category registry seed failed (will retry hourly): {ex.GetType().Name}");
            }
        }
    }
}
