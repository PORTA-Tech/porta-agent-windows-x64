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

    private static readonly long RetentionWindowMs = checked((long)TimeSpan.FromDays(90).TotalMilliseconds);
    private const int FinalMarkerMemoryCap = 4096;
    private static readonly TimeSpan FinalMarkerRetention = TimeSpan.FromHours(1);

    private readonly IBrainStateStore _writer;
    private readonly IBucketFinalizerDbWriter _bucketDbWriter;
    private readonly Func<DateTimeOffset> _nowProvider;
    private readonly BucketBuffers _buffers = new();
    private readonly ConcurrentDictionary<long, long> _finalizedBucketMarkers = new();
    private long _highestFinalBucketSeq = long.MinValue;
    private long _lateDropCount;
    private long _futureSkewDropCount;
    private long _tooOldDropCount;
    private BucketDropCountersSnapshot _qualityDropSnapshot = new(0, 0, 0);

    public BucketFinalizer(
        IBrainStateStore writer,
        IBucketFinalizerDbWriter bucketDbWriter,
        Func<DateTimeOffset>? nowProvider = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bucketDbWriter = bucketDbWriter ?? throw new ArgumentNullException(nameof(bucketDbWriter));
        _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
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
        long nowUtcMs = _nowProvider().ToUnixTimeMilliseconds();
        BucketLifecycleState initialBucketState = IsFinalBucket(bucketSeq)
            ? BucketLifecycleState.FINAL
            : BucketLifecycleState.OPEN;
        EventTimestampValidation timestampValidation = ShouldDropEvent(tsUtcMs, initialBucketState, nowUtcMs);
        if (timestampValidation.ShouldDrop)
        {
            LogTimestampDrop(timestampValidation.DropReason, bucketSeq, tsUtcMs, nowUtcMs);
            return;
        }

        BucketBuffer bucket = _buffers.GetOrCreate(bucketSeq);
        EventTimestampDropReason dropReason = EventTimestampDropReason.None;

        lock (bucket.Gate)
        {
            BucketLifecycleState effectiveBucketState = (bucket.State == BucketLifecycleState.FINAL || IsFinalBucket(bucketSeq))
                ? BucketLifecycleState.FINAL
                : bucket.State;
            EventTimestampValidation lockedValidation = ShouldDropEvent(tsUtcMs, effectiveBucketState, nowUtcMs);
            if (lockedValidation.ShouldDrop)
            {
                dropReason = lockedValidation.DropReason;
            }
            else
            {
                bool wasPendingFinalize = bucket.State == BucketLifecycleState.PENDING_FINALIZE;
                BucketAggregators.ApplyEventToBucket(bucket, snapshot);
                if (wasPendingFinalize)
                {
                    // D2: keep PENDING_FINALIZE and recompute at finalize time.
                    bucket.Dirty = true;
                    bucket.PendingMutationCount++;
                }
            }
        }

        if (dropReason != EventTimestampDropReason.None)
        {
            if (dropReason == EventTimestampDropReason.LateFinal)
            {
                _buffers.Remove(bucketSeq);
            }

            LogTimestampDrop(dropReason, bucketSeq, tsUtcMs, nowUtcMs);
        }
    }

    public void RunFinalizerTick(DateTimeOffset nowUtc)
    {
        long nowUtcMs = nowUtc.ToUnixTimeMilliseconds();
        IReadOnlyList<long> finalizable;

        lock (BucketFinalizeLock)
        {
            PromoteOpenBucketsToPendingLocked(nowUtcMs);
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
            _futureSkewDropCount = 0;
            _tooOldDropCount = 0;
            _qualityDropSnapshot = new BucketDropCountersSnapshot(0, 0, 0);
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

            // Finalize eligibility:
            // now_ms >= late_cutoff_ms AND state == PENDING_FINALIZE.
            if (bucket.State == BucketLifecycleState.PENDING_FINALIZE
                && Time.IsBucketFinalizeEligible(nowUtcMs, bucket.EndUtcMs))
            {
                finalizable.Add(seq);
            }
        }

        return finalizable;
    }

    private void PromoteOpenBucketsToPendingLocked(long nowUtcMs)
    {
        foreach (long seq in _buffers.OrderedBucketSeqs())
        {
            if (!_buffers.TryGet(seq, out BucketBuffer? bucket) || bucket == null)
            {
                continue;
            }

            lock (bucket.Gate)
            {
                if (bucket.State != BucketLifecycleState.OPEN)
                {
                    continue;
                }

                // OPEN -> PENDING trigger:
                // when bucket window ends (now_ms >= bucket_end_ms).
                if (!Time.IsBucketWindowEnded(nowUtcMs, bucket.EndUtcMs))
                {
                    continue;
                }

                TransitionLocked(bucket, BucketLifecycleState.OPEN, BucketLifecycleState.PENDING_FINALIZE, nowUtcMs);
            }
        }
    }

    private void FinalizeBucketPass(long bucketSeq, long nowUtcMs)
    {
        string? deferredLog = null;
        long? successorSeqToMarkDirty = null;
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

                if (!Time.IsBucketFinalizeEligible(nowUtcMs, bucket.EndUtcMs))
                {
                    return;
                }

                if (bucket.State != BucketLifecycleState.PENDING_FINALIZE)
                {
                    return;
                }

                BucketDropCountersSnapshot currentDropSnapshot = ReadDropCountersSnapshot();
                BucketDropCountersDelta dropDelta = ComputeDropDelta(_qualityDropSnapshot, currentDropSnapshot);
                _qualityDropSnapshot = currentDropSnapshot;
                BucketDataQualityScore quality = BucketDataQualityScorer.Compute(bucket, dropDelta);
                bucket.QualityScore = quality.QualityScore;
                bucket.Partial = quality.Partial;
                bucket.StateFlagsStage["quality_score"] = quality.QualityScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                bucket.StateFlagsStage["partial"] = quality.Partial ? "1" : "0";
                bucket.StateFlagsStage["major_channels_missing"] = quality.MissingMajorChannels.Count == 0
                    ? string.Empty
                    : string.Join(",", quality.MissingMajorChannels);
                bool wasDirtyFromPendingMutation = bucket.Dirty && bucket.PendingMutationCount > 0;
                int pendingMutationsAtFinalize = bucket.PendingMutationCount;

                AgentBrainSnapshot? finalizedSnapshot = RecomputeForFinalize(bucket);

                // Finalize block: all write-side actions remain under BucketFinalizeLock.
                try
                {
                    if (finalizedSnapshot != null)
                    {
                        InputBucketRow inputBucketRow = InputBucketRowMapper.BuildInputBucketRow(bucket);
                        _bucketDbWriter.WriteFinalizedBucketSnapshot(finalizedSnapshot, inputBucketRow);
                    }

                    UpdateRuntimeStateLocked(bucket, nowUtcMs);
                    UpdateCooldownsLocked(bucket, nowUtcMs);
                }
                catch (Exception ex)
                {
                    // Contract: on failed commit/write, bucket is never marked FINAL.
                    bucket.Dirty = true;
                    deferredLog =
                        $"bucket_finalize_write_failed bucket_seq={bucket.BucketSeq} state={bucket.State} error={ex.GetType().Name}:{ex.Message}";
                    return;
                }

                TransitionLocked(bucket, BucketLifecycleState.PENDING_FINALIZE, BucketLifecycleState.FINAL, nowUtcMs);
                bucket.Dirty = false;
                bucket.PendingMutationCount = 0;
                bucket.FinalizedAtUtcMs = nowUtcMs;
                long nextHighest = Math.Max(Volatile.Read(ref _highestFinalBucketSeq), bucket.BucketSeq);
                Volatile.Write(ref _highestFinalBucketSeq, nextHighest);
                _finalizedBucketMarkers[bucket.BucketSeq] = nowUtcMs;
                if (wasDirtyFromPendingMutation)
                {
                    successorSeqToMarkDirty = checked(bucket.BucketSeq + 1);
                }

                if (finalizedSnapshot != null)
                {
                    if (wasDirtyFromPendingMutation)
                    {
                        deferredLog =
                            $"bucket_finalize_recompute_pending bucket_seq={bucket.BucketSeq} pending_mutations={pendingMutationsAtFinalize}";
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

            if (successorSeqToMarkDirty.HasValue)
            {
                MarkSuccessorDirtyIfPresentLocked(successorSeqToMarkDirty.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(deferredLog))
        {
            Console.WriteLine(deferredLog);
        }
    }

    private static AgentBrainSnapshot? RecomputeForFinalize(BucketBuffer bucket)
    {
        // Materialize deterministic per-bucket aggregate projection.
        // No state rollback is allowed; PENDING buckets are finalized with
        // aggregate values derived from all events seen for the bucket.
        return BucketAggregators.BuildFinalizedSnapshot(bucket);
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

    private static EventTimestampValidation ShouldDropEvent(
        long event_ts,
        BucketLifecycleState bucket_state,
        long now_ms)
    {
        return Time.ValidateEventTimestamp(
            event_ts_utc_ms: event_ts,
            now_utc_ms: now_ms,
            retention_window_ms: RetentionWindowMs,
            bucket_final: bucket_state == BucketLifecycleState.FINAL);
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

    private void LogTimestampDrop(EventTimestampDropReason reason, long bucketSeq, long eventTsUtcMs, long nowUtcMs)
    {
        long counter = IncrementDropCounter(reason);
        switch (reason)
        {
            case EventTimestampDropReason.FutureSkew:
                Console.WriteLine(
                    $"bucket_future_skew_drop bucket_seq={bucketSeq} event_ts_utc_ms={eventTsUtcMs} now_utc_ms={nowUtcMs} future_skew_drop_count={counter}");
                break;
            case EventTimestampDropReason.TooOld:
                Console.WriteLine(
                    $"bucket_too_old_drop bucket_seq={bucketSeq} event_ts_utc_ms={eventTsUtcMs} now_utc_ms={nowUtcMs} too_old_drop_count={counter}");
                break;
            case EventTimestampDropReason.LateFinal:
                Console.WriteLine(
                    $"bucket_final_late_drop bucket_seq={bucketSeq} late_drop_count={counter}");
                break;
        }
    }

    private long IncrementDropCounter(EventTimestampDropReason reason)
    {
        return reason switch
        {
            EventTimestampDropReason.FutureSkew => Interlocked.Increment(ref _futureSkewDropCount),
            EventTimestampDropReason.TooOld => Interlocked.Increment(ref _tooOldDropCount),
            EventTimestampDropReason.LateFinal => Interlocked.Increment(ref _lateDropCount),
            _ => 0
        };
    }

    private BucketDropCountersSnapshot ReadDropCountersSnapshot()
    {
        return new BucketDropCountersSnapshot(
            FutureSkewDrops: Volatile.Read(ref _futureSkewDropCount),
            TooOldDrops: Volatile.Read(ref _tooOldDropCount),
            LateFinalDrops: Volatile.Read(ref _lateDropCount));
    }

    private static BucketDropCountersDelta ComputeDropDelta(
        BucketDropCountersSnapshot previous,
        BucketDropCountersSnapshot current)
    {
        long future = Math.Max(0, current.FutureSkewDrops - previous.FutureSkewDrops);
        long tooOld = Math.Max(0, current.TooOldDrops - previous.TooOldDrops);
        long late = Math.Max(0, current.LateFinalDrops - previous.LateFinalDrops);
        return new BucketDropCountersDelta(future, tooOld, late);
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

    private void MarkSuccessorDirtyIfPresentLocked(long successorBucketSeq)
    {
        if (!_buffers.TryGet(successorBucketSeq, out BucketBuffer? successor) || successor == null)
        {
            return;
        }

        lock (successor.Gate)
        {
            if (successor.State == BucketLifecycleState.FINAL)
            {
                return;
            }

            successor.Dirty = true;
            successor.StateFlagsStage["needs_recompute_prev_bucket"] = "1";
        }
    }

    private static void TransitionLocked(
        BucketBuffer bucket,
        BucketLifecycleState from,
        BucketLifecycleState to,
        long transitionUtcMs)
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

        if (from == BucketLifecycleState.OPEN && to == BucketLifecycleState.PENDING_FINALIZE)
        {
            if (bucket.PendingSinceUtcMs != BucketBuffer.PendingSinceUnset)
            {
                throw new InvalidOperationException(
                    $"Bucket {bucket.BucketSeq} already has pending_since_ms={bucket.PendingSinceUtcMs} before OPEN->PENDING.");
            }

            // Pin pending_since_ms exactly at OPEN->PENDING transition.
            bucket.PendingSinceUtcMs = transitionUtcMs;
        }
        else if (from == BucketLifecycleState.PENDING_FINALIZE && to == BucketLifecycleState.FINAL)
        {
            if (bucket.PendingSinceUtcMs == BucketBuffer.PendingSinceUnset)
            {
                throw new InvalidOperationException(
                    $"Bucket {bucket.BucketSeq} missing pending_since_ms before PENDING->FINAL.");
            }
        }

        bucket.State = to;
    }
}
