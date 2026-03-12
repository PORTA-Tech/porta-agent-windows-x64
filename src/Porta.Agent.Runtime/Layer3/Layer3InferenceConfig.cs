namespace Porta.Agent.Runtime.Layer3;

internal sealed class Layer3InferenceConfig
{
    public string ModelVersion { get; init; } = "deterministic-v1.1.0";
    public double ZMax { get; init; } = 6.0;
    public double TrendEpsilon { get; init; } = 0.01;
    public int EvaluationCadenceSeconds { get; init; } = 10;
    public int PrimitiveStaleAfterSeconds { get; init; } = 60;

    public IReadOnlyDictionary<string, Layer3MetricRiskSpec> MetricRiskSpecs { get; init; } = new Dictionary<string, Layer3MetricRiskSpec>(StringComparer.Ordinal)
    {
        ["system.cpu_utilization"] = new(0.11, Layer3RiskDirection.HighOnly),
        ["system.memory_utilization"] = new(0.10, Layer3RiskDirection.HighOnly),
        ["system.disk_io_rate"] = new(0.05, Layer3RiskDirection.HighOnly),
        ["system.network_throughput"] = new(0.06, Layer3RiskDirection.HighOnly),
        ["system.process_count"] = new(0.05, Layer3RiskDirection.HighOnly),
        ["system.service_health_status"] = new(0.08, Layer3RiskDirection.LowOnly),
        ["interaction.keystroke_rate"] = new(0.10, Layer3RiskDirection.HighOnly),
        ["interaction.mouse_movement_distance"] = new(0.10, Layer3RiskDirection.HighOnly),
        ["interaction.app_switch_frequency"] = new(0.08, Layer3RiskDirection.HighOnly),
        ["interaction.idle_duration"] = new(0.06, Layer3RiskDirection.LowOnly),
        ["agent.cpu_usage"] = new(0.09, Layer3RiskDirection.HighOnly),
        ["agent.memory_usage"] = new(0.06, Layer3RiskDirection.HighOnly),
        ["agent.event_queue_depth"] = new(0.03, Layer3RiskDirection.HighOnly),
        ["agent.error_count"] = new(0.03, Layer3RiskDirection.HighOnly)
    };
}

internal enum Layer3RiskDirection
{
    HighOnly = 0,
    LowOnly = 1,
    TwoSided = 2
}

internal readonly record struct Layer3MetricRiskSpec(
    double Weight,
    Layer3RiskDirection Direction);
