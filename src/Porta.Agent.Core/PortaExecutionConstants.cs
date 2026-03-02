namespace Porta.Agent.Core;

public static class PortaExecutionConstants
{
    public const int BucketSeconds = 5;
    public const int BucketMs = 5000;
    public const int MaxLateMs = 2000;
    public const int FinalizeDelayMs = 2000;
    public const int SkewLimitMs = 10000;
    public const long EpochAnchorMs = 1704067200000L; // 2024-01-01T00:00:00.000Z
    public const int BaselineMinSamples = 120;

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
