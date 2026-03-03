namespace Porta.Agent.Core.Analysis;

internal static class SessionConstants
{
    internal static class SustainedAttention
    {
        // C3 pinned threshold: "focus switches low" when focus_switch_count <= 1 per bucket.
        public const int FocusSwitchesLowMax = 1;
        public const int FocusSwitchesSpikeMin = 3;

        // C4 pinned thresholds (absolute entropy in v1).
        public const double EntropyStableMax = 1.50;
        public const double EntropySpikeMin = 2.00;

        // C5 pinned threshold: quality is "decent" for SA increment fallback during cold start.
        public const double QualityDecentMin = 0.70;
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
