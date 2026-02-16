namespace Porta.Agent.Runtime.Input;

internal readonly struct RawScrollEvent
{
    public long Timestamp { get; }
    public int Delta { get; }

    public RawScrollEvent(long timestamp, int delta)
    {
        Timestamp = timestamp;
        Delta = delta;
    }
}
