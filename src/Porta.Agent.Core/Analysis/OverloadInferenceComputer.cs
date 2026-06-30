using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct OverloadInferenceResult(
    double OverloadLikelihood,
    double Confidence,
    double FComponent,
    double IComponent,
    double VComponent,
    IReadOnlyList<OverloadTopPrimitive> FTopPrimitives,
    IReadOnlyList<OverloadTopPrimitive> ITopPrimitives,
    IReadOnlyList<OverloadTopPrimitive> VTopPrimitives,
    double Quality,
    double Agreement,
    double PrimitiveCoverage,
    int MissingPrimitiveCount,
    bool UsedSnapshotFallback,
    string Source,
    int ComponentsRegistryVersion,
    double PressureLevel = 0d);

internal readonly record struct OverloadTopPrimitive(
    string Key,
    double Value,
    double Weight,
    double Contribution);

internal static class OverloadInferenceComputer
{
    internal static OverloadInferenceResult ComputeForFinalizedBucket(
        AgentBrainSnapshot snapshot,
        InputBucketRow bucket,
        PrimitiveComputationResult primitive,
        BaselineGatingResult gating,
        RobustNormalizationSnapshot? robust,
        double sustainedOnTaskSeconds = 0d)
    {
        _ = snapshot;
        _ = robust;
        Dictionary<string, double> primitiveValues = BuildPrimitiveValues(primitive);
        Dictionary<string, bool> primitiveAvailability = BuildPrimitiveAvailability(primitive);
        ComponentComputation f = ResolveComponent(OverloadComponentsRegistry.FragmentationComponentKey, primitiveValues);
        ComponentComputation i = ResolveComponent(OverloadComponentsRegistry.IntensityComponentKey, primitiveValues);
        ComponentComputation v = ResolveComponent(OverloadComponentsRegistry.VolatilityComponentKey, primitiveValues);
        double fComponent = f.Value;
        double iComponent = i.Value;
        double vComponent = v.Value;

        double componentOverload = Clamp01(
            (PortaExecutionConstants.OverloadInference.WeightF * fComponent)
            + (PortaExecutionConstants.OverloadInference.WeightV * vComponent)
            + (PortaExecutionConstants.OverloadInference.WeightI * iComponent));

        // Compatibility contract:
        // OverloadLikelihood remains the conservative component-weighted action signal.
        double overload = componentOverload;
        double pressure = ComputePressureLevel(bucket, fComponent, iComponent, vComponent, overload, sustainedOnTaskSeconds);

        double agreementRaw = ComputeAgreementRaw(primitiveValues);
        (double coverage, int missingCount) = ComputePrimitiveCoverage(primitiveAvailability);
        double partialPenalty = bucket.Partial == 1
            ? PortaExecutionConstants.OverloadInference.AgreementPartialPenalty
            : 1d;
        double agreement = Clamp01(agreementRaw * coverage * partialPenalty);
        double quality = Clamp01(bucket.QualityScore);

        double baseConfidence = Clamp01((quality + agreement) / 2d);
        double confidence = Clamp01(Math.Min(baseConfidence, gating.ConfidenceCap));

        return new OverloadInferenceResult(
            OverloadLikelihood: overload,
            PressureLevel: pressure,
            Confidence: confidence,
            FComponent: fComponent,
            IComponent: iComponent,
            VComponent: vComponent,
            FTopPrimitives: f.TopPrimitives,
            ITopPrimitives: i.TopPrimitives,
            VTopPrimitives: v.TopPrimitives,
            Quality: quality,
            Agreement: agreement,
            PrimitiveCoverage: coverage,
            MissingPrimitiveCount: missingCount,
            UsedSnapshotFallback: false,
            Source: "bucket_finalizer",
            ComponentsRegistryVersion: OverloadComponentsRegistry.RegistryVersion);
    }

