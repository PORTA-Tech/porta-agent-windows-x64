namespace Porta.Agent.Core;

public static class PortaExecutionConstants
{
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
