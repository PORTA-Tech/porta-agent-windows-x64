namespace Porta.Agent.Runtime.Input;

internal readonly struct ScrollEvent
{
    public long Timestamp { get; }
    public int Delta { get; }
    public bool WasSuppressed { get; }

    public ScrollEvent(long timestamp, int delta, bool wasSuppressed)
    {
        Timestamp = timestamp;
        Delta = delta;
        WasSuppressed = wasSuppressed;
    }
}
