namespace Porta.Agent.Core;

public sealed record AgentBrainSnapshot(
    DateTimeOffset TimestampUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    double SmoothedTypingBurstIntensity,
    double SmoothedScrollVelocityIndex,
    double SmoothedFocusSwitchRate,
    double FocusStrength,
    double OverloadLikelihood,
    double FatigueProbability,
    double CognitiveStability,
    double DominantStateValue,
    string DominantStateName,
    double Confidence,
    string LastAction,
    bool ActivationWindowTriggered,
    bool CooldownTriggered,
    bool ActivationWindowOpen,
    IReadOnlyList<string> Contributors);
