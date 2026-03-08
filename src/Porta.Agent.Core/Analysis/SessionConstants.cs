namespace Porta.Agent.Core.Analysis;

internal static class SessionConstants
{
    internal static class SustainedAttention
    {
        // Real desktop work often includes small navigation bursts and brief context peeks.
        public const int FocusSwitchesLowMax = 2;
        public const int FocusSwitchesSpikeMin = 5;

        // Entropy thresholds are intentionally wider so short tab/window checks do not kill continuity.
        public const double EntropyStableMax = 1.80;
        public const double EntropySpikeMin = 2.50;

        public const double QualityDecentMin = 0.60;
    }

    internal static class IdleFragmentation
    {
        public const double FrequencyMaxPerBucket = 3.0;
        public const double RatioMaxPerBucket = 0.60;
        public const double FrequencyWeight = 0.50;
        public const double RatioWeight = 0.50;
        public const double EwmaAlpha = 0.35;
        public const double MissingDecay = 0.90;
    }
}
