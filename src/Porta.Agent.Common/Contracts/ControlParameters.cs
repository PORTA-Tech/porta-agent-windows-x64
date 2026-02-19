namespace Porta.Agent.Common.Contracts
{
    public sealed class ControlParameters
    {
        public bool SuppressionEnabled { get; init; }
        public bool ActuationEnabled { get; init; }
        public string RuntimeMode { get; init; } = "normal";
        public double HeartbeatFrequencyHz { get; init; }
        public bool TelemetryOutboundSuspended { get; init; }
        public int FixedDelayMs { get; init; }
        public string? TargetProcess { get; init; }
        public double TargetArousalMin { get; init; } = 0.45;
        public double TargetArousalMax { get; init; } = 0.55;
        public double Kp { get; init; } = 0.45;
        public double Ki { get; init; } = 0.08;
        public double Kd { get; init; } = 0.06;
        public bool EnableGainScheduling { get; init; } = true;
        public double OutputSmoothingAlpha { get; init; } = 0.25;
        public double ConfidenceThreshold { get; init; } = 0.34;
        public double MaxDeltaPerTick { get; init; } = 0.08;
    }
}
