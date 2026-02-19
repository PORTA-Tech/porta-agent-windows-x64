namespace Porta.Agent.Common.Processing;

public readonly struct SignalSample
{
    public long Timestamp { get; }
    public double Value { get; }

    public SignalSample(long timestamp, double value)
    {
        Timestamp = timestamp;
        Value = value;
    }
}
