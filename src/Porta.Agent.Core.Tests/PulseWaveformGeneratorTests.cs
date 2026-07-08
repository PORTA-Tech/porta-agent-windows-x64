using Porta.Agent.Common.Analysis;

namespace Porta.Agent.Core.Tests;

// Phase 2a — the widget pulse line's pure signal model. The WPF strip just draws what this
// class produces, so these tests ARE the behavioral contract: height is the primary intensity
// channel, rate secondary, rhythm encodes state, frozen means dead-still.
public sealed class PulseWaveformGeneratorTests
{
    [Fact]
    public void BeatShape_IsBaselineOutsideTheBeatSegments()
    {
        Assert.Equal(0d, PulseWaveformGenerator.BeatShape(0.08), 3);
        Assert.Equal(0d, PulseWaveformGenerator.BeatShape(0.40), 3);
        Assert.Equal(0d, PulseWaveformGenerator.BeatShape(0.80), 3);
        Assert.Equal(0d, PulseWaveformGenerator.BeatShape(0.99), 3);
    }

    [Fact]
    public void BeatShape_RSpikePeaksAtOne()
    {
        // The R spike peak sits at phase 0.20.
        Assert.Equal(1.0d, PulseWaveformGenerator.BeatShape(0.20), 3);
        // And it is the global maximum over the whole beat.
        double max = 0d;
        for (double p = 0d; p < 1d; p += 0.001)
        {
            max = Math.Max(max, PulseWaveformGenerator.BeatShape(p));
        }

        Assert.Equal(1.0d, max, 2);
    }

    [Fact]
    public void BeatShape_HasSDipBelowBaseline()
    {
        double min = 0d;
        for (double p = 0d; p < 1d; p += 0.001)
        {
            min = Math.Min(min, PulseWaveformGenerator.BeatShape(p));
        }

        Assert.True(min < -0.30 && min > -0.45, $"S dip out of expected band: {min}");
    }

    [Fact]
    public void HeightIsThePrimaryIntensityChannel()
    {
        // Design-review requirement: amplitude must respond to intensity far more strongly
        // than rate does. Compare relative ranges (max/min ratios) for the focus rhythm.
        double ampRatio = PulseWaveformGenerator.Amplitude(PulseRhythm.Regular, 1d)
            / PulseWaveformGenerator.Amplitude(PulseRhythm.Regular, 0d);
        double rateRatio = PulseWaveformGenerator.BeatsPerSecond(PulseRhythm.Regular, 1d)
            / PulseWaveformGenerator.BeatsPerSecond(PulseRhythm.Regular, 0d);

        Assert.True(ampRatio > rateRatio * 2d,
            $"Height must dominate: ampRatio={ampRatio:F2}, rateRatio={rateRatio:F2}");
    }

    [Theory]
    [InlineData(PulseRhythm.Regular)]
    [InlineData(PulseRhythm.Irregular)]
    [InlineData(PulseRhythm.Tremor)]
    public void AmplitudeAndRate_AreMonotonicInIntensity(PulseRhythm rhythm)
    {
        Assert.True(PulseWaveformGenerator.Amplitude(rhythm, 0.9) > PulseWaveformGenerator.Amplitude(rhythm, 0.1));
        Assert.True(PulseWaveformGenerator.BeatsPerSecond(rhythm, 0.9) > PulseWaveformGenerator.BeatsPerSecond(rhythm, 0.1));
    }

    [Fact]
    public void Flatline_IsNearlyFlat()
    {
        Assert.True(PulseWaveformGenerator.Amplitude(PulseRhythm.Flatline, 1d) <= 0.10);
        // Even at max intensity the away line barely moves.
        var gen = new PulseWaveformGenerator(seed: 7);
        double maxAbs = 0d;
        for (int i = 0; i < 2000; i++)
        {
            maxAbs = Math.Max(maxAbs, Math.Abs(gen.Advance(0.15, PulseRhythm.Flatline, 1d)));
        }

        Assert.True(maxAbs <= 0.10, $"Flatline moved too much: {maxAbs}");
    }

    [Fact]
    public void Frozen_NeverMovesAndNeverAdvances()
    {
        var gen = new PulseWaveformGenerator(seed: 1);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(0d, gen.Advance(0.15, PulseRhythm.Frozen, 1d));
        }

