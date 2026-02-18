namespace Porta.Agent.Runtime.Input;

internal readonly struct KeyboardEvent
{
    public long Timestamp { get; }
    public int VirtualKey { get; }
    public bool IsKeyDown { get; }
    public bool IsInjected { get; }

    public KeyboardEvent(long timestamp, int virtualKey, bool isKeyDown, bool isInjected)
    {
        Timestamp = timestamp;
        VirtualKey = virtualKey;
        IsKeyDown = isKeyDown;
        IsInjected = isInjected;
    }
}
