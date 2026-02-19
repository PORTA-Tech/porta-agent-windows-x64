namespace Porta.Agent.Common.Environment;

public readonly record struct AuthorityGatedState(
    ActuationState State,
    bool UseSafeProfile);