    private static double ComputePressureLevel(
        InputBucketRow bucket,
        double fComponent,
        double iComponent,
        double vComponent,
        double overload,
        double sustainedOnTaskSeconds)
    {
        double strongest = Math.Max(fComponent, Math.Max(iComponent, vComponent));
        double mean = (fComponent + iComponent + vComponent) / 3d;
        double expressiveBlend = Clamp01(
            (PortaExecutionConstants.OverloadInference.PressureDominantComponentWeight * strongest)
            + (PortaExecutionConstants.OverloadInference.PressureMeanComponentWeight * mean));
        double absoluteStrainFloor = ComputeAbsoluteStrainFloor(bucket);
        double basePressure = Clamp01(Math.Max(overload, Math.Max(expressiveBlend, absoluteStrainFloor)));

        // Sustained-on-task lift (1a). The component blend and strain floors only react to
        // multitasking; sustained single-app effort never moved pressure past ~0.51 across 44k
        // real buckets, leaving the upper half of the scale permanently dead. This term raises
        // pressure as continuous, genuinely-active, no-recovery focus accumulates over time.
        // It is strictly additive and a no-op when on-task duration is below the ramp start, so
        // brief work and every existing caller (which passes 0) are byte-identical to before.
        double sustainedPressure = ComputeSustainedPressure(basePressure, expressiveBlend, sustainedOnTaskSeconds);
        return Clamp01(Math.Max(basePressure, sustainedPressure));
    }

    private static double ComputeSustainedPressure(
        double basePressure,
        double expressiveBlend,
        double sustainedOnTaskSeconds)
    {
        double rampStart = PortaExecutionConstants.OverloadInference.PressureSustainedRampStartSeconds;
        double rampFull = PortaExecutionConstants.OverloadInference.PressureSustainedRampFullSeconds;
        double rampSpan = rampFull - rampStart;
        if (!double.IsFinite(sustainedOnTaskSeconds)
            || sustainedOnTaskSeconds <= rampStart
            || rampSpan <= 0d)
        {
            return basePressure;
        }

        // Duration ramp: 0 at the ramp start, 1 at the ramp full point.
        double ramp = Clamp01((sustainedOnTaskSeconds - rampStart) / rampSpan);
        // Active gate: suppresses the lift when the user is not genuinely working (idle/quiet),
        // so simply leaving an app foreground without input cannot manufacture pressure.
        double activeGate = Clamp01(
            expressiveBlend / PortaExecutionConstants.OverloadInference.PressureSustainedActiveGateReference);
        double lift = PortaExecutionConstants.OverloadInference.PressureSustainedLiftGain * ramp * activeGate;
        // Cap keeps the sustained lift below the overload band (0.72): deep focus reads as
        // "high pressure", never as conservative overload, and never auto-shortens break windows.
        return Math.Min(PortaExecutionConstants.OverloadInference.PressureSustainedCap, basePressure + lift);
    }

    private static double ComputeAbsoluteStrainFloor(InputBucketRow bucket)
    {
        bool softAbsoluteStrain =
            bucket.FocusSwitchCount >= PortaExecutionConstants.OverloadInference.PressureAbsoluteSwitchSoftThreshold
            || bucket.FocusAppEntropy >= PortaExecutionConstants.OverloadInference.PressureAbsoluteEntropySoftThreshold
            || bucket.FocusActiveAppCount >= PortaExecutionConstants.OverloadInference.PressureAbsoluteActiveAppsSoftThreshold;
        bool hardAbsoluteStrain =
            bucket.FocusSwitchCount >= PortaExecutionConstants.OverloadInference.PressureAbsoluteSwitchHardThreshold
            || bucket.FocusAppEntropy >= PortaExecutionConstants.OverloadInference.PressureAbsoluteEntropyHardThreshold
            || bucket.FocusActiveAppCount >= PortaExecutionConstants.OverloadInference.PressureAbsoluteActiveAppsHardThreshold;

        if (hardAbsoluteStrain)
        {
            return PortaExecutionConstants.OverloadInference.PressureAbsoluteHardFloor;
        }

        if (softAbsoluteStrain)
        {
            return PortaExecutionConstants.OverloadInference.PressureAbsoluteSoftFloor;
        }

        return 0d;
    }

