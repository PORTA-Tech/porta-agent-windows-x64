namespace Porta.Agent.Common.Contracts
{
    public sealed class AgentTelemetry
    {
        public string AgentId { get; init; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; init; }
        public string Kind { get; init; } = string.Empty;
        public int? Delta { get; init; }
        public bool? WasSuppressed { get; init; }
        public long? QueueDepth { get; init; }
        public long? DroppedEvents { get; init; }
        public string? Note { get; init; }
    }
}
