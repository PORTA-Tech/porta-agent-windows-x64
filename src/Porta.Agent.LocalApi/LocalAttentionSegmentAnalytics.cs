using System.Text.Json;

namespace Porta.Agent.LocalApi;

public static class LocalAttentionSegmentAnalytics
{
    public const string FocusSegmentKind = "focus";
    public const string ContextExcursionSegmentKind = "context_excursion";
    public const string OffTaskDistractionSegmentKind = "off_task_distraction";
    public const string FragmentationDistractionSegmentKind = "fragmentation_distraction";
    public const string AwaySegmentKind = "away";

    public static string ResolveAttentionSegmentKind(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string explicitKind = NormalizeAttentionSegmentKind(snapshot.AttentionSegmentKind);
        if (!string.Equals(explicitKind, FocusSegmentKind, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(snapshot.AttentionSegmentKind))
        {
            return explicitKind;
        }

        return NormalizeAttentionSegmentKind(ReadStringFlag(snapshot.StateFlagsJson, "attention_segment_kind"));
    }

    public static double ResolveAttentionSegmentSeconds(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.AttentionSegmentSeconds > 0d)
        {
            return snapshot.AttentionSegmentSeconds;
        }

        return Math.Max(0d, ReadDoubleFlag(snapshot.StateFlagsJson, "attention_segment_seconds"));
    }

    public static bool IsDistractionEpisode(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.DistractionEpisodeActive)
        {
            return true;
        }

        string kind = ResolveAttentionSegmentKind(snapshot);
        return string.Equals(kind, OffTaskDistractionSegmentKind, StringComparison.Ordinal)
            || string.Equals(kind, FragmentationDistractionSegmentKind, StringComparison.Ordinal);
    }

    public static double ResolveDistractionEpisodeSeconds(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.DistractionEpisodeSeconds > 0d)
        {
            return snapshot.DistractionEpisodeSeconds;
        }

        double flaggedSeconds = Math.Max(0d, ReadDoubleFlag(snapshot.StateFlagsJson, "distraction_episode_seconds"));
        if (flaggedSeconds > 0d)
        {
            return flaggedSeconds;
        }

        return IsDistractionEpisode(snapshot)
            ? ResolveAttentionSegmentSeconds(snapshot)
            : 0d;
    }

    public static bool IsAwaySegment(LocalApiCurrentSnapshotResponse snapshot) =>
        string.Equals(ResolveAttentionSegmentKind(snapshot), AwaySegmentKind, StringComparison.Ordinal);

    public static bool IsContextExcursionSegment(LocalApiCurrentSnapshotResponse snapshot) =>
        string.Equals(ResolveAttentionSegmentKind(snapshot), ContextExcursionSegmentKind, StringComparison.Ordinal);

    public static string NormalizeAttentionSegmentKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            ContextExcursionSegmentKind => ContextExcursionSegmentKind,
            OffTaskDistractionSegmentKind => OffTaskDistractionSegmentKind,
            FragmentationDistractionSegmentKind => FragmentationDistractionSegmentKind,
            AwaySegmentKind => AwaySegmentKind,
            _ => FocusSegmentKind
        };

    private static string? ReadStringFlag(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out JsonElement node)
                && node.ValueKind == JsonValueKind.String
                    ? node.GetString()
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private static double ReadDoubleFlag(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return 0d;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out JsonElement node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetDouble(out double numeric))
            {
                return numeric;
            }
        }
        catch
        {
        }

        return 0d;
    }
}
