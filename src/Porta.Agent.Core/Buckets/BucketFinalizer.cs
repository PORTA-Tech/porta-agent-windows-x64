using System.Collections.Concurrent;

namespace Porta.Agent.Core.Buckets;

/// <summary>
/// Single DB writer and bucket lifecycle owner.
/// Finalization + recomputation + DB writes + runtime_state/cooldown updates
/// are all constrained to the finalize pass under BucketFinalizeLock.
/// </summary>
internal sealed class BucketFinalizer
{
    private static readonly object BucketFinalizeLock = new();
    private static int _instanceCount;

    private const int FinalMarkerMemoryCap = 4096;
    private static readonly TimeSpan FinalMarkerRetention = TimeSpan.FromHours(1);

    private readonly IBrainStateStore _writer;
    private readonly IBucketFinalizerDbWriter _bucketDbWriter;
    private readonly BucketBuffers _buffers = new();
    private readonly ConcurrentDictionary<long, long> _finalizedBucketMarkers = new();
    private long _highestFinalBucketSeq = long.MinValue;
    private long _lateDropCount;

    public BucketFinalizer(IBrainStateStore writer, IBucketFinalizerDbWriter bucketDbWriter)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bucketDbWriter = bucketDbWriter ?? throw new ArgumentNullException(nameof(bucketDbWriter));
        if (Interlocked.Increment(ref _instanceCount) > 1)
        {
            Console.WriteLine("bucket_single_writer_violation multiple_bucket_finalizer_instances_detected=true");
        }
    }

    public void WriteSnapshot(AgentBrainSnapshot snapshot)
    {
        // Ingest path contract:
        // - compute canonical ts_utc_ms and bucket_seq
        // - route to in-memory OPEN/PENDING buffer
        // - no DB writes in ingest
        // Locking contract:
        // - no BucketFinalizeLock for ingest path
        // - per-bucket lock only during aggregate mutation
        long tsUtcMs = snapshot.TimestampUtc.ToUnixTimeMilliseconds();
        long bucketSeq = Time.bucketSeqFromEventTs(tsUtcMs);
        if (IsFinalBucket(bucketSeq))
        {
            long lateDropCount = Interlocked.Increment(ref _lateDropCount);
            Console.WriteLine($"bucket_final_late_drop bucket_seq={bucketSeq} late_drop_count={lateDropCount}");
            return;
        }

        BucketBuffer bucket = _buffers.GetOrCreate(bucketSeq);
        bool shouldDropLate = false;
        long lateDropCountAfterDrop = 0;

        lock (bucket.Gate)
        {
            if (bucket.State == BucketLifecycleState.FINAL || IsFinalBucket(bucketSeq))
            {
                shouldDropLate = true;
                lateDropCountAfterDrop = Interlocked.Increment(ref _lateDropCount);
            }
            else
            {
                bool wasPendingFinalize = bucket.State == BucketLifecycleState.PENDING_FINALIZE;
                BucketAggregators.ApplyEventToBucket(bucket, snapshot);
                if (wasPendingFinalize)
                {
                    // D2: keep PENDING_FINALIZE and recompute at finalize time.
                    bucket.DirtyWhilePending = true;
                    bucket.PendingMutationCount++;
                }
            }
        }

        if (shouldDropLate)
        {
            _buffers.Remove(bucketSeq);
            Console.WriteLine($"bucket_final_late_drop bucket_seq={bucketSeq} late_drop_count={lateDropCountAfterDrop}");
        }
    }

    public void RunFinalizerTick(DateTimeOffset nowUtc)
    {
        long nowUtcMs = nowUtc.ToUnixTimeMilliseconds();
        IReadOnlyList<long> finalizable;

        lock (BucketFinalizeLock)
        {
            finalizable = CollectFinalizableBucketSeqsLocked(nowUtcMs);
            EvictFinalMarkersLocked(nowUtcMs);
        }

        foreach (long bucketSeq in finalizable)
        {
            FinalizeBucketPass(bucketSeq, nowUtcMs);
        }
    }

    public void WriteControlEvent(AgentControlEvent controlEvent)
    {
        lock (BucketFinalizeLock)
        {
            _writer.WriteControlEvent(controlEvent);
        }
    }

    public void WriteSystemChange(AgentSystemChange change)
    {
        lock (BucketFinalizeLock)
        {
            _writer.WriteSystemChange(change);
        }
    }

    public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics)
    {
        lock (BucketFinalizeLock)
        {
            _writer.WriteDiagnostics(diagnostics);
        }
    }

    public void WriteConsent(AgentConsentRecord consent)
    {
        lock (BucketFinalizeLock)
        {
            _writer.WriteConsent(consent);
        }
    }

    public void WriteSettings(AgentSettingsRecord settings)
    {
        lock (BucketFinalizeLock)
        {
            _writer.WriteSettings(settings);
        }
    }

    public void DeleteLocalData()
    {
        lock (BucketFinalizeLock)
        {
            _writer.DeleteLocalData();
            _buffers.Clear();
            _finalizedBucketMarkers.Clear();
            _highestFinalBucketSeq = long.MinValue;
            _lateDropCount = 0;
        }
    }

    private IReadOnlyList<long> CollectFinalizableBucketSeqsLocked(long nowUtcMs)
    {
        if (_buffers.Count == 0)
        {
            return [];
        }

        var finalizable = new List<long>(_buffers.Count);
        foreach (long seq in _buffers.OrderedBucketSeqs())
        {
            if (!_buffers.TryGet(seq, out BucketBuffer? bucket) || bucket == null)
            {
                continue;
            }

            // D1: time-based eligibility.
            // Finalizable OPEN buckets are those whose window settled long enough ago.
            long finalizeEligibleAtMs = ComputeFinalizeEligibleAtMs(bucket.EndUtcMs);
            if (nowUtcMs >= finalizeEligibleAtMs)
            {
                finalizable.Add(seq);
            }
        }

        return finalizable;
    }

    private void FinalizeBucketPass(long bucketSeq, long nowUtcMs)
    {
        string? deferredLog = null;
        lock (BucketFinalizeLock)
        {
            if (!_buffers.TryGet(bucketSeq, out BucketBuffer? bucket) || bucket == null)
            {
                return;
            }

            lock (bucket.Gate)
            {
                if (bucket.State == BucketLifecycleState.FINAL)
                {
                    return;
                }

                long finalizeEligibleAtMs = ComputeFinalizeEligibleAtMs(bucket.EndUtcMs);
                if (nowUtcMs < finalizeEligibleAtMs)
                {
                    return;
                }

                if (bucket.State == BucketLifecycleState.OPEN)
                {
                    TransitionLocked(bucket, BucketLifecycleState.OPEN, BucketLifecycleState.PENDING_FINALIZE);
                }

                if (bucket.State != BucketLifecycleState.PENDING_FINALIZE)
                {
                    return;
                }

                AgentBrainSnapshot? finalizedSnapshot = RecomputeForFinalize(bucket);

                // Finalize block: all write-side actions remain under BucketFinalizeLock.
                try
                {
                    if (finalizedSnapshot != null)
                    {
                        _bucketDbWriter.WriteFinalizedBucketSnapshot(finalizedSnapshot);
                    }

                    UpdateRuntimeStateLocked(bucket, nowUtcMs);
                    UpdateCooldownsLocked(bucket, nowUtcMs);
                }
                catch (Exception ex)
                {
                    // Contract: on failed commit/write, bucket is never marked FINAL.
                    bucket.DirtyWhilePending = true;
                    bucket.PendingMutationCount++;
                    deferredLog =
                        $"bucket_finalize_write_failed bucket_seq={bucket.BucketSeq} state={bucket.State} error={ex.GetType().Name}:{ex.Message}";
                    return;
                }

                TransitionLocked(bucket, BucketLifecycleState.PENDING_FINALIZE, BucketLifecycleState.FINAL);
                long nextHighest = Math.Max(Volatile.Read(ref _highestFinalBucketSeq), bucket.BucketSeq);
                Volatile.Write(ref _highestFinalBucketSeq, nextHighest);
                _finalizedBucketMarkers[bucket.BucketSeq] = nowUtcMs;

                if (finalizedSnapshot != null)
                {
                    if (bucket.DirtyWhilePending)
                    {
                        deferredLog =
                            $"bucket_finalize_recompute_pending bucket_seq={bucket.BucketSeq} pending_mutations={bucket.PendingMutationCount}";
                    }
                    else
                    {
                        deferredLog = $"bucket_finalized bucket_seq={bucket.BucketSeq} samples={bucket.SampleCount}";
                    }
                }
                else
                {
                    deferredLog = $"bucket_finalized_empty bucket_seq={bucket.BucketSeq}";
                }

                _buffers.Remove(bucket.BucketSeq);
                EvictFinalMarkersLocked(nowUtcMs);
            }
        }

        if (!string.IsNullOrWhiteSpace(deferredLog))
        {
            Console.WriteLine(deferredLog);
        }
    }

    private static AgentBrainSnapshot? RecomputeForFinalize(BucketBuffer bucket)
    {
        // For v1, recompute means materializing the latest in-memory aggregate snapshot.
        // No state rollback is allowed; PENDING buckets are finalized with last deterministic aggregate.
        return bucket.LatestSnapshot;
    }

    private static void UpdateRuntimeStateLocked(BucketBuffer bucket, long nowUtcMs)
    {
        _ = bucket;
        _ = nowUtcMs;
        // Placeholder for runtime_state write model staged in later steps.
    }

    private static void UpdateCooldownsLocked(BucketBuffer bucket, long nowUtcMs)
    {
        _ = bucket;
        _ = nowUtcMs;
        // Placeholder for cooldown/stabilization updates staged in later steps.
    }

    private bool IsFinalBucket(long bucketSeq)
    {
        long highestFinal = Volatile.Read(ref _highestFinalBucketSeq);
        if (bucketSeq <= highestFinal)
        {
            return true;
        }

        return _finalizedBucketMarkers.ContainsKey(bucketSeq);
    }

    private static long ComputeFinalizeEligibleAtMs(long bucketEndUtcMs)
    {
        // Safe eligibility: never finalize before late allowance window closes.
        // finalizeDelayMs is optional extra buffer; max() preserves both contracts.
        int settleMs = Math.Max(Time.maxLateMs, Time.finalizeDelayMs);
        return checked(bucketEndUtcMs + settleMs);
    }

    private void EvictFinalMarkersLocked(long nowUtcMs)
    {
        long cutoffMs = checked(nowUtcMs - (long)FinalMarkerRetention.TotalMilliseconds);
        foreach (KeyValuePair<long, long> marker in _finalizedBucketMarkers)
        {
            if (marker.Value < cutoffMs)
            {
                _finalizedBucketMarkers.TryRemove(marker.Key, out _);
            }
        }

        while (_finalizedBucketMarkers.Count > FinalMarkerMemoryCap)
        {
            KeyValuePair<long, long>? oldest = null;
            foreach (KeyValuePair<long, long> marker in _finalizedBucketMarkers)
            {
                if (!oldest.HasValue || marker.Value < oldest.Value.Value)
                {
                    oldest = marker;
                }
            }

            if (!oldest.HasValue)
            {
                break;
            }

            _finalizedBucketMarkers.TryRemove(oldest.Value.Key, out _);
        }
    }

    private static void TransitionLocked(BucketBuffer bucket, BucketLifecycleState from, BucketLifecycleState to)
    {
        if (bucket.State != from)
        {
            throw new InvalidOperationException(
                $"Invalid bucket state transition for {bucket.BucketSeq}. Expected {from}, current {bucket.State}, requested {to}.");
        }

        if ((int)to <= (int)from)
        {
            throw new InvalidOperationException(
                $"Non-forward bucket state transition for {bucket.BucketSeq}: {from}->{to}.");
        }

        bucket.State = to;
    }
}
