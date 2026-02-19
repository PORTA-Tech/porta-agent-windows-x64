namespace Porta.Agent.Common.State;

public sealed class ControlFeatures
{
    public long Timestamp { get; init; }
    public double ArousalError { get; init; }
    public double ArousalVelocity { get; init; }
    public double ArousalIntegral { get; init; }
    public double EngagementIndex { get; init; }
    public double InstabilityIndex { get; init; }
    public double SignalConfidence { get; init; }
}
