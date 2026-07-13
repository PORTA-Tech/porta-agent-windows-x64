using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core;

/// <summary>
/// IBrainStateStore adapter that routes all writes through BucketFinalizer single-writer flow.
/// </summary>
public sealed class BucketFinalizerStateStore : IBrainStateStore, IBucketFinalizerPump, IInputSignalsIngress
{
    private readonly IBrainStateStore _inner;
    private readonly BucketFinalizer _finalizer;

    public BucketFinalizerStateStore(IBrainStateStore inner)
        : this(inner, nowProvider: null)
    {
    }

    internal BucketFinalizerStateStore(IBrainStateStore inner, Func<DateTimeOffset>? nowProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (_inner is not IBucketFinalizerDbWriter bucketDbWriter)
        {
            throw new InvalidOperationException(
                "BucketFinalizerStateStore requires an inner store that implements IBucketFinalizerDbWriter.");
        }

        _finalizer = new BucketFinalizer(_inner, bucketDbWriter, nowProvider);
    }

    public void WriteSnapshot(AgentBrainSnapshot snapshot) => _finalizer.WriteSnapshot(snapshot);
    public void IngestInputSignals(InputSignalsPacket packet) => _finalizer.IngestInputSignals(packet);
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
    public AgentLatestStateDetails? ReadLatestStateDetails() => _inner.ReadLatestStateDetails();
    public AgentNotificationSuppressionStatus ReadNotificationSuppressionStatus() => _inner.ReadNotificationSuppressionStatus();
    public AgentBrightnessActionStatus ReadBrightnessActionStatus() => _inner.ReadBrightnessActionStatus();
    public bool ReadDbMigrationFailed() => _inner.ReadDbMigrationFailed();

    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds)
        => _inner.ReadTimeline(fromUtc, toUtc, bucketSeconds);

    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null)
        => _inner.ReadRecentEvents(limit, sinceUtc);
    public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200)
        => _inner.ReadEventsSinceId(sinceIdExclusive, limit);

    public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000)
        => _inner.ReadEvents(fromUtc, toUtc, limit);
    public IReadOnlyDictionary<string, double> ReadFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => _inner.ReadFocusAppDwellSeconds(fromUtc, toUtc);
    public IReadOnlyList<AgentPendingIntervention> ReadPendingInterventions(string sessionId, int limit = 20)
        => _inner.ReadPendingInterventions(sessionId, limit);
    public bool AcknowledgeIntervention(AgentInterventionAck ack)
        => _inner.AcknowledgeIntervention(ack);
    public void RecordGroundTruthLabel(GroundTruthLabel label) => _inner.RecordGroundTruthLabel(label);
    public void RecordPromptShown(string labelTarget, string promptKind, DateTimeOffset shownAtUtc)
        => _inner.RecordPromptShown(labelTarget, promptKind, shownAtUtc);
    public void ResolvePromptAnswered(string labelTarget, DateTimeOffset answeredAtUtc)
        => _inner.ResolvePromptAnswered(labelTarget, answeredAtUtc);
    public AgentLabelPromptCadence ReadLabelPromptCadence() => _inner.ReadLabelPromptCadence();
    public AgentSignupNudgeContext ReadSignupNudgeContext() => _inner.ReadSignupNudgeContext();
    public void RecordSignupNudgeShown(DateTimeOffset shownAtUtc) => _inner.RecordSignupNudgeShown(shownAtUtc);
    public AgentLabelDiagnostics ReadLabelDiagnostics() => _inner.ReadLabelDiagnostics();
    public AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc, DateTimeOffset nowUtc)
        => _inner.CreateReminder(title, dueAtUtc, nowUtc);
    public AgentReminder? ReadNextReminder(DateTimeOffset nowUtc) => _inner.ReadNextReminder(nowUtc);
    public bool AcknowledgeReminder(long id, string action, DateTimeOffset nowUtc)
        => _inner.AcknowledgeReminder(id, action, nowUtc);
    public AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc)
        => _inner.CreateTask(title, plannedSeconds, nowUtc);
    public IReadOnlyList<AgentTask> ReadTasks(DateTimeOffset dayStartUtc, DateTimeOffset nowUtc)
        => _inner.ReadTasks(dayStartUtc, nowUtc);
    public AgentTask? ActOnTask(long id, string action, DateTimeOffset nowUtc)
        => _inner.ActOnTask(id, action, nowUtc);

    public AgentStorageInfo ReadStorageInfo() => _inner.ReadStorageInfo();
    public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null) => _inner.ReadTodaySummary(dateUtc);
    public AgentDistractionSummary ReadCurrentDistractionSummary() => _inner.ReadCurrentDistractionSummary();

    public string ExportBundle(
        IReadOnlyList<string> logFilePaths,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths,
        IReadOnlyDictionary<string, string> supplementalJsonFiles)
        => _inner.ExportBundle(logFilePaths, consentPath, settingsPath, versions, configPaths, supplementalJsonFiles);
}
