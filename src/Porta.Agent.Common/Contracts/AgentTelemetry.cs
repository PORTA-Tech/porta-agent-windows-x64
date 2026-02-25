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
        public string? Note { get; init; }
    }
}
