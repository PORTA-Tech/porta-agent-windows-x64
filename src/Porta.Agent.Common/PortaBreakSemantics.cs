namespace Porta.Agent.Common;

public static class PortaBreakSemantics
{
    // Canonical break kinds — used as both action ID and break kind in new events.
    public const string ShortBreakKind = "short_break";
    public const string LongBreakKind = "long_break";
    // Legacy transport IDs from before the taxonomy simplification.
    // New events emit ShortBreakKind / LongBreakKind directly.
    // These constants exist only for reading and normalising historical events.
    public const string LegacyShortBreakAction = "stretch_break_suggestion";
    public const string LegacyLongBreakAction = "break_suggestion";

    public static bool IsBreakSuggestionAction(string? action) =>
        ResolveBreakKind(action) is not null;

    public static string? ResolveBreakKind(string? actionOrKind)
    {
        string normalized = Normalize(actionOrKind);
        return normalized switch
        {
            ShortBreakKind or LegacyShortBreakAction => ShortBreakKind,
            LongBreakKind or LegacyLongBreakAction => LongBreakKind,
            _ => null
        };
    }

    public static string ResolveBreakLabel(string? actionOrKind, string fallback = "Break")
    {
        return ResolveBreakKind(actionOrKind) switch
        {
            ShortBreakKind => "Short break",
            LongBreakKind => "Long break",
            _ => fallback
        };
    }

    public static TimeSpan ResolveBreakCountdownDuration(string? actionOrKind)
    {
        return ResolveBreakKind(actionOrKind) switch
        {
            ShortBreakKind => TimeSpan.FromSeconds(90),
            LongBreakKind => TimeSpan.FromMinutes(5),
            _ => TimeSpan.FromMinutes(1)
        };
    }

    // Returns the old transport ID for systems that have not yet migrated to
    // reading canonical break kinds. Prefer ShortBreakKind / LongBreakKind directly.
    public static string? ResolveLegacyActionId(string? actionOrKind)
    {
        return ResolveBreakKind(actionOrKind) switch
        {
            ShortBreakKind => LegacyShortBreakAction,
            LongBreakKind => LegacyLongBreakAction,
            _ => null
        };
    }

    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;
}
