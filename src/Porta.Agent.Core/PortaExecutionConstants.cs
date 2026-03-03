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

    public static class RobustNormalization
    {
        public const double DefaultStdFloor = 1e-6;
        public const double ZClampMin = -3.0;
        public const double ZClampMax = 3.0;
        public const int MetricReadyMinSamples = BaselineMinSamples;
        public const int RollingBoundsBufferSize = 600; // 50 minutes at 5s buckets.
        public const double BoundsP5Quantile = 0.05;
        public const double BoundsP95Quantile = 0.95;
        public const string BoundsQuantileMethod = "nearest_rank";
        public const int BoundsEstimatorVersion = 1;
        public const int StdFloorRegistryVersion = 1;
        public const string WinsorFallbackStrategy = "identity";
    }

    public static class PrimitiveNormalization
    {
        public const double SigmoidScale = 1.2;
        public const double EwmaAlpha = 0.35;
        public const double MissingChannelDecay = 0.90;
        public const double InitialSPrevNeutral = 0.5;
        public const string SPrevRuntimeStateKey = "primitives_prev_json";
        public const string LegacySPrevRuntimeStateKey = "primitive_computer:s_prev";
        public const string ContractVersion = "v1";
    }

    public static class DerivedSessionMetrics
    {
        public const string RuntimeStateKey = "derived_session:current";
        public const string ContractVersion = "v1";
        public const double IdleSecondsStableMax = 1.0;
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
