using System;
using Porta.Agent.Common.State;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Processing;

public sealed class StateAggregator
{
    private readonly SignalBuffer _scroll;
    private readonly SignalBuffer _keyboard;
    private readonly SignalBuffer _focus;
    private readonly double _windowSeconds;
    private readonly double _emaAlpha;
    private double _lastComposite;

    public StateAggregator(int capacityPerSignal = 4096, double windowSeconds = 5.0, double emaAlpha = 0.25)
    {
        _scroll = new SignalBuffer(capacityPerSignal);
        _keyboard = new SignalBuffer(capacityPerSignal);
        _focus = new SignalBuffer(capacityPerSignal);
        _windowSeconds = Math.Max(1.0, windowSeconds);
        _emaAlpha = MathEx.Clamp01(emaAlpha);
    }

    public void AddScroll(long timestamp, double normalizedDelta)
    {
        _scroll.Add(timestamp, normalizedDelta);
    }

    public void AddKeyboard(long timestamp, bool isKeyDown)
    {
        if (isKeyDown)
        {
            _keyboard.Add(timestamp, 1.0);
        }
    }

    public void AddFocusChange(long timestamp)
    {
        _focus.Add(timestamp, 1.0);
    }

    public AggregatedState Aggregate(long timestamp, long frequency)
    {
        long windowTicks = Time.SecondsToTicks(_windowSeconds, frequency);
        long start = Math.Max(0, timestamp - windowTicks);
        _scroll.TrimBefore(start);
        _keyboard.TrimBefore(start);
        _focus.TrimBefore(start);

        WindowStats scrollStats = ComputeWindowStats(_scroll, start, timestamp);
        WindowStats keyboardStats = ComputeWindowStats(_keyboard, start, timestamp);
        WindowStats focusStats = ComputeWindowStats(_focus, start, timestamp);

        double windowSec = MathEx.Quantize(Math.Max(0.001, Time.ToSeconds(windowTicks, frequency)));
        double scrollRate = MathEx.Quantize(scrollStats.Sum / windowSec);
        double keyboardRate = MathEx.Quantize(keyboardStats.Count / windowSec);
        double focusSwitchRate = MathEx.Quantize(focusStats.Count / windowSec);
        double interactionVariance = MathEx.Quantize(ComputeVariance(scrollStats));
        double focusStability = MathEx.Quantize(1.0 - MathEx.Clamp01(focusSwitchRate / 2.0));
        double compositeRaw = MathEx.Quantize(
            MathEx.Clamp01((0.55 * Math.Abs(scrollRate)) + (0.35 * MathEx.Clamp01(keyboardRate / 8.0)) + (0.10 * (1.0 - focusStability))));
        double composite = MathEx.Quantize(MathEx.Ema(_lastComposite, compositeRaw, _emaAlpha));
        _lastComposite = composite;

        int presentSignals = 0;
        if (scrollStats.Count > 0) presentSignals++;
        if (keyboardStats.Count > 0) presentSignals++;
        if (focusStats.Count > 0) presentSignals++;
        double signalConfidence = MathEx.Quantize(presentSignals / 3.0);

        return new AggregatedState
        {
            Timestamp = timestamp,
            ScrollRate = scrollRate,
            KeyboardRate = keyboardRate,
            FocusStability = focusStability,
            InteractionVariance = interactionVariance,
            CompositeArousalEstimate = composite,
            SignalConfidence = signalConfidence
        };
    }

    private static WindowStats ComputeWindowStats(SignalBuffer buffer, long startInclusive, long endInclusive)
    {
        int count = 0;
        double sum = 0;
        double sumSq = 0;

        foreach (SignalSample sample in buffer.EnumerateWindow(startInclusive, endInclusive))
        {
            count++;
            sum += sample.Value;
            sumSq += sample.Value * sample.Value;
        }

        return new WindowStats(count, sum, sumSq);
    }

    private static double ComputeVariance(WindowStats stats)
    {
        if (stats.Count < 2)
        {
            return 0;
        }

        double mean = stats.Sum / stats.Count;
        return Math.Max(0, (stats.SumSquares / stats.Count) - (mean * mean));
    }

    private readonly struct WindowStats
    {
        public int Count { get; }
        public double Sum { get; }
        public double SumSquares { get; }

        public WindowStats(int count, double sum, double sumSquares)
        {
            Count = count;
            Sum = sum;
            SumSquares = sumSquares;
        }
    }
}
