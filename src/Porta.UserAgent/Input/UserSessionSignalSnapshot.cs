namespace Porta.UserAgent.Input;

internal readonly record struct UserSessionSignalSnapshot(
    int KeyboardKeydownCount,
    int KeyboardBackspaceCount,
    int MouseWheelDelta,
    double MouseMoveDx,
    double MouseMoveDy,
    DateTimeOffset? LastInputEventUtc);
