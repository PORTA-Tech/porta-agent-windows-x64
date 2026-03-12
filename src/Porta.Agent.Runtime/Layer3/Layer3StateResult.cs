namespace Porta.Agent.Runtime.Layer3;

internal readonly record struct Layer3StateResult(
    bool Success,
    string StateId,
    string CorrelationId,
    string StateName,
    double Probability,
    double Confidence,
    double EvidenceCoverage,
    double GlobalRiskScore,
    double RiskDelta,
    string TrendDirection,
    string ModelVersion,
    string DistributionJson,
    string ContributingPrimitivesJson,
    string ExplanationJson,
    DateTimeOffset EvaluatedAtUtc,
    string ErrorReason)
{
    public static Layer3StateResult Error(string reason, DateTimeOffset atUtc) =>
        new(false, string.Empty, string.Empty, string.Empty, 0, 0, 0, 0, 0, "flat", string.Empty, "{}", "[]", "{}", atUtc, reason);
}
