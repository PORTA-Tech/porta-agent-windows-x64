using System;
using System.Text.Json;
using Porta.Agent.Common.Control;

namespace Porta.Agent.Common.Contracts;

public sealed class ControlSnapshotTelemetry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public const int CurrentSchemaVersion = 1;
    public const int ValuePrecisionDecimals = 6;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long TickTimestamp { get; init; }
    public DateTimeOffset CapturedUtc { get; init; }
    public int PrecisionDecimals { get; init; } = ValuePrecisionDecimals;

    public double AggScrollRate { get; init; }
    public double AggKeyboardRate { get; init; }
    public double AggFocusStability { get; init; }
    public double AggInteractionVariance { get; init; }
    public double AggCompositeArousal { get; init; }
    public double AggSignalConfidence { get; init; }

    public double FeatArousalError { get; init; }
    public double FeatArousalVelocity { get; init; }
    public double FeatArousalIntegral { get; init; }
    public double FeatEngagementIndex { get; init; }
    public double FeatInstabilityIndex { get; init; }
    public double FeatSignalConfidence { get; init; }

    public double PolicyProposedIntensity { get; init; }
    public double PolicyProposedDynamics { get; init; }
    public double PolicyProposedVisualShift { get; init; }
    public double PolicyProposedPacing { get; init; }
    public string PolicyProposedAuthority { get; init; } = string.Empty;

    public double SafetyAppliedIntensity { get; init; }
    public double SafetyAppliedDynamics { get; init; }
    public double SafetyAppliedVisualShift { get; init; }
    public double SafetyAppliedPacing { get; init; }
    public string SafetyAppliedAuthority { get; init; } = string.Empty;
    public string SafetyNote { get; init; } = string.Empty;

    public static ControlSnapshotTelemetry FromSnapshot(
        long tickTimestamp,
        DateTimeOffset capturedUtc,
        DeterministicControlEngine.ControlEvaluationSnapshot snapshot)
    {
        return new ControlSnapshotTelemetry
        {
            TickTimestamp = tickTimestamp,
            CapturedUtc = capturedUtc,
            AggScrollRate = Q(snapshot.Aggregated.ScrollRate),
            AggKeyboardRate = Q(snapshot.Aggregated.KeyboardRate),
            AggFocusStability = Q(snapshot.Aggregated.FocusStability),
            AggInteractionVariance = Q(snapshot.Aggregated.InteractionVariance),
            AggCompositeArousal = Q(snapshot.Aggregated.CompositeArousalEstimate),
            AggSignalConfidence = Q(snapshot.Aggregated.SignalConfidence),
            FeatArousalError = Q(snapshot.Features.ArousalError),
            FeatArousalVelocity = Q(snapshot.Features.ArousalVelocity),
            FeatArousalIntegral = Q(snapshot.Features.ArousalIntegral),
            FeatEngagementIndex = Q(snapshot.Features.EngagementIndex),
            FeatInstabilityIndex = Q(snapshot.Features.InstabilityIndex),
            FeatSignalConfidence = Q(snapshot.Features.SignalConfidence),
            PolicyProposedIntensity = Q(snapshot.ProposedCommand.StimulationIntensity),
            PolicyProposedDynamics = Q(snapshot.ProposedCommand.SceneDynamics),
            PolicyProposedVisualShift = Q(snapshot.ProposedCommand.VisualTemperatureShift),
            PolicyProposedPacing = Q(snapshot.ProposedCommand.PacingModifier),
            PolicyProposedAuthority = snapshot.ProposedCommand.AuthorityLevel.ToString().ToLowerInvariant(),
            SafetyAppliedIntensity = Q(snapshot.SafeCommand.StimulationIntensity),
            SafetyAppliedDynamics = Q(snapshot.SafeCommand.SceneDynamics),
            SafetyAppliedVisualShift = Q(snapshot.SafeCommand.VisualTemperatureShift),
            SafetyAppliedPacing = Q(snapshot.SafeCommand.PacingModifier),
            SafetyAppliedAuthority = snapshot.SafeCommand.AuthorityLevel.ToString().ToLowerInvariant(),
            SafetyNote = snapshot.SafeCommand.Note
        };
    }

    public string ToCanonicalJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    private static double Q(double value)
    {
        return Math.Round(value, ValuePrecisionDecimals, MidpointRounding.AwayFromZero);
    }
}
