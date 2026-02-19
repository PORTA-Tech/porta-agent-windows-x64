namespace Porta.Agent.Common.State;

public sealed class ControlState
{
    public bool IsInitialized { get; set; }
    public long LastTimestamp { get; set; }
    public double LastCompositeArousal { get; set; }
    public double LastError { get; set; }
    public double IntegralError { get; set; }
    public double LastCommand { get; set; }
    public double LastSmoothedCommand { get; set; }
    public double LastActuationIntensity { get; set; }
    public double BaselineArousalMean { get; set; }
    public double BaselineVariance { get; set; }
    public double DriftOffset { get; set; }
    public double LastStableKp { get; set; }
    public double LastStableKi { get; set; }
    public double LastStableKd { get; set; }
}
