using Porta.Agent.Core;
using System.Threading;

namespace Porta.Agent.Core.Tests;

internal sealed class LocalApiContractFakeStore : IBrainStateStore, IInputSignalsIngress
{
    private readonly List<AgentBrainSnapshot> _writtenSnapshots = [];
    private readonly List<InputSignalsPacket> _ingestedInputSignals = [];
    public IReadOnlyList<AgentBrainSnapshot> WrittenSnapshots => _writtenSnapshots.AsReadOnly();
    public int SnapshotWriteCount => _writtenSnapshots.Count;
    public IReadOnlyList<InputSignalsPacket> IngestedInputSignals => _ingestedInputSignals.AsReadOnly();
    public int InputSignalsIngestCount => _ingestedInputSignals.Count;

    public AgentBrainSnapshot? LatestSnapshot { get; set; }
    public AgentLatestStateDetails? LatestStateDetails { get; set; }
    public bool ThrowOnReadLatestStateDetails { get; set; }
    public bool ThrowOnReadNotificationSuppressionStatus { get; set; }
    public bool ThrowOnReadBrightnessActionStatus { get; set; }
    public bool ThrowOnReadDbMigrationFailed { get; set; }
    public string ReadLatestStateDetailsFailureMessage { get; set; } = "latest_state_details_unavailable";
    public string ReadNotificationSuppressionStatusFailureMessage { get; set; } = "notification_status_unavailable";
    public string ReadBrightnessActionStatusFailureMessage { get; set; } = "brightness_status_unavailable";
    public string ReadDbMigrationFailedFailureMessage { get; set; } = "db_migration_status_unavailable";
    public AgentNotificationSuppressionStatus NotificationStatus { get; set; } = new(
        Active: false,
        Ready: true,
        ActionState: "inactive",
        LastError: null,
        BackoffUntilUtc: null,
        EnableCooldownUntilUtc: null,
        MinOnUntilUtc: null,
        RestorePending: false);

    public AgentBrightnessActionStatus BrightnessStatus { get; set; } = new(
        Supported: true,
        ActionReady: true,
        Active: false,
        RampDirection: "idle",
        PreviousLevel: -1,
        TargetLevel: -1,
        CurrentLevel: -1,
        LastError: null,
        BackoffUntilUtc: null,
        CooldownUntilUtc: null);

    public bool DbMigrationFailed { get; set; }
    public IReadOnlyList<AgentControlEvent> RecentEvents { get; set; } = [];
    public IReadOnlyList<AgentPendingIntervention> PendingInterventions { get; set; } = [];
    public List<AgentInterventionAck> AckedInterventions { get; } = [];
    public AgentConsentRecord? LatestConsent { get; set; } = new(
        TimestampUtc: DateTimeOffset.UtcNow,
        Active: true,
        Version: "1.0.0",
        DisclosureHash: "hash");

    public AgentSettingsRecord? LatestSettings { get; set; } = new(
        TimestampUtc: DateTimeOffset.UtcNow,
        PrivacyLevel: "Standard",
        RegulationIntensity: "Balanced",
        DisabledCategories: [],
        NotificationReductionEnabled: true,
        FocusModeEnabled: true,
        AnimationReductionEnabled: false,
        TemporaryAppRestrictionEnabled: false,
        BaselineRecalibrationStartedUtc: null,
        BaselineRecalibrationUntilUtc: null);
    public ManualResetEventSlim? IngestInputSignalsEntered { get; set; }
    public ManualResetEventSlim? IngestInputSignalsRelease { get; set; }
    public TimeSpan IngestInputSignalsDelay { get; set; }
    public ManualResetEventSlim? ReadLatestStateDetailsEntered { get; set; }
    public ManualResetEventSlim? ReadLatestStateDetailsRelease { get; set; }
    public TimeSpan ReadLatestStateDetailsDelay { get; set; }

    public void WriteSnapshot(AgentBrainSnapshot snapshot)
    {
        _writtenSnapshots.Add(snapshot);
        LatestSnapshot = snapshot;
    }
    public void IngestInputSignals(InputSignalsPacket packet)
    {
        IngestInputSignalsEntered?.Set();
        if (IngestInputSignalsDelay > TimeSpan.Zero)
        {
            Thread.Sleep(IngestInputSignalsDelay);
        }

        IngestInputSignalsRelease?.Wait();
        _ingestedInputSignals.Add(packet);
    }
    public void WriteControlEvent(AgentControlEvent controlEvent) { }
    public void WriteSystemChange(AgentSystemChange change) { }
    public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics) { }
    public void WriteConsent(AgentConsentRecord consent) => LatestConsent = consent;
    public AgentConsentRecord? ReadLatestConsent() => LatestConsent;
    public void WriteSettings(AgentSettingsRecord settings) => LatestSettings = settings;
    public AgentSettingsRecord? ReadLatestSettings() => LatestSettings;
    public void DeleteLocalData() { }
    public AgentBrainSnapshot? ReadLatestSnapshot() => LatestSnapshot;
    public AgentLatestStateDetails? ReadLatestStateDetails()
    {
        ReadLatestStateDetailsEntered?.Set();
        if (ReadLatestStateDetailsDelay > TimeSpan.Zero)
        {
            Thread.Sleep(ReadLatestStateDetailsDelay);
        }

        ReadLatestStateDetailsRelease?.Wait();

        if (ThrowOnReadLatestStateDetails)
        {
            throw new InvalidOperationException(ReadLatestStateDetailsFailureMessage);
        }

        return LatestStateDetails;
    }

    public AgentNotificationSuppressionStatus ReadNotificationSuppressionStatus()
    {
        if (ThrowOnReadNotificationSuppressionStatus)
        {
            throw new InvalidOperationException(ReadNotificationSuppressionStatusFailureMessage);
        }

        return NotificationStatus;
    }

    public AgentBrightnessActionStatus ReadBrightnessActionStatus()
    {
        if (ThrowOnReadBrightnessActionStatus)
        {
            throw new InvalidOperationException(ReadBrightnessActionStatusFailureMessage);
        }

        return BrightnessStatus;
    }

    public bool ReadDbMigrationFailed()
    {
        if (ThrowOnReadDbMigrationFailed)
        {
            throw new InvalidOperationException(ReadDbMigrationFailedFailureMessage);
        }

        return DbMigrationFailed;
    }
    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = Time.bucketSeconds) => [];
    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null) => RecentEvents.Take(limit).ToList();
    public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200) => [];
    public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000) => [];
    public IReadOnlyList<AgentPendingIntervention> ReadPendingInterventions(string sessionId, int limit = 20)
        => PendingInterventions
            .Where(i => string.Equals(i.SessionId, sessionId, StringComparison.Ordinal))
            .Take(limit)
            .ToList();
    public bool AcknowledgeIntervention(AgentInterventionAck ack)
    {
        AckedInterventions.Add(ack);
        return true;
    }
    public AgentStorageInfo ReadStorageInfo() => new(RetentionDays: 90, DatabaseBytes: 0);
    public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null) => new(
        DateUtc: dateUtc ?? DateOnly.FromDateTime(DateTime.UtcNow),
        TotalInterventions: 0,
        AverageFocusStrength: 0,
        PeakOverloadLikelihood: 0,
        IdleFragmentationIndex: 0,
        MostVolatileHourUtc: null);
    public string ExportBundle(
        IReadOnlyList<string> logFilePaths,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths,
        IReadOnlyDictionary<string, string> supplementalJsonFiles) => string.Empty;
}
