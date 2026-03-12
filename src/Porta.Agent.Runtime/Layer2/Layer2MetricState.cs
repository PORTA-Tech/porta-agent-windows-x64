namespace Porta.Agent.Runtime.Layer2;

internal sealed class Layer2MetricState
{
    public long SampleCount { get; set; }
    public double BaselineMean { get; set; }
    public double BaselineVariance { get; set; }
    public double EffectiveSampleCount { get; set; }
    public bool BaselineInitialized { get; set; }
    public double EwmaShort { get; set; }
    public double EwmaLong { get; set; }
    public bool EwmaInitialized { get; set; }
    public DateTimeOffset LastUpdateUtc { get; set; }
}
