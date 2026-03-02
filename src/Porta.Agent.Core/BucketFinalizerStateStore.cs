using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core;

/// <summary>
/// IBrainStateStore adapter that routes all writes through BucketFinalizer single-writer flow.
/// </summary>
public sealed class BucketFinalizerStateStore : IBrainStateStore, IBucketFinalizerPump
{
    private readonly IBrainStateStore _inner;
    private readonly BucketFinalizer _finalizer;

    public BucketFinalizerStateStore(IBrainStateStore inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (_inner is not IBucketFinalizerDbWriter bucketDbWriter)
        {
            throw new InvalidOperationException(
                "BucketFinalizerStateStore requires an inner store that implements IBucketFinalizerDbWriter.");
        }

        _finalizer = new BucketFinalizer(_inner, bucketDbWriter);
    }

    public void WriteSnapshot(AgentBrainSnapshot snapshot) => _finalizer.WriteSnapshot(snapshot);
    public void RunFinalizerTick(DateTimeOffset nowUtc) => _finalizer.RunFinalizerTick(nowUtc);
    public void WriteControlEvent(AgentControlEvent controlEvent) => _finalizer.WriteControlEvent(controlEvent);
    public void WriteSystemChange(AgentSystemChange change) => _finalizer.WriteSystemChange(change);
    public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics) => _finalizer.WriteDiagnostics(diagnostics);
    public void WriteConsent(AgentConsentRecord consent) => _finalizer.WriteConsent(consent);
    public AgentConsentRecord? ReadLatestConsent() => _inner.ReadLatestConsent();
    public void WriteSettings(AgentSettingsRecord settings) => _finalizer.WriteSettings(settings);
    public AgentSettingsRecord? ReadLatestSettings() => _inner.ReadLatestSettings();
    public void DeleteLocalData() => _finalizer.DeleteLocalData();
    public AgentBrainSnapshot? ReadLatestSnapshot() => _inner.ReadLatestSnapshot();

    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds)
        => _inner.ReadTimeline(fromUtc, toUtc, bucketSeconds);

    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null)
        => _inner.ReadRecentEvents(limit, sinceUtc);

    public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000)
        => _inner.ReadEvents(fromUtc, toUtc, limit);

    public AgentStorageInfo ReadStorageInfo() => _inner.ReadStorageInfo();
    public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null) => _inner.ReadTodaySummary(dateUtc);

    public string ExportBundle(
        IReadOnlyList<string> logFilePaths,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths,
        IReadOnlyDictionary<string, string> supplementalJsonFiles)
        => _inner.ExportBundle(logFilePaths, consentPath, settingsPath, versions, configPaths, supplementalJsonFiles);
}
