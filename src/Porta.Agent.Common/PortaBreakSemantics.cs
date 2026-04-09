namespace Porta.Agent.Common;

public static class PortaBreakSemantics
{
    public const string ShortBreakKind = "short_break";
    public const string LongBreakKind = "long_break";
    public const string ShortBreakAction = "stretch_break_suggestion";
    public const string LongBreakAction = "break_suggestion";

    public static bool IsBreakSuggestionAction(string? action) =>
        ResolveBreakKind(action) is not null;

    public static string? ResolveBreakKind(string? actionOrKind)
    {
        string normalized = Normalize(actionOrKind);
        return normalized switch
        {
            ShortBreakKind or ShortBreakAction => ShortBreakKind,
            LongBreakKind or LongBreakAction => LongBreakKind,
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

    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;
}
