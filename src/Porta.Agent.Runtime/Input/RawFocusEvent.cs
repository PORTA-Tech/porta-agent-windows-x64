namespace Porta.Agent.Runtime.Input;

internal readonly struct FocusEvent
{
    public long Timestamp { get; }
    public uint ProcessId { get; }
    public nint WindowHandle { get; }

    public FocusEvent(long timestamp, uint processId, nint windowHandle)
    {
        Timestamp = timestamp;
        ProcessId = processId;
        WindowHandle = windowHandle;
    }
}
