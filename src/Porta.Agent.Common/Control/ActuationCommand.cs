using System;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Control;

public enum AuthorityLevel
{
    Normal = 0,
    Reduced = 1,
    Safe = 2
}

public sealed class ActuationCommand
{
    public const int CurrentContractVersion = 2;

    public int ContractVersion { get; init; } = CurrentContractVersion;
    public double StimulationIntensity { get; init; }
    public double SceneDynamics { get; init; }
    public double VisualTemperatureShift { get; init; }
    public double PacingModifier { get; init; }
    public AuthorityLevel AuthorityLevel { get; init; } = AuthorityLevel.Normal;
    public string Note { get; init; } = string.Empty;

    public static ActuationCommand FromControlSignal(
        double normalizedControlSignal,
        AuthorityLevel authorityLevel = AuthorityLevel.Normal,
        string note = "policy_evaluate")
    {
        double signed = MathEx.Quantize(Math.Clamp(normalizedControlSignal, -1.0, 1.0));
        double intensity = MathEx.Quantize(MathEx.Clamp01(Math.Abs(signed)));

        return new ActuationCommand
        {
            StimulationIntensity = intensity,
            SceneDynamics = MathEx.Quantize(MathEx.Clamp01(0.35 + (0.65 * intensity))),
            VisualTemperatureShift = MathEx.Quantize(signed),
            PacingModifier = MathEx.Quantize(signed * 0.75),
            AuthorityLevel = authorityLevel,
            Note = note
        };
    }

    public static ActuationCommand Safe(string note = "safe")
    {
        return new ActuationCommand
        {
            StimulationIntensity = 0,
            SceneDynamics = 0,
            VisualTemperatureShift = 0,
            PacingModifier = 0,
            AuthorityLevel = AuthorityLevel.Safe,
            Note = note
        };
    }
}
