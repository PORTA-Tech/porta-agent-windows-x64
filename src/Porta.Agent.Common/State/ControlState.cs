namespace Porta.Agent.Common.State;

public sealed class ControlState
{
    public bool IsInitialized { get; set; }
    public long LastTimestamp { get; set; }
    public double LastError { get; set; }
    public double IntegralError { get; set; }
    public double LastCommand { get; set; }
    public double LastSmoothedCommand { get; set; }
}
