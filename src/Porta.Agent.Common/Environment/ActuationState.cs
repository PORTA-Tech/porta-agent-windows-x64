using Porta.Agent.Common.Control;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Environment;

public readonly record struct ActuationState(
    double StimulationIntensity,
    double SceneDynamics,
    double VisualTemperatureShift,
    double PacingModifier,
    AuthorityLevel AuthorityLevel)
{
    public static ActuationState FromCommand(ActuationCommand command)
    {
        return new ActuationState(
            StimulationIntensity: MathEx.Quantize(MathEx.Clamp01(command.StimulationIntensity), 6),
            SceneDynamics: MathEx.Quantize(MathEx.Clamp01(command.SceneDynamics), 6),
            VisualTemperatureShift: MathEx.Quantize(Math.Clamp(command.VisualTemperatureShift, -1.0, 1.0), 6),
            PacingModifier: MathEx.Quantize(Math.Clamp(command.PacingModifier, -1.0, 1.0), 6),
            AuthorityLevel: command.AuthorityLevel);
    }

    public static ActuationState Safe => new(
        StimulationIntensity: 0,
        SceneDynamics: 0,
        VisualTemperatureShift: 0,
        PacingModifier: 0,
        AuthorityLevel: AuthorityLevel.Safe);
}
