namespace Porta.Agent.Runtime.Layer2;

internal readonly record struct Layer2PrimitiveResult(
    bool Success,
    string PrimitiveId,
    string Name,
    double Value,
    string Unit,
    double BaselineMean,
    double? BaselineStd,
    double ZScore,
    double Ewma,
    int WindowSizeSeconds,
    double Confidence,
    DateTimeOffset ComputedAtUtc,
    string ErrorReason)
{
    public static Layer2PrimitiveResult Error(string reason, DateTimeOffset atUtc) =>
        new(false, string.Empty, string.Empty, 0, string.Empty, 0, null, 0, 0, 0, 0, atUtc, reason);
}
