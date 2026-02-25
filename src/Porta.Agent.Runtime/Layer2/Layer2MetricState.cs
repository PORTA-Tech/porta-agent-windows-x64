namespace Porta.Agent.Runtime.Layer2;

internal sealed class Layer2MetricState
{
    public long SampleCount { get; set; }
    public double Mean { get; set; }
    public double M2 { get; set; }
    public double EwmaShort { get; set; }
    public double EwmaLong { get; set; }
    public bool EwmaInitialized { get; set; }
    public DateTimeOffset LastUpdateUtc { get; set; }
}
