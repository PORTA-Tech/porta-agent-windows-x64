using System;
using System.Collections.Generic;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Processing;

public sealed class SignalBuffer
{
    private readonly RingBuffer<SignalSample> _buffer;

    public SignalBuffer(int capacity)
    {
        _buffer = new RingBuffer<SignalSample>(capacity);
    }

    public int Count => _buffer.Count;

    public void Add(long timestamp, double value)
    {
        _buffer.Add(new SignalSample(timestamp, value));
    }

    public void TrimBefore(long timestampInclusive)
    {
        _buffer.RemoveOlderThan(sample => sample.Timestamp >= timestampInclusive);
    }

    public IEnumerable<SignalSample> Enumerate()
    {
        return _buffer.Enumerate();
    }

    public IEnumerable<SignalSample> EnumerateWindow(long startInclusive, long endInclusive)
    {
        foreach (SignalSample sample in _buffer.Enumerate())
        {
            if (sample.Timestamp < startInclusive || sample.Timestamp > endInclusive)
            {
                continue;
            }

            yield return sample;
        }
    }
}
