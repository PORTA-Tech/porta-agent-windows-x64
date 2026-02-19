namespace Porta.Agent.Common.State;

public sealed class ScrollState
{
    public int RawDelta { get; init; }
    public double NormalizedDelta { get; init; }
    public double VelocityPerSecond { get; init; }
    public long Timestamp { get; init; }
}
