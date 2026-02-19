using System;
using Porta.Agent.Common.Control;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Environment;

public static class AuthorityGate
{
    public static AuthorityGatedState Apply(ActuationState input)
    {
        if (input.AuthorityLevel == AuthorityLevel.Safe)
        {
            return new AuthorityGatedState(ActuationState.Safe, UseSafeProfile: true);
        }

        if (input.AuthorityLevel == AuthorityLevel.Reduced)
        {
            const double scale = 0.6;
            return new AuthorityGatedState(
                new ActuationState(
                    StimulationIntensity: MathEx.Quantize(MathEx.Clamp01(input.StimulationIntensity * scale), 6),
                    SceneDynamics: MathEx.Quantize(MathEx.Clamp01(input.SceneDynamics * scale), 6),
                    VisualTemperatureShift: MathEx.Quantize(Math.Clamp(input.VisualTemperatureShift * scale, -1.0, 1.0), 6),
                    PacingModifier: MathEx.Quantize(Math.Clamp(input.PacingModifier * scale, -1.0, 1.0), 6),
                    AuthorityLevel: AuthorityLevel.Reduced),
                UseSafeProfile: false);
        }

        return new AuthorityGatedState(input, UseSafeProfile: false);
    }
}
