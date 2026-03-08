using System.Text.Json;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class InputSignalsBucketIntegrationTests
{
    [Fact]
    public void IngestedSignals_FinalizeThroughBucketPipeline_WithoutDirectSnapshotWrites()
    {
        var innerStore = new FakeBucketIngressStore();
        var stateStore = new BucketFinalizerStateStore(
            innerStore,
            () => new DateTimeOffset(2026, 03, 07, 10, 0, 12, TimeSpan.Zero));

        DateTimeOffset windowStartUtc = new(2026, 03, 07, 10, 0, 3, TimeSpan.Zero);
        DateTimeOffset windowEndUtc = new(2026, 03, 07, 10, 0, 7, TimeSpan.Zero);

        stateStore.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 1,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: 4,
            KeyboardBackspaceCount: 2,
            MouseWheelDelta: 120,
            MouseMoveDx: 20d,
            MouseMoveDy: -10d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 4d,
            LastInputEventUtc: windowEndUtc));

        stateStore.RunFinalizerTick(new DateTimeOffset(2026, 03, 07, 10, 0, 12, TimeSpan.Zero));

        Assert.Equal(2, innerStore.FinalizedWrites.Count);
        Assert.Equal(0, innerStore.DirectSnapshotWrites);

        FinalizedWrite first = innerStore.FinalizedWrites.OrderBy(static write => write.InputRow.BucketSeq).First();
        FinalizedWrite second = innerStore.FinalizedWrites.OrderBy(static write => write.InputRow.BucketSeq).Last();

        Assert.Equal(0, first.InputRow.Partial);
        Assert.Equal(0, second.InputRow.Partial);
        Assert.Equal("a1b2c3d4", first.InputRow.DominantAppHash);
        Assert.Equal("a1b2c3d4", second.InputRow.DominantAppHash);
        Assert.Equal(2d, first.InputRow.IdleSeconds, precision: 6);
        Assert.Equal(2d, second.InputRow.IdleSeconds, precision: 6);

        AssertKeyboardCounts(first.InputRow, expectedKeydowns: 3, expectedBackspaces: 1);
        AssertKeyboardCounts(second.InputRow, expectedKeydowns: 3, expectedBackspaces: 1);
    }

    private static void AssertKeyboardCounts(InputBucketRow row, int expectedKeydowns, int expectedBackspaces)
    {
        using JsonDocument json = JsonDocument.Parse(row.ContributorsJson);
        JsonElement keyboard = json.RootElement.GetProperty("keyboard");
        Assert.Equal(expectedKeydowns, keyboard.GetProperty("keys").GetInt32());
        Assert.Equal(expectedBackspaces, keyboard.GetProperty("backspace").GetInt32());
    }

    private readonly record struct FinalizedWrite(AgentBrainSnapshot Snapshot, InputBucketRow InputRow);

    private sealed class FakeBucketIngressStore : IBrainStateStore, IBucketFinalizerDbWriter
    {
        public int DirectSnapshotWrites { get; private set; }
        public List<FinalizedWrite> FinalizedWrites { get; } = [];

        public void WriteFinalizedBucketSnapshot(AgentBrainSnapshot snapshot, InputBucketRow inputBucketRow)
        {
            FinalizedWrites.Add(new FinalizedWrite(snapshot, inputBucketRow));
        }

        public void WriteSnapshot(AgentBrainSnapshot snapshot)
        {
            DirectSnapshotWrites++;
            throw new InvalidOperationException("direct_snapshot_write_not_expected");
        }

        public void WriteControlEvent(AgentControlEvent controlEvent) { }
        public void WriteSystemChange(AgentSystemChange change) { }
        public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics) { }
        public void WriteConsent(AgentConsentRecord consent) { }
        public AgentConsentRecord? ReadLatestConsent() => null;
        public void WriteSettings(AgentSettingsRecord settings) { }
        public AgentSettingsRecord? ReadLatestSettings() => null;
        public void DeleteLocalData() { }
        public AgentBrainSnapshot? ReadLatestSnapshot() => null;
        public AgentLatestStateDetails? ReadLatestStateDetails() => null;
        public AgentNotificationSuppressionStatus ReadNotificationSuppressionStatus()
            => new(
                Active: false,
                Ready: true,
                ActionState: "inactive",
                LastError: null,
                BackoffUntilUtc: null,
                EnableCooldownUntilUtc: null,
                MinOnUntilUtc: null,
                RestorePending: false);
        public AgentBrightnessActionStatus ReadBrightnessActionStatus()
            => new(
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
        public bool ReadDbMigrationFailed() => false;
        public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = Time.bucketSeconds) => [];
        public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null) => [];
        public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200) => [];
        public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000) => [];
        public IReadOnlyList<AgentPendingIntervention> ReadPendingInterventions(string sessionId, int limit = 20) => [];
        public bool AcknowledgeIntervention(AgentInterventionAck ack) => true;
        public AgentStorageInfo ReadStorageInfo() => new(RetentionDays: 90, DatabaseBytes: 0);
        public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null)
            => new(DateOnly.FromDateTime(DateTime.UtcNow), 0, 0, 0, 0, null);
        public string ExportBundle(
            IReadOnlyList<string> logFilePaths,
            string consentPath,
            string settingsPath,
            IReadOnlyDictionary<string, string> versions,
            IReadOnlyList<string> configPaths,
            IReadOnlyDictionary<string, string> supplementalJsonFiles) => string.Empty;
    }
}
