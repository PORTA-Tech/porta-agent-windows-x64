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
        public const double FocusEpisodeSettleSeconds = 15.0;
        public const double PassiveFocusAccumulateMaxSeconds = 20.0;
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

    public static class DistractionEpisodes
    {
        public const double AwaySegmentStartSeconds = 60.0;
        public const double OffTaskDistractionStartSeconds = 90.0;
        public const double FragmentationDistractionStartSeconds = 120.0;
        public const double FragmentationHighAppCountMin = 4.0;

        // Micro-burst (rapid repeated switching) detection
        public const double MicroBurstEwmaAlpha = 0.25;
        public const double MicroBurstSwitchRateThreshold = 2.0;
        public const double MicroBurstMinTriggerSeconds = 60.0;
        public const int    MicroBurstMinSwitchesPerBucket = 2;

        // Ping-pong (A→B→A→B→A→B cycle) detection
        public const int    PingPongMinCycles = 3;
        public const double PingPongWindowSeconds = 180.0;
        public const double PingPongMaxDwellPerHopSeconds = 45.0;

        // Never-settled (active but never locks onto an app) detection
        public const double NeverSettledActiveInteractionMinutes = 20.0;
        public const double NeverSettledCandidateMaxSeconds = 15.0;

        // Shallow-dwell (grazing — rolling avg dwell below ceiling) detection
        public const double ShallowDwellEwmaAlpha = 0.30;
        public const double ShallowDwellCeilingSeconds = 40.0;
        public const int    ShallowDwellMinSwitchesPerBucket = 2;
        public const double ShallowDwellMinTriggerSeconds = 90.0;

        // Focus-departure (time away from anchor app) detection
        public const double FocusDepartureThresholdSeconds = 600.0;
        public const int    FocusDepartureReturnBuckets = 2;

        // Anchor-app locking and migration
        public const double AnchorCandidateRequiredSeconds = 120.0;
        public const double AnchorMigrationThresholdSeconds = 240.0;

        // Phase 2 — Category-based patterns
        public const double PassiveConsumptionMinTriggerSeconds = 180.0;
        public const int    CommMonitoringMinReturns = 3;
        public const double CommMonitoringWindowSeconds = 600.0;

        // Phase 3 — Unconditional Gaming / SocialMedia patterns
        public const double SocialMediaDistractionMinSeconds      = 120.0;  // 2 min continuous
        public const int    SocialMediaLoopMinReturns             = 3;
        public const double SocialMediaLoopWindowSeconds          = 900.0;  // 15 min
        public const double GamingLauncherMinSeconds              = 180.0;  // 3 min
        public const double GamingActiveMinSeconds                = 60.0;   // 60 sec — no grace

        // Phase 3 — Session-level absence / start-quality patterns
        public const double FocusCategoryAbsentMinSeconds         = 1200.0; // 20 min
        public const double SessionStartQualityWindowSeconds      = 600.0;  // 10 min
        public const double SessionStartQualityMinDistractionRatio = 0.6;   // 60 % distraction
    }

    public static class TimeOfDayMultipliers
    {
        public const int    EarlyMorningStart = 6;
        public const int    EarlyMorningEnd = 9;
        public const double EarlyMorningMultiplier = 1.0;

        public const int    MorningStart = 9;
        public const int    MorningEnd = 12;
        public const double MorningMultiplier = 0.85;

        public const int    LunchStart = 12;
        public const int    LunchEnd = 14;
        public const double LunchMultiplier = 1.10;

        public const int    AfternoonStart = 14;
        public const int    AfternoonEnd = 17;
        public const double AfternoonMultiplier = 0.90;

        public const int    EveningStart = 17;
        public const int    EveningEnd = 20;
        public const double EveningMultiplier = 1.15;

        public const double LateEveningMultiplier = 1.25; // 20:00 onward
    }

    public static class OverloadStabilization
    {
        // Reviewed after the pressure split: overload activation stays
        // intentionally conservative and separate from the more expressive
        // user-facing pressure signal.
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
        public const double PressureDominantComponentWeight = 0.60;
        public const double PressureMeanComponentWeight = 0.40;
        public const int PressureAbsoluteSwitchSoftThreshold = 3;
        public const int PressureAbsoluteSwitchHardThreshold = 5;
        public const double PressureAbsoluteEntropySoftThreshold = 1.80;
        public const double PressureAbsoluteEntropyHardThreshold = 2.50;
        public const double PressureAbsoluteActiveAppsSoftThreshold = 4.0;
        public const double PressureAbsoluteActiveAppsHardThreshold = 5.0;
        public const double PressureAbsoluteSoftFloor = 0.58;
        public const double PressureAbsoluteHardFloor = 0.72;
    }

    public static class BreakPolicy
    {
        public const int ShortBreakWindowSeconds = 30 * 60;
        public const int ShortBreakEffectiveFocusSecondsLowPressure = 25 * 60;
        public const int ShortBreakEffectiveFocusSecondsHighPressure = 20 * 60;
        public const int LongBreakWindowSeconds = 75 * 60;
        public const int LongBreakEffectiveFocusSecondsLowPressure = 60 * 60;
        public const int LongBreakEffectiveFocusSecondsHighPressure = 50 * 60;
        public const int CooldownSeconds = 1800;
        public const long CooldownWindowMs = CooldownSeconds * 1000L;
        public const double ConfidenceMin = 0.50;
        public const double HighPressureLevel = OverloadStabilization.ThresholdOn;
        public const double DeliveryBoundaryIdleMinSeconds = 2.0;
        public const double DeliveryBoundaryIdleMaxSeconds = 30.0;
        public const double DeliveryBoundaryTypingQuietMax = 0.35;
        public const double DeliveryBoundaryKeyboardBurstQuietMax = 0.20;
        public const string RuntimeStateKey = "break_policy:cooldown";
        public const string ContractVersion = "v2";
    }

    public static class DeepWorkDetection
    {
        public const int SustainedAttentionSeconds = 600;
        public const double ConfidenceMin = 0.55;
        public const double FragmentationMax = 0.50;
        public const double VolatilityMax = 0.50;
        public const double IntensityMin = 0.40;
        public const double IntensityMax = 0.75;
        public const int DwellOnSeconds = 60;
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
