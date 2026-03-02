namespace Porta.Agent.Runtime.Input;

internal readonly struct ScrollEvent
{
    public long MonotonicTimestamp { get; }
    public long TimestampUtcMs { get; }
    public int Delta { get; }
    public bool WasSuppressed { get; }

    public ScrollEvent(long monotonicTimestamp, long timestampUtcMs, int delta, bool wasSuppressed)
    {
        MonotonicTimestamp = monotonicTimestamp;
        TimestampUtcMs = timestampUtcMs;
        Delta = delta;
        WasSuppressed = wasSuppressed;
    }
}
