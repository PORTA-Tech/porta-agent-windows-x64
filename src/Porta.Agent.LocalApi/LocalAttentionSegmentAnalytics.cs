using System.Text.Json;

namespace Porta.Agent.LocalApi;

public static class LocalAttentionSegmentAnalytics
{
    public const string SteadyFocusReasonCode = "steady_focus";
    public const string FocusEpisodeActiveReasonCode = "focus_episode_active";
    public const string FocusSegmentKind = "focus";
    public const string ContextExcursionSegmentKind = "context_excursion";
    public const string OffTaskDistractionSegmentKind = "off_task_distraction";
    public const string FragmentationDistractionSegmentKind = "fragmentation_distraction";
    public const string AwaySegmentKind = "away";
    // Block F — new distraction kinds
    public const string RollingMicroBurstSegmentKind         = "rolling_micro_burst";
    public const string ReturnLoopDistractionSegmentKind     = "return_loop_distraction";
    public const string UnfocusedSessionSegmentKind          = "unfocused_session";
    public const string ShallowDwellDistractionSegmentKind   = "shallow_dwell_distraction";
    public const string FocusDepartureDistractionSegmentKind = "focus_departure_distraction";

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
            || string.Equals(kind, FragmentationDistractionSegmentKind, StringComparison.Ordinal)
            || string.Equals(kind, RollingMicroBurstSegmentKind, StringComparison.Ordinal)
            || string.Equals(kind, ReturnLoopDistractionSegmentKind, StringComparison.Ordinal)
            || string.Equals(kind, UnfocusedSessionSegmentKind, StringComparison.Ordinal)
            || string.Equals(kind, ShallowDwellDistractionSegmentKind, StringComparison.Ordinal)
            || string.Equals(kind, FocusDepartureDistractionSegmentKind, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true when the attention-segment kind string corresponds to a genuine
    /// distraction episode. Context excursion and away are intentionally excluded —
    /// context excursion does not reset the focus timer; away has its own gating path.
    /// </summary>
    public static bool IsDistractionKind(string? kind)
    {
        string normalized = NormalizeAttentionSegmentKind(kind);
        return normalized is
            OffTaskDistractionSegmentKind or
            FragmentationDistractionSegmentKind or
            RollingMicroBurstSegmentKind or
            ReturnLoopDistractionSegmentKind or
            UnfocusedSessionSegmentKind or
            ShallowDwellDistractionSegmentKind or
            FocusDepartureDistractionSegmentKind;
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

    public static string ResolveAttentionSegmentReasonCode(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string explicitCode = NormalizeReasonCode(snapshot.AttentionSegmentReasonCode);
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            return explicitCode;
        }

        string fromFlags = NormalizeReasonCode(ReadStringFlag(snapshot.StateFlagsJson, "attention_segment_reason_code"));
        if (!string.IsNullOrWhiteSpace(fromFlags))
        {
            return fromFlags;
        }

        return ResolveAttentionSegmentKind(snapshot) switch
        {
            AwaySegmentKind => "away",
            ContextExcursionSegmentKind => "context_excursion",
            OffTaskDistractionSegmentKind => "outside_focus_context",
            FragmentationDistractionSegmentKind => "fragmentation_distraction",
            _ => SteadyFocusReasonCode
        };
    }

    public static string ResolveAttentionSegmentReasonText(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? explicitText = NormalizeReasonText(snapshot.AttentionSegmentReasonText);
        if (!string.IsNullOrWhiteSpace(explicitText))
        {
            return explicitText;
        }

        string? fromFlags = NormalizeReasonText(ReadStringFlag(snapshot.StateFlagsJson, "attention_segment_reason_text"));
        if (!string.IsNullOrWhiteSpace(fromFlags))
        {
            return fromFlags;
        }

        string kind = ResolveAttentionSegmentKind(snapshot);
        double seconds = ResolveAttentionSegmentSeconds(snapshot);
        double distrSec = ResolveDistractionEpisodeSeconds(snapshot);
        return kind switch
        {
            AwaySegmentKind                      => $"Away for {FormatEpisodeDuration(seconds)}.",
            ContextExcursionSegmentKind          => $"Outside focus context for {FormatEpisodeDuration(seconds)}.",
            OffTaskDistractionSegmentKind        => $"Stayed outside focus context for {FormatEpisodeDuration(distrSec)}.",
            FragmentationDistractionSegmentKind  => $"Rapid switching / fragmentation for {FormatEpisodeDuration(distrSec)}.",
            // Block F — new distraction kinds
            RollingMicroBurstSegmentKind         => $"Rapid repeated app switching for {FormatEpisodeDuration(distrSec)}.",
            ReturnLoopDistractionSegmentKind     => $"Repetitive ping-pong between two apps for {FormatEpisodeDuration(distrSec)}.",
            UnfocusedSessionSegmentKind          => $"Active for {FormatEpisodeDuration(distrSec)} without settling on a focus app.",
            ShallowDwellDistractionSegmentKind   => $"Sustained surface-level browsing for {FormatEpisodeDuration(distrSec)}.",
            FocusDepartureDistractionSegmentKind => $"Away from focus app for {FormatEpisodeDuration(distrSec)}.",
            _ => "Steady focus episode."
        };
    }

    public static string ResolveFocusEpisodeReasonCode(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string explicitCode = NormalizeReasonCode(snapshot.FocusEpisodeReasonCode);
        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            return explicitCode;
        }

        string fromFlags = NormalizeReasonCode(ReadStringFlag(snapshot.StateFlagsJson, "focus_episode_reason_code"));
        if (!string.IsNullOrWhiteSpace(fromFlags))
        {
            return fromFlags;
        }

        return snapshot.FocusEpisodeState?.Trim().ToLowerInvariant() switch
        {
            "reset" => "focus_episode_reset",
            "pause" => "focus_episode_paused",
            "decay" => "focus_episode_decay",
            _ => FocusEpisodeActiveReasonCode
        };
    }

