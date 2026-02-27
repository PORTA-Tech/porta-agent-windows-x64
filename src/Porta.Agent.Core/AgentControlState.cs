namespace Porta.Agent.Core;

public sealed record AgentControlState(
    bool Paused,
    bool ConsentActive,
    DateTimeOffset? PauseUntilUtc,
    string RegulationIntensity,
    bool NotificationReductionEnabled,
    bool FocusModeEnabled,
    bool AnimationReductionEnabled,
    bool TemporaryAppRestrictionEnabled);