    private static Dictionary<string, double> BuildPrimitiveValues(PrimitiveComputationResult primitive)
    {
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey] = Clamp01(primitive.TypingSmoothed),
            [PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey] = Clamp01(primitive.ScrollSmoothed),
            [PrimitiveRegistry.FocusSwitchRate.PrimitiveKey] = Clamp01(primitive.FocusSmoothed)
        };
    }

    private static Dictionary<string, bool> BuildPrimitiveAvailability(PrimitiveComputationResult primitive)
    {
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey] = primitive.TypingChannelAvailable,
            [PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey] = primitive.ScrollChannelAvailable,
            [PrimitiveRegistry.FocusSwitchRate.PrimitiveKey] = primitive.FocusChannelAvailable
        };
    }

    private static ComponentComputation ResolveComponent(
        string componentKey,
        IReadOnlyDictionary<string, double> primitiveValues)
    {
        OverloadComponentDefinition definition = OverloadComponentsRegistry.Components
            .FirstOrDefault(component => string.Equals(component.ComponentKey, componentKey, StringComparison.Ordinal));

        if (definition.Sources.Count == 0)
        {
            return new ComponentComputation(0d, []);
        }

        double weightedSum = 0d;
        double totalWeight = 0d;
        var contributions = new List<OverloadTopPrimitive>(definition.Sources.Count);
        foreach (OverloadComponentSource source in definition.Sources)
        {
            primitiveValues.TryGetValue(source.PrimitiveKey, out double raw);
            double transformed = source.Transform switch
            {
                OverloadPrimitiveTransform.Invert => 1d - Clamp01(raw),
                _ => Clamp01(raw)
            };
            double contribution = source.Weight * transformed;
            contributions.Add(new OverloadTopPrimitive(
                Key: source.PrimitiveKey,
                Value: transformed,
                Weight: source.Weight,
                Contribution: contribution));

            weightedSum += contribution;
            totalWeight += source.Weight;
        }

        if (totalWeight <= 0d || !double.IsFinite(totalWeight))
        {
            return new ComponentComputation(0d, []);
        }

        IReadOnlyList<OverloadTopPrimitive> top = contributions
            .OrderByDescending(static item => item.Contribution)
            .ThenBy(static item => item.Key, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return new ComponentComputation(Clamp01(weightedSum / totalWeight), top);
    }

    private static double ComputeAgreementRaw(IReadOnlyDictionary<string, double> primitiveValues)
    {
        if (OverloadComponentsRegistry.Components.Count == 0)
        {
            return 0d;
        }

        double sum = 0d;
        foreach (OverloadComponentDefinition component in OverloadComponentsRegistry.Components)
        {
            sum += ComputeComponentAgreement(component, primitiveValues);
        }

        return Clamp01(sum / OverloadComponentsRegistry.Components.Count);
    }

    private static double ComputeComponentAgreement(
        OverloadComponentDefinition component,
        IReadOnlyDictionary<string, double> primitiveValues)
    {
        if (component.Sources.Count == 0)
        {
            return 0d;
        }

        double minValue = 1d;
        double maxValue = 0d;
        foreach (OverloadComponentSource source in component.Sources)
        {
            primitiveValues.TryGetValue(source.PrimitiveKey, out double raw);
            double transformed = source.Transform switch
            {
                OverloadPrimitiveTransform.Invert => 1d - Clamp01(raw),
                _ => Clamp01(raw)
            };

            minValue = Math.Min(minValue, transformed);
            maxValue = Math.Max(maxValue, transformed);
        }

        double spread = Clamp01(maxValue - minValue);
        return Clamp01(1d - spread);
    }

    private static (double Coverage, int MissingCount) ComputePrimitiveCoverage(
        IReadOnlyDictionary<string, bool> primitiveAvailability)
    {
        HashSet<string> referencedPrimitives = OverloadComponentsRegistry.Components
            .SelectMany(static component => component.Sources)
            .Select(static source => source.PrimitiveKey)
            .ToHashSet(StringComparer.Ordinal);
        if (referencedPrimitives.Count == 0)
        {
            return (1d, 0);
        }

        int availableCount = 0;
        foreach (string primitiveKey in referencedPrimitives)
        {
            if (primitiveAvailability.TryGetValue(primitiveKey, out bool available) && available)
            {
                availableCount++;
            }
        }

        int missingCount = Math.Max(0, referencedPrimitives.Count - availableCount);
        double coverage = availableCount / (double)referencedPrimitives.Count;
        return (Clamp01(coverage), missingCount);
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private readonly record struct ComponentComputation(
        double Value,
        IReadOnlyList<OverloadTopPrimitive> TopPrimitives);
}
