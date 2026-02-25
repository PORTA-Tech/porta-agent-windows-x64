namespace Porta.Agent.Common.Contracts
{
    public sealed class AgentTelemetry
    {
        public string AgentId { get; init; } = string.Empty;
        public string MachineId { get; init; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; init; }
        public string EventType { get; init; } = string.Empty;
        public string SchemaVersion { get; init; } = "1.0.0";
        public string Kind { get; init; } = string.Empty;
        public int? Delta { get; init; }
        public bool? WasSuppressed { get; init; }
        public long? QueueDepth { get; init; }
        public long? DroppedEvents { get; init; }
        public string? MetricName { get; init; }
        public double? Value { get; init; }
        public double? WindowDurationSeconds { get; init; }
        public string? Unit { get; init; }
        public string? CollectionMethod { get; init; }
        public string? PrivacyClass { get; init; }
        public string? ConsentState { get; init; }
        public string? SignatureAlgorithm { get; init; }
        public string? SignatureKeyId { get; init; }
        public string? Signature { get; init; }
        public string? PrimitiveId { get; init; }
        public string? Name { get; init; }
        public double? BaselineMean { get; init; }
        public double? BaselineStd { get; init; }
        public double? ZScore { get; init; }
        public double? Ewma { get; init; }
        public int? WindowSizeSeconds { get; init; }
        public double? Confidence { get; init; }
        public DateTimeOffset? ComputedAtUtc { get; init; }
        public string? StateId { get; init; }
        public string? StateName { get; init; }
        public double? Probability { get; init; }
        public double? GlobalRiskScore { get; init; }
        public double? RiskDelta { get; init; }
        public string? TrendDirection { get; init; }
        public string? ModelVersion { get; init; }
        public string? DistributionJson { get; init; }
        public string? ContributingPrimitivesJson { get; init; }
        public string? ExplanationJson { get; init; }
        public string? Note { get; init; }
    }
}
