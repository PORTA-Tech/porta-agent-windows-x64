using System.Text.Json;
using Porta.Agent.Common.Analysis;

namespace Porta.Agent.LocalApi;

public static class LocalAttentionSegmentAnalytics
{
    public const string SteadyFocusReasonCode = "steady_focus";
    public const string FocusEpisodeActiveReasonCode = "focus_episode_active";

    // Kind strings are defined once in Porta.Agent.Common.Analysis.AttentionSegmentKinds — the
    // single source of truth shared by the agent producer and every reader. These public
    // aliases keep the existing LocalApi / Control Center / test call sites working unchanged
    // while guaranteeing there is only one place a kind string is ever spelled.
    public const string FocusSegmentKind                     = AttentionSegmentKinds.Focus;
    public const string ContextExcursionSegmentKind          = AttentionSegmentKinds.ContextExcursion;
    public const string OffTaskDistractionSegmentKind        = AttentionSegmentKinds.OffTaskDistraction;
    public const string FragmentationDistractionSegmentKind  = AttentionSegmentKinds.FragmentationDistraction;
    public const string AwaySegmentKind                      = AttentionSegmentKinds.Away;
    // Block F distraction kinds
    public const string RollingMicroBurstSegmentKind         = AttentionSegmentKinds.RollingMicroBurst;
    public const string ReturnLoopDistractionSegmentKind     = AttentionSegmentKinds.ReturnLoopDistraction;
    public const string UnfocusedSessionSegmentKind          = AttentionSegmentKinds.UnfocusedSession;
    public const string ShallowDwellDistractionSegmentKind   = AttentionSegmentKinds.ShallowDwellDistraction;
    public const string FocusDepartureDistractionSegmentKind = AttentionSegmentKinds.FocusDepartureDistraction;
    // Phase 3 / category distraction kinds
    public const string SocialMediaDistractionSegmentKind    = AttentionSegmentKinds.SocialMediaDistraction;
    public const string SocialMediaLoopSegmentKind           = AttentionSegmentKinds.SocialMediaLoop;
    public const string GamingLauncherDistractionSegmentKind = AttentionSegmentKinds.GamingLauncherDistraction;
    public const string ActiveGamingDistractionSegmentKind   = AttentionSegmentKinds.ActiveGamingDistraction;
    public const string PassiveConsumptionSegmentKind        = AttentionSegmentKinds.PassiveConsumption;
    public const string CommMonitoringLoopSegmentKind        = AttentionSegmentKinds.CommMonitoringLoop;
    public const string FocusCategoryAbsentSegmentKind       = AttentionSegmentKinds.FocusCategoryAbsent;
    public const string BadSessionStartSegmentKind           = AttentionSegmentKinds.BadSessionStart;

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
    public static bool IsDistractionKind(string? kind) =>
        AttentionSegmentKinds.IsDistraction(kind);

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
            // Phase 3 / category kinds — display-mapped to off-task distraction so the existing
            // Control Center episode rendering and counts treat them as distraction. Mapping
            // here (the single chokepoint every read path funnels through) means no Control
            // Center code needs to change. See the const block above for why.
            SocialMediaDistractionSegmentKind        => OffTaskDistractionSegmentKind,
            SocialMediaLoopSegmentKind               => OffTaskDistractionSegmentKind,
            GamingLauncherDistractionSegmentKind     => OffTaskDistractionSegmentKind,
            ActiveGamingDistractionSegmentKind       => OffTaskDistractionSegmentKind,
            PassiveConsumptionSegmentKind            => OffTaskDistractionSegmentKind,
            CommMonitoringLoopSegmentKind            => OffTaskDistractionSegmentKind,
            FocusCategoryAbsentSegmentKind           => OffTaskDistractionSegmentKind,
            BadSessionStartSegmentKind               => OffTaskDistractionSegmentKind,
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
