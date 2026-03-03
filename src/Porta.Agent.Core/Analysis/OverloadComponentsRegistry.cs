namespace Porta.Agent.Core.Analysis;

internal enum OverloadPrimitiveTransform
{
    Identity = 0,
    Invert = 1
}

internal readonly record struct OverloadComponentSource(
    string PrimitiveKey,
    double Weight,
    OverloadPrimitiveTransform Transform);

internal readonly record struct OverloadComponentDefinition(
    string ComponentKey,
    IReadOnlyList<OverloadComponentSource> Sources);

internal static class OverloadComponentsRegistry
{
    internal const int RegistryVersion = 1;
    internal const string FragmentationComponentKey = "F";
    internal const string IntensityComponentKey = "I";
    internal const string VolatilityComponentKey = "V";

    // v1 contract pinned to current primitive registry keys.
    internal static readonly OverloadComponentDefinition Fragmentation = new(
        ComponentKey: FragmentationComponentKey,
        Sources:
        [
            new(
                PrimitiveKey: PrimitiveRegistry.FocusSwitchRate.PrimitiveKey,
                Weight: 0.50d,
                Transform: OverloadPrimitiveTransform.Identity),
            new(
                PrimitiveKey: PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey,
                Weight: 0.35d,
                Transform: OverloadPrimitiveTransform.Identity),
            new(
                PrimitiveKey: PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey,
                Weight: 0.15d,
                Transform: OverloadPrimitiveTransform.Identity)
        ]);

    internal static readonly OverloadComponentDefinition Intensity = new(
        ComponentKey: IntensityComponentKey,
        Sources:
        [
            new(
                PrimitiveKey: PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey,
                Weight: 0.55d,
                Transform: OverloadPrimitiveTransform.Identity),
            new(
                PrimitiveKey: PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey,
                Weight: 0.30d,
                Transform: OverloadPrimitiveTransform.Identity),
            new(
                PrimitiveKey: PrimitiveRegistry.FocusSwitchRate.PrimitiveKey,
                Weight: 0.15d,
                Transform: OverloadPrimitiveTransform.Identity)
        ]);

    internal static readonly OverloadComponentDefinition Volatility = new(
        ComponentKey: VolatilityComponentKey,
        Sources:
        [
            new(
                PrimitiveKey: PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey,
                Weight: 0.55d,
                Transform: OverloadPrimitiveTransform.Identity),
            new(
                PrimitiveKey: PrimitiveRegistry.FocusSwitchRate.PrimitiveKey,
                Weight: 0.30d,
                Transform: OverloadPrimitiveTransform.Identity),
            new(
                PrimitiveKey: PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey,
                Weight: 0.15d,
                Transform: OverloadPrimitiveTransform.Identity)
        ]);

    internal static readonly IReadOnlyList<OverloadComponentDefinition> Components =
    [
        Fragmentation,
        Intensity,
        Volatility
    ];

    static OverloadComponentsRegistry()
    {
        AssertContract();
    }

    private static void AssertContract()
    {
        HashSet<string> primitiveKeys = PrimitiveRegistry.Metrics
            .Select(static metric => metric.PrimitiveKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (OverloadComponentDefinition component in Components)
        {
            if (string.IsNullOrWhiteSpace(component.ComponentKey))
            {
                throw new InvalidOperationException("Overload components registry contains an empty component key.");
            }

            if (component.Sources.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Overload component '{component.ComponentKey}' must have at least one source primitive.");
            }

            foreach (OverloadComponentSource source in component.Sources)
            {
                if (!primitiveKeys.Contains(source.PrimitiveKey))
                {
                    throw new InvalidOperationException(
                        $"Overload component '{component.ComponentKey}' references unknown primitive key '{source.PrimitiveKey}'.");
                }

                if (!double.IsFinite(source.Weight) || source.Weight <= 0d)
                {
                    throw new InvalidOperationException(
                        $"Overload component '{component.ComponentKey}' has non-positive weight for primitive '{source.PrimitiveKey}'.");
                }
            }
        }
    }
}
