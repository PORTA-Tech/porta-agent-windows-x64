using System;
using System.Collections.Generic;
using System.Linq;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Turns a hash → dwell-seconds map plus a hash → coarse-label map into the ranked
/// per-app usage list shared by the daily digest ("Where your time went") and the
/// per-2h daily-stats upload (which powers the console per-app view and the
/// server-fallback digest). Single source of truth so both paths rank identically.
/// Coarse labels only — never a raw window title / URL.
/// </summary>
internal static class FocusAppUsageBuilder
{
    /// <summary>Minimum foreground time (seconds) for an app to be worth showing.</summary>
    public const double MinSecondsToReport = 60d;

    /// <summary>Maximum number of apps reported.</summary>
    public const int MaxApps = 8;

    public static IReadOnlyList<FocusAppUsagePayload> BuildTopApps(
        IReadOnlyDictionary<string, double> dwellByHash,
        IReadOnlyDictionary<string, string> labelByHash,
        IReadOnlyDictionary<string, int>? categoryByHash = null)
    {
        var byLabel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var categoryForLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in dwellByHash)
        {
            if (!labelByHash.TryGetValue(entry.Key, out string? label) || string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            byLabel[label] = byLabel.TryGetValue(label, out double existing) ? existing + entry.Value : entry.Value;

            // Attach the app's category (first known/non-zero wins for a given label) so the
            // server can attribute distraction to specific apps without re-deriving categories.
            if (categoryByHash != null
                && categoryByHash.TryGetValue(entry.Key, out int cat) && cat != 0
                && (!categoryForLabel.TryGetValue(label, out int knownCat) || knownCat == 0))
            {
                categoryForLabel[label] = cat;
            }
        }

        return byLabel
            .Where(static kv => kv.Value >= MinSecondsToReport)
            .OrderByDescending(static kv => kv.Value)
            .Take(MaxApps)
            .Select(kv => new FocusAppUsagePayload(
                kv.Key,
                Math.Round(kv.Value),
                categoryForLabel.TryGetValue(kv.Key, out int c) ? c : 0))
            .ToList();
    }
}
