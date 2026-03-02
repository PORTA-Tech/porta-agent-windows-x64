namespace Porta.Agent.Runtime.Input;

internal readonly struct MouseMoveEvent
{
    public long MonotonicTimestamp { get; }
    public long TimestampUtcMs { get; }
    public int Dx { get; }
    public int Dy { get; }

    public MouseMoveEvent(long monotonicTimestamp, long timestampUtcMs, int dx, int dy)
    {
        MonotonicTimestamp = monotonicTimestamp;
        TimestampUtcMs = timestampUtcMs;
        Dx = dx;
        Dy = dy;
    }
}
