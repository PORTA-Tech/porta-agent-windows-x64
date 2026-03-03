namespace Porta.Agent.Core;

public static class PortaExecutionConstants
{
    public const int BaselineMinSamples = 120;
    public static class BaselineUpdateGating
    {
        public const double MinQualityScore = 0.60;
        public const int ZScoreMinSamples = 30;
        public const double ZScoreMax = 5.0;
        public const double StdEpsilon = 1e-6;
        public const string StdConvention = "population";
        public const string NumericType = "float64";
    }

    public static class BaselineReadiness
    {
        public const double ReadyCoverageThreshold = 0.80;
        public const int RecentQualityBuckets = 24; // 2 minutes at 5s buckets.
        public const double RecentQualityThreshold = 0.70;
        public const double ColdStartConfidenceCap = 0.45;
    }

    public static class OverloadStabilization
    {
        public const double ThresholdOn = 0.80;
        public const double ThresholdOff = 0.65;
        public const int DwellOnSeconds = 120;
        public const int DwellOffSeconds = 60;
        public const double ConfidenceMin = 0.60;
        public const int GapAllowSeconds = 10;
    }

    public static class BreakPolicy
    {
        public const int SustainedAttentionSeconds = 3600;
        public const int CooldownSeconds = 1800;
    }

    public static class DeepWorkDetection
    {
        public const int SustainedAttentionSeconds = 900;
        public const double ConfidenceMin = 0.65;
    }
}