        // After being frozen, the phase has not drifted: the first regular sample matches a
        // fresh generator's first regular sample (same seed → same jitter stream).
        var fresh = new PulseWaveformGenerator(seed: 1);
        Assert.Equal(fresh.Advance(0.15, PulseRhythm.Regular, 0.5), gen.Advance(0.15, PulseRhythm.Regular, 0.5), 6);
    }

    [Theory]
    [InlineData(PulseRhythm.Regular, 0d)]
    [InlineData(PulseRhythm.Regular, 1d)]
    [InlineData(PulseRhythm.Irregular, 0.5d)]
    [InlineData(PulseRhythm.Tremor, 1d)]
    [InlineData(PulseRhythm.Flatline, 0.3d)]
    public void Advance_StaysWithinDocumentedBounds(PulseRhythm rhythm, double intensity)
    {
        var gen = new PulseWaveformGenerator(seed: 42);
        for (int i = 0; i < 5000; i++)
        {
            double v = gen.Advance(0.15, rhythm, intensity);
            Assert.True(v >= PulseWaveformGenerator.MinSample && v <= PulseWaveformGenerator.MaxSample,
                $"Out of bounds at step {i}: {v}");
        }
    }

    [Fact]
    public void Advance_ToleratesGarbageInputs()
    {
        var gen = new PulseWaveformGenerator(seed: 3);
        Assert.Equal(0d, gen.Advance(double.NaN, PulseRhythm.Regular, 0.5));
        Assert.Equal(0d, gen.Advance(-1d, PulseRhythm.Regular, 0.5));
        double v1 = gen.Advance(0.15, PulseRhythm.Regular, double.NaN);
        double v2 = gen.Advance(0.15, PulseRhythm.Regular, double.PositiveInfinity);
        Assert.True(double.IsFinite(v1) && double.IsFinite(v2));
    }

    [Fact]
    public void Irregular_HasVaryingBeatGaps_RegularDoesNot()
    {
        // Measure the spacing between R-spike peaks over a long run for both rhythms.
        static List<int> PeakSpacings(PulseRhythm rhythm)
        {
            // Fine sampling (dt=0.02 → ~42 samples/beat) so a sample always lands near the
            // R-spike peak; threshold 0.3 sits far above the tallest P/T bump (~0.13) and far
            // below the shallowest sampled R spike (~0.43) — no quantization flakiness.
            var gen = new PulseWaveformGenerator(seed: 11);
            var samples = new List<double>();
            for (int i = 0; i < 8000; i++)
            {
                samples.Add(gen.Advance(0.02, rhythm, 0.5));
            }

            var peaks = new List<int>();
            for (int i = 1; i < samples.Count - 1; i++)
            {
                if (samples[i] > 0.3 && samples[i] >= samples[i - 1] && samples[i] >= samples[i + 1])
                {
                    if (peaks.Count == 0 || i - peaks[^1] > 10)
                    {
                        peaks.Add(i);
                    }
                }
            }

            var spacings = new List<int>();
            for (int i = 1; i < peaks.Count; i++)
            {
                spacings.Add(peaks[i] - peaks[i - 1]);
            }

            return spacings;
        }

        static double RelativeSpread(List<int> spacings)
        {
            double mean = spacings.Average();
            double variance = spacings.Average(s => (s - mean) * (s - mean));
            return Math.Sqrt(variance) / mean;
        }

        List<int> regular = PeakSpacings(PulseRhythm.Regular);
        List<int> irregular = PeakSpacings(PulseRhythm.Irregular);

        Assert.True(regular.Count > 10 && irregular.Count > 10, "not enough beats detected");
        Assert.True(RelativeSpread(regular) < 0.05, $"regular rhythm drifted: {RelativeSpread(regular):F3}");
        Assert.True(RelativeSpread(irregular) > 0.10, $"irregular rhythm too steady: {RelativeSpread(irregular):F3}");
    }

    [Fact]
    public void SameSeed_IsDeterministic()
    {
        var a = new PulseWaveformGenerator(seed: 99);
        var b = new PulseWaveformGenerator(seed: 99);
        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(a.Advance(0.13, PulseRhythm.Irregular, 0.7), b.Advance(0.13, PulseRhythm.Irregular, 0.7), 10);
        }
    }
}
