namespace Porta.Agent.Common.State;

public sealed class AggregatedState
{
    public long Timestamp { get; init; }
    public double ScrollRate { get; init; }
    public double KeyboardRate { get; init; }
    public double FocusStability { get; init; }
    public double InteractionVariance { get; init; }
    public double CompositeArousalEstimate { get; init; }
    public double SignalConfidence { get; init; }
}
