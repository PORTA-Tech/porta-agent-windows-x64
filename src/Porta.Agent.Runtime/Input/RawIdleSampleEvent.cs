namespace Porta.Agent.Runtime.Input;

internal readonly struct IdleSampleEvent
{
    public long TimestampUtcMs { get; }
    public double IdleSeconds { get; }

    public IdleSampleEvent(long timestampUtcMs, double idleSeconds)
    {
        TimestampUtcMs = timestampUtcMs;
        IdleSeconds = idleSeconds;
    }
}
