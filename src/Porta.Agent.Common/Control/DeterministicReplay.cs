using System.Collections.Generic;
using System.Linq;
using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Common.Control;

public static class DeterministicReplay
{
    public sealed class ReplaySample
    {
        public long Timestamp { get; init; }
        public int ScrollDelta { get; init; }
        public bool KeyDown { get; init; }
        public bool FocusChanged { get; init; }
    }

    public static IReadOnlyList<ActuationCommand> Run(
        DeterministicControlEngine engine,
        ControlParameters parameters,
        IReadOnlyList<ReplaySample> samples)
    {
        var outputs = new List<ActuationCommand>(samples.Count);
        var ordered = samples
            .Select((sample, index) => (sample, index))
            .OrderBy(x => x.sample.Timestamp)
            .ThenBy(x => x.index)
            .Select(x => x.sample);
        long nextTick = 0;
        foreach (ReplaySample sample in ordered)
        {
            if (sample.ScrollDelta != 0)
            {
                engine.AddScrollSample(sample.Timestamp, sample.ScrollDelta);
            }

            if (sample.KeyDown)
            {
                engine.AddKeyboardSample(sample.Timestamp, isKeyDown: true);
            }

            if (sample.FocusChanged)
            {
                engine.AddFocusSample(sample.Timestamp);
            }

            if (nextTick == 0)
            {
                nextTick = sample.Timestamp;
            }

            while (engine.IsTickDue(sample.Timestamp, nextTick))
            {
                outputs.Add(engine.EvaluateAtTimestamp(nextTick, parameters));
                nextTick += engine.TickIntervalTicks;
            }
        }

        return outputs;
    }
}
