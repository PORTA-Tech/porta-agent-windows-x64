namespace Porta.Agent.Runtime.Layer4;

internal readonly record struct Layer4StateResult(
    string CommittedState,
    string CorrelationId,
    double Probability,
    double Confidence,
    double EvidenceCoverage,
    string DistributionJson,
    string ContributingPrimitivesJson,
    string ExplanationJson,
    DateTimeOffset EvaluatedAtUtc,
    string TransitionReason);
