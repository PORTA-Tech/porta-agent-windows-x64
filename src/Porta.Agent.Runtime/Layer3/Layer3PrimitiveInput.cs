namespace Porta.Agent.Runtime.Layer3;

internal readonly record struct Layer3PrimitiveInput(
    string PrimitiveId,
    string CorrelationId,
    string MachineId,
    string Name,
    double Value,
    string Unit,
    double ZScore,
    double Ewma,
    double Confidence,
    DateTimeOffset ComputedAtUtc);
