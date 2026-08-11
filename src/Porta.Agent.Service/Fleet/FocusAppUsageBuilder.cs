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

    /// <summary>
    /// Workstream 5 — the residual honesty rule. Browser pages that neither the curated
    /// title rules nor the 2e address-bar fallback could recognize all pool into one
    /// identity labeled with <see cref="Porta.Agent.Common.Analysis.BrowserTitleClassifier.UnknownSiteToken"/>
    /// ("web") — verified on real fleet label maps, where that single pooled row can rank
    /// high in "where your time went". "web" is meaningless to users; the residue renders
    /// as this honest label instead, and never as a guessed site or category.
    /// </summary>
    public const string ResidualBrowsingLabel = "Other browsing";

    /// <summary>
    /// Complaint 7 (2026-08-11) — foreground time whose identity was never labelled at all.
    ///
    /// This used to be DROPPED: no entry in the label map meant `continue`, and the time simply
    /// left the report. Measured on the founder's machine, 2026-08-10: 42m 30s across 14 of 19
    /// identities went nowhere, so "where your time went" did not add up to the day he worked.
    /// That is worse than an honest unnamed row and breaks the same rule the residue exists to
    /// keep — admit the time, never guess the name.
    ///
    /// It is deliberately NOT folded into <see cref="ResidualBrowsingLabel"/>. An unlabelled
    /// identity might be a desktop app rather than a browser page — nothing here knows which —
    /// and calling a desktop app "Other browsing" would be exactly the invented name a client
    /// report must never contain. Two honest rows still add up; one guessed row does not.
    /// </summary>
    public const string UnnamedTimeLabel = "Unnamed";

    public static IReadOnlyList<FocusAppUsagePayload> BuildTopApps(
        IReadOnlyDictionary<string, double> dwellByHash,
        IReadOnlyDictionary<string, string> labelByHash,
        IReadOnlyDictionary<string, int>? categoryByHash = null)
    {
        var byLabel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var categoryForLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in dwellByHash)
        {
            // Complaint 7 — an identity with no label is time we cannot NAME, not time that did not
            // happen. It now surfaces under UnnamedTimeLabel instead of being dropped, so the day
            // adds up. See the field measurement on that constant.
            if (!labelByHash.TryGetValue(entry.Key, out string? label) || string.IsNullOrWhiteSpace(label))
            {
                label = UnnamedTimeLabel;
            }

            // A1 — the lock screen and other non-user shell surfaces are away time, never an app.
            // The capture fix (UserSessionNativeSignals) stops new dwell being recorded for them;
            // this guard keeps rows ALREADY stored under a shell label (e.g. "lockapp") out of
            // "where your time went" on machines whose history predates that fix.
            if (Porta.Agent.Common.Analysis.AppCategoryClassifier.IsSystemShellLabel(label))
            {
                continue;
            }

            if (string.Equals(label, Porta.Agent.Common.Analysis.BrowserTitleClassifier.UnknownSiteToken, StringComparison.OrdinalIgnoreCase))
            {
                label = ResidualBrowsingLabel;
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
