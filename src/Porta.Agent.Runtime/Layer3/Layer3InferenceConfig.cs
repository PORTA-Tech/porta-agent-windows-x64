namespace Porta.Agent.Runtime.Layer3;

internal sealed class Layer3InferenceConfig
{
    public string ModelVersion { get; init; } = "deterministic-v1.0.0";
    public double ZMax { get; init; } = 6.0;
    public double TrendEpsilon { get; init; } = 0.01;
    public int EvaluationCadenceSeconds { get; init; } = 10;
    public int PrimitiveStaleAfterSeconds { get; init; } = 60;

    public IReadOnlyDictionary<string, double> MetricWeights { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["system.cpu_utilization"] = 0.11,
        ["system.memory_utilization"] = 0.10,
        ["system.disk_io_rate"] = 0.05,
        ["system.network_throughput"] = 0.06,
        ["system.process_count"] = 0.05,
        ["system.service_health_status"] = 0.08,
        ["interaction.keystroke_rate"] = 0.10,
        ["interaction.mouse_movement_distance"] = 0.10,
        ["interaction.app_switch_frequency"] = 0.08,
        ["interaction.idle_duration"] = 0.06,
        ["agent.cpu_usage"] = 0.09,
        ["agent.memory_usage"] = 0.06,
        ["agent.event_queue_depth"] = 0.03,
        ["agent.error_count"] = 0.03
    };
}
