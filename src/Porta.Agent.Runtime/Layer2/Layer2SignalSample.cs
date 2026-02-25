namespace Porta.Agent.Runtime.Layer2;

internal readonly record struct Layer2SignalSample(
    string MachineId,
    string CorrelationId,
    string MetricName,
    double Value,
    string Unit,
    double WindowDurationSeconds,
    string CollectionMethod,
    string PrivacyClass,
    DateTimeOffset TimestampUtc);
