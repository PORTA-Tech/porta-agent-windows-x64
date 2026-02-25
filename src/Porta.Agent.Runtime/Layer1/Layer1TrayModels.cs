namespace Porta.Agent.Runtime.Layer1;

internal sealed record Layer1TraySnapshot(
    string Status,
    Layer1ConsentState ConsentState,
    DateTimeOffset? LastSignalAtUtc,
    string Summary,
    bool CanPause,
    int? PauseRemainingSeconds,
    IReadOnlyDictionary<string, double> RiskProbabilities,
    double RiskSigma,
    IReadOnlyList<Layer1TrayPrimitiveView> RecentPrimitives,
    IReadOnlyList<Layer1TrayInterventionView> RecentInterventions);

internal sealed record Layer1TrayPrimitiveView(
    string Name,
    double Value,
    string Unit,
    double Confidence,
    DateTimeOffset TimestampUtc);

internal sealed record Layer1TrayInterventionView(
    string ActionType,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    double Confidence,
    string PolicyId,
    string Reason);
