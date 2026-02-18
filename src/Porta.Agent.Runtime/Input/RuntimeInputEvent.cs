namespace Porta.Agent.Runtime.Input;

internal enum InputEventKind
{
    Scroll = 0,
    Keyboard = 1,
    Focus = 2
}

internal readonly struct RuntimeInputEvent
{
    public InputEventKind Kind { get; }
    public ScrollEvent Scroll { get; }
    public KeyboardEvent Keyboard { get; }
    public FocusEvent Focus { get; }

    private RuntimeInputEvent(InputEventKind kind, ScrollEvent scroll, KeyboardEvent keyboard, FocusEvent focus)
    {
        Kind = kind;
        Scroll = scroll;
        Keyboard = keyboard;
        Focus = focus;
    }

    public static RuntimeInputEvent FromScroll(ScrollEvent evt) => new(InputEventKind.Scroll, evt, default, default);
    public static RuntimeInputEvent FromKeyboard(KeyboardEvent evt) => new(InputEventKind.Keyboard, default, evt, default);
    public static RuntimeInputEvent FromFocus(FocusEvent evt) => new(InputEventKind.Focus, default, default, evt);
}
