using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// 2d: one pass of the server-side categorization loop. Finds the coarse tokens the user-agent has
/// seen but the local curated rules could not categorize (label present, category Unknown), asks
/// the control plane to resolve them, and merges any answers into the overlay file the user-agent
/// reads. Newly-seen tokens are queued server-side and simply retried next pass.
///
/// Fully best-effort: any failure is swallowed so it can never disrupt fleet reporting.
/// </summary>
internal static class AppCategoryOverlaySync
{
    private const int MaxTokensPerSync = 100;

    // The generic "unbranded web" bucket is intentionally never sent for categorization — it is a
    // catch-all, not a real site, so an LLM answer for it would be meaningless.
    private const string GenericWebToken = "web";

    public static async Task RunOnceAsync(ControlPlaneClient client, string agentId, CancellationToken token)
    {
        if (client is null || !client.IsEnabled || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        IReadOnlyDictionary<string, string> labels = FocusAppLabelMapReader.ReadLabels();
        if (labels.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, int> categories = FocusAppLabelMapReader.ReadCategories();

        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in labels)
        {
            // Unresolved = seen (has a label) but with no category, or an explicit Unknown (0).
            if (categories.TryGetValue(entry.Key, out int category) && category != 0)
            {
                continue;
            }

            string tokenText = (entry.Value ?? string.Empty).Trim().ToLowerInvariant();
            if (tokenText.Length == 0 || string.Equals(tokenText, GenericWebToken, StringComparison.Ordinal))
            {
                continue;
            }

            unresolved.Add(tokenText);
            if (unresolved.Count >= MaxTokensPerSync)
            {
                break;
            }
        }

        if (unresolved.Count == 0)
        {
            return;
        }

        var tokens = new List<string>(unresolved);
        AppCategoryResolveResultPayload? result =
            await client.ResolveAppCategoriesAsync(agentId, tokens, token).ConfigureAwait(false);

        if (result?.Mappings is { Count: > 0 } mappings)
        {
            AppCategoryOverlayWriter.MergeAndWrite(mappings);
        }
    }
}
