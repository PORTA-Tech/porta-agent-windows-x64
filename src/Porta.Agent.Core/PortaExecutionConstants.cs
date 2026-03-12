namespace Porta.Agent.Core;

public static class PortaExecutionConstants
{
    public const int BaselineMinSamples = 60;
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
        public const double ReadyCoverageThreshold = 0.70;
        public const int RecentQualityBuckets = 24; // 2 minutes at 5s buckets.
        public const double RecentQualityThreshold = 0.65;
        public const int ProvisionalMetricMinSamples = 24;
        public const double ProvisionalCoverageThreshold = 0.45;
        public const int ProvisionalRecentQualityBuckets = 12; // 1 minute at 5s buckets.
        public const double ProvisionalRecentQualityThreshold = 0.55;
        public const double BootstrappingGuidanceQualityFloor = 0.45;
        public const double ColdStartConfidenceCap = 0.55;
        public const double ProvisionalConfidenceCapMin = 0.60;
        public const double ProvisionalConfidenceCap = 0.75;
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
        public const double GapResetSeconds = 180.0;
        public const double IdleSecondsStableMax = 10.0;
        public const double IdleAccumulateMaxSeconds = 10.0;
        public const double IdlePauseMaxSeconds = 60.0;
        public const double IdleDecayMaxSeconds = 180.0;
        public const double IdleResetSeconds = 180.0;
        public const double IdleDecayPerBucketSeconds = 2.5;
        public const double ContextShiftGraceSeconds = 60.0;
        public const double ContextShiftResetSeconds = 300.0;
        public const double ContextShiftDecayPerBucketSeconds = 5.0;
        public const double InstabilityResetSeconds = 180.0;
        public const double InstabilityDecayPerBucketSeconds = 5.0;
        public const double KeepScore = 1.0;
        public const double PauseScore = 0.0;
        public const double DecayScore = 0.35;
        public const double ResetScore = 0.0;
        public const double FocusMissingConfidencePenalty = 0.35;
        public const double KeyboardMissingConfidencePenalty = 0.08;
        public const double MouseMissingConfidencePenalty = 0.08;
        public const double BothInteractionsMissingConfidencePenalty = 0.20;
        public const double IdleMissingConfidencePenalty = 0.12;
        public const double PartialConfidencePenalty = 0.05;
        public const double NoInteractionConfidencePenalty = 0.05;
    }

    public static class OverloadStabilization
    {
        public const double ThresholdOn = 0.72;
        public const double ThresholdOff = 0.58;
        public const int DwellOnSeconds = 60;
        public const int DwellOffSeconds = 45;
        public const double ConfidenceMin = 0.55;
        public const int GapAllowSeconds = 20;
        public const double LowQualityPauseThreshold = 0.35;
        public const int LowQualityDwellDecayMs = 5000;
        public const string RuntimeStateKey = "overload_stabilization:current";
        public const string ActiveRuntimeStateKey = "overload_active";
        public const string ContractVersion = "v1";
    }

    public static class OverloadInference
    {
        public const double WeightF = 0.45;
        public const double WeightV = 0.30;
        public const double WeightI = 0.25;
        public const double AgreementPartialPenalty = 0.80;
    }

    public static class BreakPolicy
    {
        public const int MicrobreakWindowSeconds = 35 * 60;
        public const int MicrobreakEffectiveFocusSecondsLowOverload = 30 * 60;
        public const int MicrobreakEffectiveFocusSecondsHighOverload = 25 * 60;
        public const int ShortBreakWindowSeconds = 75 * 60;
        public const int ShortBreakEffectiveFocusSecondsLowOverload = 60 * 60;
        public const int ShortBreakEffectiveFocusSecondsHighOverload = 45 * 60;
        public const int CooldownSeconds = 1800;
        public const long CooldownWindowMs = CooldownSeconds * 1000L;
        public const double ConfidenceMin = 0.50;
        public const double HighOverloadLikelihood = OverloadStabilization.ThresholdOn;
        public const double DeliveryBoundaryIdleMinSeconds = 2.0;
        public const double DeliveryBoundaryIdleMaxSeconds = 30.0;
        public const double DeliveryBoundaryTypingQuietMax = 0.35;
        public const double DeliveryBoundaryKeyboardBurstQuietMax = 0.20;
        public const string RuntimeStateKey = "break_policy:cooldown";
        public const string ContractVersion = "v2";
    }

    public static class DeepWorkDetection
    {
        public const int SustainedAttentionSeconds = 900;
        public const double ConfidenceMin = 0.65;
        public const double FragmentationMax = 0.50;
        public const double VolatilityMax = 0.50;
        public const double IntensityMin = 0.40;
        public const double IntensityMax = 0.75;
        public const int DwellOnSeconds = 120;
        public const int DwellOffSeconds = 60;
        public const int GapAllowSeconds = 10;
        public const string RuntimeStateKey = "deep_work_stabilization:current";
        public const string ActiveRuntimeStateKey = "deep_work_active";
        public const string ContractVersion = "v1";
    }

    public static class NotificationSuppression
    {
        public const string RuntimeStateKey = "deep_work_notifications:current";
        public const string ContractVersion = "v1";
        public const int FailureBackoffSeconds = 300;
        public const int EnableCooldownSeconds = 600;
        public const int MinOnSeconds = 120;
    }

    public static class BrightnessAdjustment
    {
        public const string RuntimeStateKey = "overload_brightness:current";
        public const string ContractVersion = "v1";
        public const int ReductionDelta = 5;
        public const int MinSafeBrightness = 20;
        public const int RampStepPerBucket = 1;
        public const int CooldownSeconds = 600;
        public const int FailureBackoffSeconds = 600;
    }
}
