namespace Porta.Agent.Core;

public sealed record AgentBrainSnapshot(
    DateTimeOffset TimestampUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    double FocusStrength,
    double OverloadLikelihood,
    double FatigueProbability,
    double CognitiveStability,
    double Confidence,
    string LastAction,
    IReadOnlyList<string> Contributors);
