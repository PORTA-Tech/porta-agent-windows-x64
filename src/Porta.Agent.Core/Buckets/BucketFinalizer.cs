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

    public void IngestInputSignals(InputSignalsPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        DateTimeOffset nowUtc = _nowProvider();
        long nowUtcMs = nowUtc.ToUnixTimeMilliseconds();
        DateTimeOffset referenceUtc = packet.LastInputEventUtc
            ?? packet.WindowEndUtc
            ?? packet.WindowStartUtc
            ?? nowUtc;
        long windowStartUtcMs = (packet.WindowStartUtc ?? referenceUtc).ToUniversalTime().ToUnixTimeMilliseconds();
        long windowEndUtcMs = (packet.WindowEndUtc ?? referenceUtc).ToUniversalTime().ToUnixTimeMilliseconds();
        if (windowEndUtcMs < windowStartUtcMs)
        {
            (windowStartUtcMs, windowEndUtcMs) = (windowEndUtcMs, windowStartUtcMs);
        }

        if (windowEndUtcMs <= windowStartUtcMs)
        {
            windowEndUtcMs = checked(windowStartUtcMs + 1);
        }

        IReadOnlyList<BucketWindowSlice> slices = BuildWindowSlices(windowStartUtcMs, windowEndUtcMs);
        if (slices.Count == 0)
        {
            return;
        }

        int[] keydownsBySlice = DistributeNonNegativeInt(packet.KeyboardKeydownCount, slices);
        int[] backspacesBySlice = DistributeNonNegativeInt(packet.KeyboardBackspaceCount, slices);
        int[] keyboardBurstsBySlice = DistributeNonNegativeInt(packet.KeyboardBurstCount, slices);
        int[] keyboardCorrectionSpikesBySlice = DistributeNonNegativeInt(packet.KeyboardCorrectionSpikeCount, slices);
        int[] mouseWheelBySlice = DistributeSignedInt(packet.MouseWheelDelta, slices);
        int[] mouseMoveDxBySlice = DistributeSignedInt((int)Math.Round(packet.MouseMoveDx, MidpointRounding.AwayFromZero), slices);
        int[] mouseMoveDyBySlice = DistributeSignedInt((int)Math.Round(packet.MouseMoveDy, MidpointRounding.AwayFromZero), slices);
        int[] mouseMoveSamplesBySlice = DistributeNonNegativeInt(packet.MouseMoveSampleCount, slices);
        int[] mouseMicroCorrectionsBySlice = DistributeNonNegativeInt(packet.MouseMicroCorrectionCount, slices);
        int[] mouseWheelEventsBySlice = DistributeNonNegativeInt(packet.MouseWheelEventCount, slices);
        int[] mouseScrollBurstsBySlice = DistributeNonNegativeInt(packet.MouseScrollBurstCount, slices);
        int[] idleObservationCountBySlice = DistributeNonNegativeInt(packet.IdleObservationCount, slices);
        double[] idleSecondsBySlice = DistributeTrailingIdleSeconds(
            packet.IdleSeconds,
            windowStartUtcMs,
            windowEndUtcMs,
            slices);
        double[] idleSecondsWithinWindowBySlice = DistributeNonNegativeDouble(packet.IdleSecondsWithinWindow ?? 0d, slices);
        int[] microIdleCountBySlice = DistributeNonNegativeInt(packet.MicroIdleCount, slices);
        double[] microIdleSecondsBySlice = DistributeNonNegativeDouble(packet.MicroIdleSeconds ?? 0d, slices);
        double[] breakSecondsWithinWindowBySlice = DistributeNonNegativeDouble(packet.BreakSecondsWithinWindow ?? 0d, slices);
        bool[] shortIdleBoundaryBySlice = DistributeShortIdleBoundaryObserved(
            packet.ShortIdleBoundaryObserved,
            idleSecondsBySlice,
            idleSecondsWithinWindowBySlice,
            microIdleSecondsBySlice,
            slices.Count);
        double[] mousePathLengthBySlice = DistributeNonNegativeDouble(packet.MousePathLengthPx ?? 0d, slices);
        string? focusAppHash = string.IsNullOrWhiteSpace(packet.FocusAppHash) ? null : packet.FocusAppHash.Trim().ToLowerInvariant();
        bool useTelemetrySummaries = packet.SchemaVer >= 2;

        for (int i = 0; i < slices.Count; i++)
        {
            ApplyInputSignalsSlice(
                slice: slices[i],
                nowUtcMs: nowUtcMs,
                useTelemetrySummaries: useTelemetrySummaries,
                keydownCount: keydownsBySlice[i],
                backspaceCount: backspacesBySlice[i],
                keyboardIkiMeanMs: packet.KeyboardIkiMeanMs ?? 0d,
                keyboardIkiStdMs: packet.KeyboardIkiStdMs ?? 0d,
                keyboardIkiCv: packet.KeyboardIkiCv ?? 0d,
                keyboardBurstCount: keyboardBurstsBySlice[i],
                keyboardBurstRate: packet.KeyboardBurstRate ?? 0d,
                keyboardCorrectionSpikeCount: keyboardCorrectionSpikesBySlice[i],
                keyboardCorrectionSpikeRate: packet.KeyboardCorrectionSpikeRate ?? 0d,
                mouseWheelDelta: mouseWheelBySlice[i],
                mouseMoveDx: mouseMoveDxBySlice[i],
                mouseMoveDy: mouseMoveDyBySlice[i],
                mouseMoveSampleCount: mouseMoveSamplesBySlice[i],
                mousePathLengthPx: mousePathLengthBySlice[i],
                mouseSpeedMeanPxPerSec: packet.MouseSpeedMeanPxPerSec ?? 0d,
                mouseJerkP95: packet.MouseJerkP95 ?? 0d,
                mouseMicroCorrectionCount: mouseMicroCorrectionsBySlice[i],
                mouseMicroCorrectionRate: packet.MouseMicroCorrectionRate ?? 0d,
                mouseWheelEventCount: mouseWheelEventsBySlice[i],
                mouseScrollBurstCount: mouseScrollBurstsBySlice[i],
                mouseScrollBurstRate: packet.MouseScrollBurstRate ?? 0d,
                mouseScrollSpeedMean: packet.MouseScrollSpeedMean ?? 0d,
                focusAppHash: focusAppHash,
                idleSeconds: idleSecondsBySlice[i],
                idleObservationCount: idleObservationCountBySlice[i],
                idleSecondsWithinWindow: idleSecondsWithinWindowBySlice[i],
                microIdleCount: microIdleCountBySlice[i],
                microIdleSeconds: microIdleSecondsBySlice[i],
                breakSecondsWithinWindow: breakSecondsWithinWindowBySlice[i],
                shortIdleBoundaryObserved: shortIdleBoundaryBySlice[i]);
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

    private void ApplyInputSignalsSlice(
        BucketWindowSlice slice,
        long nowUtcMs,
        bool useTelemetrySummaries,
        int keydownCount,
        int backspaceCount,
        double keyboardIkiMeanMs,
        double keyboardIkiStdMs,
        double keyboardIkiCv,
        int keyboardBurstCount,
        double keyboardBurstRate,
        int keyboardCorrectionSpikeCount,
        double keyboardCorrectionSpikeRate,
        int mouseWheelDelta,
        int mouseMoveDx,
        int mouseMoveDy,
        int mouseMoveSampleCount,
        double mousePathLengthPx,
        double mouseSpeedMeanPxPerSec,
        double mouseJerkP95,
        int mouseMicroCorrectionCount,
        double mouseMicroCorrectionRate,
        int mouseWheelEventCount,
        int mouseScrollBurstCount,
        double mouseScrollBurstRate,
        double mouseScrollSpeedMean,
        string? focusAppHash,
        double idleSeconds,
        int idleObservationCount,
        double idleSecondsWithinWindow,
        int microIdleCount,
        double microIdleSeconds,
        double breakSecondsWithinWindow,
        bool shortIdleBoundaryObserved)
    {
        bool hasMutation = keydownCount > 0
            || backspaceCount > 0
            || keyboardBurstCount > 0
            || keyboardCorrectionSpikeCount > 0
            || mouseWheelDelta != 0
            || mouseMoveDx != 0
            || mouseMoveDy != 0
            || mouseMoveSampleCount > 0
            || mouseMicroCorrectionCount > 0
            || mouseWheelEventCount > 0
            || mouseScrollBurstCount > 0
            || !string.IsNullOrWhiteSpace(focusAppHash)
            || idleSeconds > 0d
            || idleObservationCount > 0
            || idleSecondsWithinWindow > 0d
            || microIdleCount > 0
            || microIdleSeconds > 0d
            || breakSecondsWithinWindow > 0d
            || shortIdleBoundaryObserved;
        if (!hasMutation)
        {
            return;
        }

        BucketLifecycleState initialBucketState = IsFinalBucket(slice.BucketSeq)
            ? BucketLifecycleState.FINAL
            : BucketLifecycleState.OPEN;
        EventTimestampValidation timestampValidation = ShouldDropEvent(
            slice.RepresentativeEventUtcMs,
            initialBucketState,
            nowUtcMs);
        if (timestampValidation.ShouldDrop)
        {
            LogTimestampDrop(timestampValidation.DropReason, slice.BucketSeq, slice.RepresentativeEventUtcMs, nowUtcMs);
            return;
        }

        BucketBuffer bucket = _buffers.GetOrCreate(slice.BucketSeq);
        EventTimestampDropReason dropReason = EventTimestampDropReason.None;

        lock (bucket.Gate)
        {
            BucketLifecycleState effectiveBucketState = (bucket.State == BucketLifecycleState.FINAL || IsFinalBucket(slice.BucketSeq))
                ? BucketLifecycleState.FINAL
                : bucket.State;
            EventTimestampValidation lockedValidation = ShouldDropEvent(
                slice.RepresentativeEventUtcMs,
                effectiveBucketState,
                nowUtcMs);
            if (lockedValidation.ShouldDrop)
            {
                dropReason = lockedValidation.DropReason;
            }
            else
            {
                bool wasPendingFinalize = bucket.State == BucketLifecycleState.PENDING_FINALIZE;
                BucketAggregators.ApplyInputSignalsSliceToBucket(
                    bucket,
                    slice.StartUtcMs,
                    slice.EndUtcMs,
                    useTelemetrySummaries,
                    keydownCount,
                    backspaceCount,
                    keyboardIkiMeanMs,
                    keyboardIkiStdMs,
                    keyboardIkiCv,
                    keyboardBurstCount,
                    keyboardBurstRate,
                    keyboardCorrectionSpikeCount,
                    keyboardCorrectionSpikeRate,
                    mouseMoveDx,
                    mouseMoveDy,
                    mouseMoveSampleCount,
                    mousePathLengthPx,
                    mouseSpeedMeanPxPerSec,
                    mouseJerkP95,
                    mouseMicroCorrectionCount,
                    mouseMicroCorrectionRate,
                    mouseWheelDelta,
                    mouseWheelEventCount,
                    mouseScrollBurstCount,
                    mouseScrollBurstRate,
                    mouseScrollSpeedMean,
                    focusAppHash,
                    idleSeconds,
                    idleObservationCount,
                    idleSecondsWithinWindow,
                    microIdleCount,
                    microIdleSeconds,
                    breakSecondsWithinWindow,
                    shortIdleBoundaryObserved);
                if (wasPendingFinalize)
                {
                    bucket.Dirty = true;
                    bucket.PendingMutationCount++;
                }
            }
        }

        if (dropReason != EventTimestampDropReason.None)
        {
            if (dropReason == EventTimestampDropReason.LateFinal)
            {
                _buffers.Remove(slice.BucketSeq);
            }

            LogTimestampDrop(dropReason, slice.BucketSeq, slice.RepresentativeEventUtcMs, nowUtcMs);
        }
    }

    private static IReadOnlyList<BucketWindowSlice> BuildWindowSlices(long windowStartUtcMs, long windowEndUtcMs)
    {
        long safeStartUtcMs = Math.Min(windowStartUtcMs, windowEndUtcMs);
        long safeEndUtcMs = Math.Max(safeStartUtcMs + 1, windowEndUtcMs);
        long startBucketSeq = Time.bucketSeqFromEventTs(safeStartUtcMs);
        long endBucketSeq = Time.bucketSeqFromEventTs(safeEndUtcMs - 1);
        var slices = new List<BucketWindowSlice>(checked((int)Math.Max(1L, endBucketSeq - startBucketSeq + 1)));

        for (long bucketSeq = startBucketSeq; bucketSeq <= endBucketSeq; bucketSeq++)
        {
            (long bucketStartUtcMs, long bucketEndUtcMs) = Time.bucketStartEnd(bucketSeq);
            long sliceStartUtcMs = Math.Max(safeStartUtcMs, bucketStartUtcMs);
            long sliceEndUtcMs = Math.Min(safeEndUtcMs, bucketEndUtcMs);
            if (sliceEndUtcMs <= sliceStartUtcMs)
            {
                continue;
            }

            long representativeEventUtcMs = Math.Clamp(
                sliceEndUtcMs - 1,
                sliceStartUtcMs,
                Math.Max(sliceStartUtcMs, sliceEndUtcMs - 1));
            slices.Add(new BucketWindowSlice(bucketSeq, sliceStartUtcMs, sliceEndUtcMs, representativeEventUtcMs));
        }

        return slices;
    }

    private static int[] DistributeNonNegativeInt(int total, IReadOnlyList<BucketWindowSlice> slices)
    {
        if (total <= 0 || slices.Count == 0)
        {
            return new int[slices.Count];
        }

        double[] weights = slices.Select(static slice => (double)slice.DurationMs).ToArray();
        return DistributeNonNegativeInt(total, weights);
    }

    private static int[] DistributeSignedInt(int total, IReadOnlyList<BucketWindowSlice> slices)
    {
        if (total == 0 || slices.Count == 0)
        {
            return new int[slices.Count];
        }

        int sign = Math.Sign(total);
        int[] distributed = DistributeNonNegativeInt(Math.Abs(total), slices);
        for (int i = 0; i < distributed.Length; i++)
        {
            distributed[i] *= sign;
        }

        return distributed;
    }

    private static double[] DistributeTrailingIdleSeconds(
        double totalIdleSeconds,
        long windowStartUtcMs,
        long windowEndUtcMs,
        IReadOnlyList<BucketWindowSlice> slices)
    {
        var distributed = new double[slices.Count];
        if (totalIdleSeconds <= 0d || slices.Count == 0)
        {
            return distributed;
        }

        long safeStartUtcMs = Math.Min(windowStartUtcMs, windowEndUtcMs);
        long safeEndUtcMs = Math.Max(safeStartUtcMs + 1, windowEndUtcMs);
        long trailingIdleMs = Math.Min(
            safeEndUtcMs - safeStartUtcMs,
            Math.Max(0L, (long)Math.Round(totalIdleSeconds * 1000d, MidpointRounding.AwayFromZero)));
        if (trailingIdleMs <= 0)
        {
            return distributed;
        }

        long idleIntervalEndUtcMs = safeEndUtcMs;
        long idleIntervalStartUtcMs = Math.Max(safeStartUtcMs, idleIntervalEndUtcMs - trailingIdleMs);
        for (int i = 0; i < slices.Count; i++)
        {
            long overlapStartUtcMs = Math.Max(idleIntervalStartUtcMs, slices[i].StartUtcMs);
            long overlapEndUtcMs = Math.Min(idleIntervalEndUtcMs, slices[i].EndUtcMs);
            if (overlapEndUtcMs <= overlapStartUtcMs)
            {
                continue;
            }

            distributed[i] = (overlapEndUtcMs - overlapStartUtcMs) / 1000d;
        }

        return distributed;
    }

    private static int[] DistributeNonNegativeInt(int total, IReadOnlyList<double> weights)
    {
        var distributed = new int[weights.Count];
        if (total <= 0 || weights.Count == 0)
        {
            return distributed;
        }

        double totalWeight = weights.Sum();
        if (totalWeight <= 0d)
        {
            distributed[^1] = total;
            return distributed;
        }

        var shares = new (int Index, int BaseValue, double Remainder)[weights.Count];
        int assigned = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            double rawShare = total * (weights[i] / totalWeight);
            int baseValue = (int)Math.Floor(rawShare);
            shares[i] = (i, baseValue, rawShare - baseValue);
            distributed[i] = baseValue;
            assigned += baseValue;
        }

        int remaining = total - assigned;
        foreach ((int index, _, _) in shares
                     .OrderByDescending(static share => share.Remainder)
                     .ThenBy(static share => share.Index)
                     .Take(remaining))
        {
            distributed[index]++;
        }

        return distributed;
    }

    private static double[] DistributeNonNegativeDouble(double total, IReadOnlyList<BucketWindowSlice> slices)
    {
        var distributed = new double[slices.Count];
        if (total <= 0d || slices.Count == 0)
        {
            return distributed;
        }

        double totalWeight = slices.Sum(static slice => (double)slice.DurationMs);
        if (totalWeight <= 0d)
        {
            distributed[^1] = total;
            return distributed;
        }

        double assigned = 0d;
        for (int i = 0; i < slices.Count; i++)
        {
            if (i == slices.Count - 1)
            {
                distributed[i] = Math.Max(0d, total - assigned);
                break;
            }

            distributed[i] = total * (slices[i].DurationMs / totalWeight);
            assigned += distributed[i];
        }

        return distributed;
    }

    private static bool[] DistributeShortIdleBoundaryObserved(
        bool observed,
        IReadOnlyList<double> currentIdleSecondsBySlice,
        IReadOnlyList<double> idleSecondsWithinWindowBySlice,
        IReadOnlyList<double> microIdleSecondsBySlice,
        int count)
    {
        var distributed = new bool[count];
        if (!observed || count <= 0)
        {
            return distributed;
        }

        for (int i = count - 1; i >= 0; i--)
        {
            if (currentIdleSecondsBySlice[i] > 0d
                || idleSecondsWithinWindowBySlice[i] > 0d
                || microIdleSecondsBySlice[i] > 0d)
            {
                distributed[i] = true;
                return distributed;
            }
        }

        distributed[^1] = true;
        return distributed;
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

    private readonly record struct BucketWindowSlice(
        long BucketSeq,
        long StartUtcMs,
        long EndUtcMs,
        long RepresentativeEventUtcMs)
    {
        public long DurationMs => Math.Max(1, EndUtcMs - StartUtcMs);
    }
}
