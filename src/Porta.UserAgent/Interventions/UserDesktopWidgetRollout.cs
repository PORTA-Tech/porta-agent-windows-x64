namespace Porta.UserAgent.Interventions;

internal sealed record UserDesktopWidgetRollout(
    bool WidgetEnabled,
    bool ForceToastFallback)
{
    private const string WidgetEnabledVariable = "PORTA_DESKTOP_WIDGET_ENABLED";
    private const string ForceToastFallbackVariable = "PORTA_DESKTOP_WIDGET_FORCE_TOAST_FALLBACK";

    public bool SupportsWidgetSurface => WidgetEnabled && !ForceToastFallback;

    public string UnavailableReasonCode => SupportsWidgetSurface
        ? string.Empty
        : WidgetEnabled
            ? "widget_forced_toast_fallback"
            : "widget_disabled_by_flag";

    public static UserDesktopWidgetRollout FromEnvironment(Func<string, string?>? environmentReader = null)
    {
        environmentReader ??= Environment.GetEnvironmentVariable;

        bool widgetEnabled = ReadBool(environmentReader, WidgetEnabledVariable, fallback: true);
        bool forceToastFallback = ReadBool(environmentReader, ForceToastFallbackVariable, fallback: false);
        return new UserDesktopWidgetRollout(widgetEnabled, forceToastFallback);
    }

    private static bool ReadBool(
        Func<string, string?> environmentReader,
        string variableName,
        bool fallback)
    {
        string? raw = environmentReader(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }
}