    public static string ResolveFocusEpisodeReasonText(LocalApiCurrentSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? explicitText = NormalizeReasonText(snapshot.FocusEpisodeReasonText);
        if (!string.IsNullOrWhiteSpace(explicitText))
        {
            return explicitText;
        }

        string? fromFlags = NormalizeReasonText(ReadStringFlag(snapshot.StateFlagsJson, "focus_episode_reason_text"));
        if (!string.IsNullOrWhiteSpace(fromFlags))
        {
            return fromFlags;
        }

        return snapshot.FocusEpisodeState?.Trim().ToLowerInvariant() switch
        {
            "reset" => "Focus episode ended.",
            "pause" => "Focus episode is paused.",
            "decay" => "Focus episode is fading.",
            _ => "Focus episode active."
        };
    }

    public static string NormalizeAttentionSegmentKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            ContextExcursionSegmentKind              => ContextExcursionSegmentKind,
            OffTaskDistractionSegmentKind            => OffTaskDistractionSegmentKind,
            FragmentationDistractionSegmentKind      => FragmentationDistractionSegmentKind,
            AwaySegmentKind                          => AwaySegmentKind,
            // Block F — new kinds must be recognised; without this they silently become "focus"
            RollingMicroBurstSegmentKind             => RollingMicroBurstSegmentKind,
            ReturnLoopDistractionSegmentKind         => ReturnLoopDistractionSegmentKind,
            UnfocusedSessionSegmentKind              => UnfocusedSessionSegmentKind,
            ShallowDwellDistractionSegmentKind       => ShallowDwellDistractionSegmentKind,
            FocusDepartureDistractionSegmentKind     => FocusDepartureDistractionSegmentKind,
            _ => FocusSegmentKind
        };

    public static string NormalizeReasonCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Replace('-', '_').ToLowerInvariant();
    }

    private static string? NormalizeReasonText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatEpisodeDuration(double seconds)
    {
        double clampedSeconds = Math.Max(0d, seconds);
        if (clampedSeconds < 60d)
        {
            return $"{Math.Round(clampedSeconds, MidpointRounding.AwayFromZero)}s";
        }

        int totalSeconds = (int)Math.Round(clampedSeconds, MidpointRounding.AwayFromZero);
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int secs = totalSeconds % 60;
        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        return secs > 0 && minutes < 3 ? $"{minutes}m {secs}s" : $"{minutes}m";
    }

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
