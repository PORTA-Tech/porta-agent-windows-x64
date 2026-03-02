namespace Porta.Agent.Runtime.Input;

internal enum InputEventKind
{
    MouseWheel = 0,
    Keyboard = 1,
    Focus = 2,
    MouseMove = 3,
    IdleSample = 4,
    ControlTick = 5
}

internal readonly struct RuntimeInputEvent
{
    public InputEventKind Kind { get; }
    public ScrollEvent MouseWheel { get; }
    public KeyboardEvent Keyboard { get; }
    public FocusEvent Focus { get; }
    public MouseMoveEvent MouseMove { get; }
    public IdleSampleEvent IdleSample { get; }
    public long TickTimestamp { get; }

    private RuntimeInputEvent(
        InputEventKind kind,
        ScrollEvent mouseWheel,
        KeyboardEvent keyboard,
        FocusEvent focus,
        MouseMoveEvent mouseMove,
        IdleSampleEvent idleSample,
        long tickTimestamp)
    {
        Kind = kind;
        MouseWheel = mouseWheel;
        Keyboard = keyboard;
        Focus = focus;
        MouseMove = mouseMove;
        IdleSample = idleSample;
        TickTimestamp = tickTimestamp;
    }

    public static RuntimeInputEvent FromMouseWheel(ScrollEvent evt) => new(InputEventKind.MouseWheel, evt, default, default, default, default, 0);
    public static RuntimeInputEvent FromKeyboard(KeyboardEvent evt) => new(InputEventKind.Keyboard, default, evt, default, default, default, 0);
    public static RuntimeInputEvent FromFocus(FocusEvent evt) => new(InputEventKind.Focus, default, default, evt, default, default, 0);
    public static RuntimeInputEvent FromMouseMove(MouseMoveEvent evt) => new(InputEventKind.MouseMove, default, default, default, evt, default, 0);
    public static RuntimeInputEvent FromIdleSample(IdleSampleEvent evt) => new(InputEventKind.IdleSample, default, default, default, default, evt, 0);
    public static RuntimeInputEvent FromControlTick(long timestamp) => new(InputEventKind.ControlTick, default, default, default, default, default, timestamp);
}
