namespace Porta.Agent.Runtime.Input;

internal enum InputEventKind
{
    Scroll = 0,
    Keyboard = 1,
    Focus = 2,
    ControlTick = 3
}

internal readonly struct RuntimeInputEvent
{
    public InputEventKind Kind { get; }
    public ScrollEvent Scroll { get; }
    public KeyboardEvent Keyboard { get; }
    public FocusEvent Focus { get; }
    public long TickTimestamp { get; }

    private RuntimeInputEvent(InputEventKind kind, ScrollEvent scroll, KeyboardEvent keyboard, FocusEvent focus, long tickTimestamp)
    {
        Kind = kind;
        Scroll = scroll;
        Keyboard = keyboard;
        Focus = focus;
        TickTimestamp = tickTimestamp;
    }

    public static RuntimeInputEvent FromScroll(ScrollEvent evt) => new(InputEventKind.Scroll, evt, default, default, 0);
    public static RuntimeInputEvent FromKeyboard(KeyboardEvent evt) => new(InputEventKind.Keyboard, default, evt, default, 0);
    public static RuntimeInputEvent FromFocus(FocusEvent evt) => new(InputEventKind.Focus, default, default, evt, 0);
    public static RuntimeInputEvent FromControlTick(long timestamp) => new(InputEventKind.ControlTick, default, default, default, timestamp);
}
