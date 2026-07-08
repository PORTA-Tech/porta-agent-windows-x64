namespace Porta.Agent.Common.Analysis;

/// <summary>
/// Phase 2a — the widget's EKG-style pulse line, as PURE math (no WPF, no timers) so the
/// signal model is unit-testable. The widget calls <see cref="PulseWaveformGenerator.Advance"/>
/// on its render tick and draws the returned samples as a scrolling polyline.
///
/// Signal model (design-reviewed):
///  - Spike HEIGHT is the PRIMARY intensity channel — typing bursts, fast scrolling, and rapid
///    window switching make the beats visibly taller first.
///  - Beat RATE is the secondary channel — it rises with intensity over a narrower range.
///  - The RHYTHM encodes the state: regular for focus, irregular (arrhythmic) for distraction,
///    fast with baseline tremor for overload, near-flatline for away, and frozen (no output
///    change) for paused/limited/stale — a dead line must mean "not watching".
/// </summary>
public enum PulseRhythm
{
    /// <summary>Focus / deep work — steady, regular beats.</summary>
    Regular = 0,

    /// <summary>Distraction — beats land at irregular intervals (arrhythmic).</summary>
    Irregular = 1,

    /// <summary>Overload — faster, taller, with a slight baseline tremor.</summary>
    Tremor = 2,

    /// <summary>Away — near-flatline with rare, tiny blips.</summary>
    Flatline = 3,

    /// <summary>Paused / limited / stale — the line does not move at all.</summary>
    Frozen = 4
}

public sealed class PulseWaveformGenerator
{
    /// <summary>Samples returned by <see cref="Advance"/> always stay inside this range.</summary>
    public const double MinSample = -0.55;
    public const double MaxSample = 1.25;

    private readonly Random _random;
    private double _phase;              // position within the current beat, 0..1
    private double _currentGapFactor = 1.0; // stretches the current beat interval (rhythm jitter)

    public PulseWaveformGenerator(int? seed = null)
    {
        _random = seed is int s ? new Random(s) : new Random();
    }

    /// <summary>
    /// Advances the waveform by <paramref name="dtSeconds"/> and returns the next sample.
    /// 0 is the baseline; positive is up. Frozen always returns 0 and does not advance.
    /// </summary>
    public double Advance(double dtSeconds, PulseRhythm rhythm, double intensity)
    {
        if (rhythm == PulseRhythm.Frozen
            || !double.IsFinite(dtSeconds)
            || dtSeconds <= 0d)
        {
            return 0d;
        }

        double clamped = Clamp01(intensity);
        double beatsPerSecond = BeatsPerSecond(rhythm, clamped);
        _phase += dtSeconds * beatsPerSecond / Math.Max(0.25, _currentGapFactor);
        if (_phase >= 1d)
        {
            _phase -= Math.Floor(_phase);
            _currentGapFactor = NextGapFactor(rhythm);
        }

        double amplitude = Amplitude(rhythm, clamped);
        double noise = rhythm == PulseRhythm.Tremor
            ? (_random.NextDouble() - 0.5) * 0.06
            : 0d;

        double sample = BeatShape(_phase) * amplitude + noise;
        return Math.Clamp(sample, MinSample, MaxSample);
    }

    /// <summary>
    /// Beat rate in beats/second — the SECONDARY intensity channel (narrow relative range;
    /// see <see cref="Amplitude"/> for the primary one).
    /// </summary>
    public static double BeatsPerSecond(PulseRhythm rhythm, double intensity)
    {
        double i = Clamp01(intensity);
        return rhythm switch
        {
            PulseRhythm.Regular => 0.80 + 0.70 * i,   // ~48..90 bpm
            PulseRhythm.Irregular => 0.90 + 0.60 * i, // ~54..90 bpm, jittered gaps
            PulseRhythm.Tremor => 1.30 + 0.90 * i,    // ~78..132 bpm
            PulseRhythm.Flatline => 0.35,             // rare blips
            _ => 0d
        };
    }

    /// <summary>
    /// Spike amplitude 0..~1.15 — the PRIMARY intensity channel. Its relative range
    /// (max/min ratio) is deliberately much wider than the rate's, so height is what
    /// the eye reads first when the user works harder.
    /// </summary>
    public static double Amplitude(PulseRhythm rhythm, double intensity)
    {
        double i = Clamp01(intensity);
        return rhythm switch
        {
            PulseRhythm.Regular => 0.22 + 0.78 * i,
            PulseRhythm.Irregular => 0.22 + 0.63 * i,
            PulseRhythm.Tremor => 0.45 + 0.70 * i,
            PulseRhythm.Flatline => 0.06,
            _ => 0d
        };
    }

    /// <summary>
    /// One heartbeat as a function of phase 0..1 — a P-QRS-T-like piecewise curve.
    /// Baseline is 0; the R spike peaks at 1.0; the S dip reaches -0.38.
    /// </summary>
    public static double BeatShape(double phase)
    {
        double p = phase - Math.Floor(phase);
        if (p < 0.06)
        {
            return 0.12 * Math.Sin(p / 0.06 * Math.PI);          // P bump
        }

        if (p < 0.10)
        {
            return 0d;
        }

        if (p < 0.14)
        {
            return -0.22 * ((p - 0.10) / 0.04);                   // Q dip
        }

        if (p < 0.20)
        {
            return -0.22 + 1.22 * ((p - 0.14) / 0.06);            // R spike up to 1.0
        }

        if (p < 0.26)
        {
            return 1.0 - 1.38 * ((p - 0.20) / 0.06);              // fall to S (-0.38)
        }

        if (p < 0.32)
        {
            return -0.38 + 0.38 * ((p - 0.26) / 0.06);            // recover to baseline
        }

        if (p < 0.46)
        {
            return 0d;
        }

        if (p < 0.60)
        {
            return 0.20 * Math.Sin((p - 0.46) / 0.14 * Math.PI);  // T bump
        }

        return 0d;
    }

    // Gap factor stretches/compresses the NEXT beat interval — this is what makes
    // distraction read as arrhythmic while focus stays metronome-regular.
    private double NextGapFactor(PulseRhythm rhythm)
    {
        return rhythm switch
        {
            PulseRhythm.Irregular => 1.0 + (_random.NextDouble() * 2d - 1d) * 0.45,
            PulseRhythm.Tremor => 1.0 + (_random.NextDouble() * 2d - 1d) * 0.10,
            PulseRhythm.Flatline => 1.0 + _random.NextDouble() * 1.5,
            _ => 1.0
        };
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }
}
