namespace Porta.Agent.Common.Control;

public sealed class ControlOutput
{
    public bool ShouldInject { get; init; }
    public int InjectionDelta { get; init; }
    public int DelayMs { get; init; }
    public string Note { get; init; } = string.Empty;

    public static ControlOutput None(string note = "no_actuation")
    {
        return new ControlOutput
        {
            ShouldInject = false,
            InjectionDelta = 0,
            DelayMs = 0,
            Note = note
        };
    }
}
