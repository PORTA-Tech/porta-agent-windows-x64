namespace Porta.Agent.Runtime.Layer4;

internal readonly record struct Layer4StateResult(
    string CommittedState,
    double Probability,
    double Confidence,
    string DistributionJson,
    string ContributingPrimitivesJson,
    string ExplanationJson,
    DateTimeOffset EvaluatedAtUtc,
    string TransitionReason);
