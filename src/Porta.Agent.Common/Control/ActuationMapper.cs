using System;

namespace Porta.Agent.Common.Control;

public static class ActuationMapper
{
    public static bool TryMapToWheelDelta(ActuationCommand command, out int wheelDelta, out string note)
    {
        if (command.ContractVersion != ActuationCommand.CurrentContractVersion)
        {
            wheelDelta = 0;
            note = $"unsupported_contract_v{command.ContractVersion}";
            return false;
        }

        // Environment layer performs a deterministic unit transcode only.
        double signed = Math.Clamp(command.PacingModifier, -1.0, 1.0);
        wheelDelta = SafetyLimits.ClampInjectionDelta(
            (int)Math.Round(signed * SafetyLimits.MaxAbsInjectionDelta, MidpointRounding.AwayFromZero));
        note = "ok";
        return true;
    }
}
