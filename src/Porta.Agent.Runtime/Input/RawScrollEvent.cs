namespace Porta.Agent.Runtime.Input;

internal readonly struct ScrollEvent
{
    public long Timestamp { get; }
    public int Delta { get; }

    public ScrollEvent(long timestamp, int delta)
    {
        Timestamp = timestamp;
        Delta = delta;
    }
}
