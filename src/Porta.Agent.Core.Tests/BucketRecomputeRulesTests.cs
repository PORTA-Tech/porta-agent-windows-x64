using System.Reflection;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class BucketRecomputeRulesTests
{
    [Fact]
    public void PendingDirtyFinalize_RecomputesK_AndMarksKPlus1Dirty()
    {
        var store = new FakeFinalizerStore();
        var finalizer = new BucketFinalizer(store, store);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long bucketK = Time.bucketSeqFromEventTs(nowMs - Time.bucket_ms);
        long bucketKPlus1 = bucketK + 1;
        (long kStartMs, long kEndMs) = Time.bucketStartEnd(bucketK);
        (long k1StartMs, _) = Time.bucketStartEnd(bucketKPlus1);

        finalizer.WriteSnapshot(BuildSnapshot(kStartMs + 100, typing: 0.10));
        finalizer.WriteSnapshot(BuildSnapshot(k1StartMs + 100, typing: 0.20));

        // Move k from OPEN -> PENDING.
        finalizer.RunFinalizerTick(DateTimeOffset.FromUnixTimeMilliseconds(kEndMs + 1));

        // Late-arriving (but valid) mutation for k while PENDING.
        finalizer.WriteSnapshot(BuildSnapshot(kStartMs + 200, typing: 0.30));

        // Finalize at end + maxLate.
        finalizer.RunFinalizerTick(
            DateTimeOffset.FromUnixTimeMilliseconds(Time.bucketLateCutoffFromEnd(kEndMs) + 1));

        Assert.Single(store.FinalizedWrites);
        FinalizedWrite finalized = store.FinalizedWrites[0];
        Assert.Equal(bucketK, finalized.InputRow.BucketSeq);
        // Recomputed from full pending buffer: (0.10 + 0.30) / 2 = 0.20
        Assert.Equal(0.20d, finalized.InputRow.TypingBurstIntensity, precision: 6);

        BucketBuffer successor = GetBuffer(finalizer, bucketKPlus1);
        Assert.NotEqual(BucketLifecycleState.FINAL, successor.State);
        Assert.True(successor.Dirty);
        Assert.True(successor.StateFlagsStage.TryGetValue("needs_recompute_prev_bucket", out string? value));
        Assert.Equal("1", value);
    }

    [Fact]
    public void FinalizeWriteFailure_DoesNotMarkFinal_AndRetriesOnNextTick()
    {
        var store = new FakeFinalizerStore
        {
            RemainingFailures = 1
        };
        var finalizer = new BucketFinalizer(store, store);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long bucketK = Time.bucketSeqFromEventTs(nowMs - Time.bucket_ms);
        (long kStartMs, long kEndMs) = Time.bucketStartEnd(bucketK);
        long cutoffMs = Time.bucketLateCutoffFromEnd(kEndMs);

        finalizer.WriteSnapshot(BuildSnapshot(kStartMs + 100, typing: 0.25));

        // OPEN -> PENDING
        finalizer.RunFinalizerTick(DateTimeOffset.FromUnixTimeMilliseconds(kEndMs + 1));
        BucketBuffer pending = GetBuffer(finalizer, bucketK);
        Assert.Equal(BucketLifecycleState.PENDING_FINALIZE, pending.State);

        // First finalize attempt fails in writer.
        finalizer.RunFinalizerTick(DateTimeOffset.FromUnixTimeMilliseconds(cutoffMs + 1));
        Assert.Equal(1, store.FinalizeAttempts);
        Assert.Empty(store.FinalizedWrites);

        BucketBuffer stillPending = GetBuffer(finalizer, bucketK);
        Assert.Equal(BucketLifecycleState.PENDING_FINALIZE, stillPending.State);
        Assert.True(stillPending.Dirty);

        // Next tick retries and succeeds.
        finalizer.RunFinalizerTick(DateTimeOffset.FromUnixTimeMilliseconds(cutoffMs + 2));
        Assert.Equal(2, store.FinalizeAttempts);
        Assert.Single(store.FinalizedWrites);

        Assert.False(TryGetBuffer(finalizer, bucketK, out _));
    }

    private static BucketBuffer GetBuffer(BucketFinalizer finalizer, long bucketSeq)
    {
        bool found = TryGetBuffer(finalizer, bucketSeq, out BucketBuffer? buffer);
        Assert.True(found);
        Assert.NotNull(buffer);
        return buffer!;
    }

    private static bool TryGetBuffer(BucketFinalizer finalizer, long bucketSeq, out BucketBuffer? buffer)
    {
        FieldInfo? buffersField = typeof(BucketFinalizer).GetField(
            "_buffers",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(buffersField);
        var buffers = (BucketBuffers?)buffersField!.GetValue(finalizer);
        Assert.NotNull(buffers);
        return buffers!.TryGet(bucketSeq, out buffer);
    }

    private static AgentBrainSnapshot BuildSnapshot(long tsUtcMs, double typing)
    {
        DateTimeOffset ts = DateTimeOffset.FromUnixTimeMilliseconds(tsUtcMs);
        return new AgentBrainSnapshot(
            TimestampUtc: ts,
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: 0.10d,
            FocusSwitchRate: 0.10d,
            SmoothedTypingBurstIntensity: typing,
            SmoothedScrollVelocityIndex: 0.10d,
            SmoothedFocusSwitchRate: 0.10d,
            FocusStrength: 0.50d,
            OverloadLikelihood: 0.20d,
            FatigueProbability: 0.10d,
            CognitiveStability: 0.80d,
            DominantStateValue: 0.50d,
            DominantStateName: "focus",
            Confidence: 0.90d,
            LastAction: "maintain_focus",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: Array.Empty<string>());
    }

    private readonly record struct FinalizedWrite(AgentBrainSnapshot Snapshot, InputBucketRow InputRow);

    private sealed class FakeFinalizerStore : IBrainStateStore, IBucketFinalizerDbWriter
    {
        public int RemainingFailures { get; set; }
        public int FinalizeAttempts { get; private set; }
        public List<FinalizedWrite> FinalizedWrites { get; } = [];

        public void WriteFinalizedBucketSnapshot(AgentBrainSnapshot snapshot, InputBucketRow inputBucketRow)
        {
            FinalizeAttempts++;
            if (RemainingFailures > 0)
            {
                RemainingFailures--;
                throw new InvalidOperationException("simulated_finalize_write_failure");
            }

            FinalizedWrites.Add(new FinalizedWrite(snapshot, inputBucketRow));
        }

        public void WriteSnapshot(AgentBrainSnapshot snapshot) => throw new NotSupportedException();
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
        public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = Time.bucketSeconds) => [];
        public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null) => [];
        public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200) => [];
        public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000) => [];
        public AgentStorageInfo ReadStorageInfo() => new(RetentionDays: 0, DatabaseBytes: 0);
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
