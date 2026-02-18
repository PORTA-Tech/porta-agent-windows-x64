namespace Porta.Agent.Common.Contracts
{
    public sealed class ControlParameters
    {
        public bool SuppressionEnabled { get; init; }
        public bool ActuationEnabled { get; init; }
        public int FixedDelayMs { get; init; }
        public string? TargetProcess { get; init; }
    }
}
