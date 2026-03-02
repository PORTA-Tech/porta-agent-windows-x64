namespace Porta.Agent.Runtime.Input;

internal readonly struct FocusEvent
{
    public long MonotonicTimestamp { get; }
    public long TimestampUtcMs { get; }
    public string AppHash { get; }

    public FocusEvent(long monotonicTimestamp, long timestampUtcMs, string appHash)
    {
        MonotonicTimestamp = monotonicTimestamp;
        TimestampUtcMs = timestampUtcMs;
        AppHash = string.IsNullOrWhiteSpace(appHash) ? "unknown" : appHash;
    }
}
