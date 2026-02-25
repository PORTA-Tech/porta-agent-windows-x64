namespace Porta.Agent.Runtime.Layer4;

internal sealed class Layer4StabilityConfig
{
    public string ModelVersion { get; init; } = "stability-v1.0.0";

    public double ElevatedEnter { get; init; } = 0.25;
    public double ElevatedExit { get; init; } = 0.15;
    public double DegradingEnter { get; init; } = 0.45;
    public double DegradingExit { get; init; } = 0.35;
    public double CriticalEnter { get; init; } = 0.75;
    public double CriticalExit { get; init; } = 0.65;

    public int ElevatedDwellSeconds { get; init; } = 15;
    public int DegradingDwellSeconds { get; init; } = 20;
    public int CriticalDwellSeconds { get; init; } = 30;
    public int RecoveringDwellSeconds { get; init; } = 15;

    public int DegradingCooldownSeconds { get; init; } = 30;
    public int CriticalCooldownSeconds { get; init; } = 60;

    public int FlapTransitionCount { get; init; } = 4;
    public int FlapWindowSeconds { get; init; } = 120;
    public int FlapSuppressionSeconds { get; init; } = 120;
}
