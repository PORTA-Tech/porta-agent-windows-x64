namespace Porta.Agent.Core.Analysis;

internal readonly record struct PrimitiveMetricDefinition(
    string PrimitiveKey,
    IReadOnlyList<string> SourceMetricKeys,
    string ChannelDependency,
    double EwmaAlpha,
    bool AppliesMissingChannelRule);

internal static class PrimitiveRegistry
{
    internal const int RegistryVersion = 1;

    internal static readonly PrimitiveMetricDefinition TypingBurstIntensity = new(
        PrimitiveKey: "typing_burst_intensity",
        SourceMetricKeys:
        [
            "keyboard_iki_mean",
            "keyboard_iki_cv",
            "keyboard_burst_rate",
            "keyboard_backspace_rate"
        ],
        ChannelDependency: "keyboard",
        EwmaAlpha: PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha,
        AppliesMissingChannelRule: true);

    internal static readonly PrimitiveMetricDefinition ScrollVelocityIndex = new(
        PrimitiveKey: "scroll_velocity_index",
        SourceMetricKeys:
        [
            "mouse_speed_mean",
            "mouse_jerk_p95",
            "mouse_scroll_speed",
            "mouse_micro_correction_rate"
        ],
        ChannelDependency: "mouse",
        EwmaAlpha: PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha,
        AppliesMissingChannelRule: true);

    internal static readonly PrimitiveMetricDefinition FocusSwitchRate = new(
        PrimitiveKey: "focus_switch_rate",
        SourceMetricKeys:
        [
            "focus_switch_rate",
            "focus_app_entropy",
            "focus_active_app_count"
        ],
        ChannelDependency: "focus",
        EwmaAlpha: PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha,
        AppliesMissingChannelRule: true);

    internal static readonly IReadOnlyList<PrimitiveMetricDefinition> Metrics =
    [
        TypingBurstIntensity,
        ScrollVelocityIndex,
        FocusSwitchRate
    ];
}
