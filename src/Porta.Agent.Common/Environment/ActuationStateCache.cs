using Porta.Agent.Common.Control;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Environment;

public sealed class ActuationStateCache
{
    private readonly object _sync = new();
    private ActuationState _current = ActuationState.Safe;
    private long _lastTickTimestamp;

    public (ActuationState State, long TickTimestamp) Snapshot()
    {
        lock (_sync)
        {
            return (_current, _lastTickTimestamp);
        }
    }

    public ActuationState UpdateFromCommand(
        ActuationCommand command,
        long tickTimestamp,
        out ActuationState previous,
        out double maxAxisDelta)
    {
        ActuationState next = ActuationState.FromCommand(command);

        lock (_sync)
        {
            previous = _current;
            maxAxisDelta = ComputeMaxAxisDelta(previous, next);
            _current = next;
            _lastTickTimestamp = tickTimestamp;
            return _current;
        }
    }

    private static double ComputeMaxAxisDelta(in ActuationState previous, in ActuationState next)
    {
        double d1 = Math.Abs(next.StimulationIntensity - previous.StimulationIntensity);
        double d2 = Math.Abs(next.SceneDynamics - previous.SceneDynamics);
        double d3 = Math.Abs(next.VisualTemperatureShift - previous.VisualTemperatureShift);
        double d4 = Math.Abs(next.PacingModifier - previous.PacingModifier);
        double d5 = next.AuthorityLevel == previous.AuthorityLevel ? 0.0 : 1.0;
        return MathEx.Quantize(Math.Max(Math.Max(d1, d2), Math.Max(Math.Max(d3, d4), d5)), 6);
    }
}
