namespace Porta.Agent.Runtime.Input;

internal readonly struct KeyboardEvent
{
    public long MonotonicTimestamp { get; }
    public long TimestampUtcMs { get; }
    public bool IsKeyDown { get; }
    public bool IsBackspace { get; }
    public bool IsInjected { get; }

    public KeyboardEvent(long monotonicTimestamp, long timestampUtcMs, bool isKeyDown, bool isBackspace, bool isInjected)
    {
        MonotonicTimestamp = monotonicTimestamp;
        TimestampUtcMs = timestampUtcMs;
        IsKeyDown = isKeyDown;
        IsBackspace = isBackspace;
        IsInjected = isInjected;
    }
}
