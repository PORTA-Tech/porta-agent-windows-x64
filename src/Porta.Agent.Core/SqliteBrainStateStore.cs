using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Common;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Db;
using Porta.Agent.Core.Outputs;
using Porta.Agent.Core.Security;

namespace Porta.Agent.Core;

public sealed class SqliteBrainStateStore : IBrainStateStore, IBucketFinalizerDbWriter
{
    private const int DefaultRetentionDays = 14;
    private const int DefaultEventCooldownSeconds = 20;
    private const int ResponsiveReadBusyTimeoutMs = 250;
    private static readonly Lazy<string> BreakInstallIdHash = new(ResolveBreakInstallIdHash, isThreadSafe: true);
    private static readonly SemaphoreSlim _readGate = new(3, 3);

    private readonly object _gate = new();
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly IDeepWorkOutputActionCoordinator _deepWorkOutputActionCoordinator;
    private readonly IOverloadBrightnessActionCoordinator _overloadBrightnessActionCoordinator;
    private readonly IRuntimePolicyProvider _runtimePolicyProvider;
    private readonly TimeSpan _retention = TimeSpan.FromDays(DefaultRetentionDays);
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
    private bool _backgroundVacuumScheduled;
    private string? _lastAutoEventAction;
    private DateTimeOffset _lastAutoEventAtUtc = DateTimeOffset.MinValue;
    private ActiveInterventionState? _activeIntervention;

    private sealed record ActiveInterventionState(
        string InterventionId,
        string Action,
        string EscalationLevel,
        string PolicyId,
        string Domain,
        DateTimeOffset StartTimestampUtc,
        DateTimeOffset? PlannedEndTimestampUtc,
        DateTimeOffset? CooldownEndTimestampUtc);

    public SqliteBrainStateStore()
        : this(
            new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
            new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
            new FileRuntimePolicyProvider(),
            dbPathOverride: null)
    {
    }

    public SqliteBrainStateStore(IDeepWorkOutputActionCoordinator deepWorkOutputActionCoordinator)
        : this(
            deepWorkOutputActionCoordinator,
            new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
            new FileRuntimePolicyProvider(),
            dbPathOverride: null)
    {
    }

    public SqliteBrainStateStore(
        IDeepWorkOutputActionCoordinator deepWorkOutputActionCoordinator,
        IOverloadBrightnessActionCoordinator overloadBrightnessActionCoordinator)
        : this(
            deepWorkOutputActionCoordinator,
            overloadBrightnessActionCoordinator,
            new FileRuntimePolicyProvider(),
            dbPathOverride: null)
    {
    }

    public SqliteBrainStateStore(
        IDeepWorkOutputActionCoordinator deepWorkOutputActionCoordinator,
        IOverloadBrightnessActionCoordinator overloadBrightnessActionCoordinator,
        IRuntimePolicyProvider runtimePolicyProvider)
        : this(
            deepWorkOutputActionCoordinator,
            overloadBrightnessActionCoordinator,
            runtimePolicyProvider,
            dbPathOverride: null)
    {
    }

    internal SqliteBrainStateStore(
        IDeepWorkOutputActionCoordinator deepWorkOutputActionCoordinator,
        IOverloadBrightnessActionCoordinator overloadBrightnessActionCoordinator,
        string? dbPathOverride)
        : this(
            deepWorkOutputActionCoordinator,
            overloadBrightnessActionCoordinator,
            new FileRuntimePolicyProvider(),
            dbPathOverride)
    {
    }

    internal SqliteBrainStateStore(
        IDeepWorkOutputActionCoordinator deepWorkOutputActionCoordinator,
        IOverloadBrightnessActionCoordinator overloadBrightnessActionCoordinator,
        IRuntimePolicyProvider runtimePolicyProvider,
        string? dbPathOverride)
    {
        _deepWorkOutputActionCoordinator = deepWorkOutputActionCoordinator
            ?? throw new ArgumentNullException(nameof(deepWorkOutputActionCoordinator));
        _overloadBrightnessActionCoordinator = overloadBrightnessActionCoordinator
            ?? throw new ArgumentNullException(nameof(overloadBrightnessActionCoordinator));
        _runtimePolicyProvider = runtimePolicyProvider
            ?? throw new ArgumentNullException(nameof(runtimePolicyProvider));
        _dbPath = string.IsNullOrWhiteSpace(dbPathOverride)
            ? DbPaths.GetCoreStateDbPath()
            : dbPathOverride;
        string? dbDirectory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }
        _connectionString = DbOpen.BuildConnectionString(_dbPath);
        EnsureSchema();
        TryReconcileNotificationSuppressionOnStartup();
        TryReconcileBrightnessActionOnStartup();
        ScheduleBackgroundVacuumIfNeeded();
    }

    public string ReadActivePolicyVersion()
    {
        return _runtimePolicyProvider.GetEnvelope().Version;
    }

    public void WriteSnapshot(AgentBrainSnapshot snapshot)
    {
        if (!BucketFinalizerWriteGuard.IsActive)
        {
            throw new InvalidOperationException(
                "Direct WriteSnapshot is blocked. Route snapshot writes through BucketFinalizerStateStore/BucketFinalizer.");
        }

        WriteSnapshotCore(snapshot, inputBucketRow: null);
    }

    void IBucketFinalizerDbWriter.WriteFinalizedBucketSnapshot(AgentBrainSnapshot snapshot, InputBucketRow inputBucketRow)
    {
        using IDisposable _ = BucketFinalizerWriteGuard.Enter();
        WriteSnapshotCore(snapshot, inputBucketRow);
    }

    void IBucketFinalizerDbWriter.WriteInputSignalWindow(AgentInputSignalWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                InsertInputSignalWindow(connection, tx, window);
                PruneIfNeeded(connection, tx, window.AcceptedAtUtc);
                tx.Commit();
            }
            catch
            {
                TryRollback(tx);
                throw;
            }
        }
    }

    private void TryReconcileNotificationSuppressionOnStartup()
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                RuntimeNotificationSuppressionSettings notificationPolicy = _runtimePolicyProvider.GetEnvelope().Settings.NotificationSuppression;
                NotificationSuppressionRuntimeState existingState = ReadNotificationSuppressionRuntimeState(connection, tx);
                NotificationSuppressionStartupReconciliation reconciliation =
                    _deepWorkOutputActionCoordinator.ReconcileOnStartup(existingState, nowUtc, notificationPolicy);

                bool stateChanged = !existingState.Equals(reconciliation.RuntimeState);
                if (stateChanged || reconciliation.ShouldLogDiagnosticEvent)
                {
                    long bucketSeq = Math.Max(
                        existingState.LastTransitionSourceBucketSeq,
                        reconciliation.RuntimeState.LastTransitionSourceBucketSeq);
                    string updatedUtc = nowUtc.ToString("O");
                    UpsertRuntimeStateValue(
                        connection,
                        tx,
                        PortaExecutionConstants.NotificationSuppression.RuntimeStateKey,
                        BuildNotificationSuppressionRuntimeStateJson(
                            bucketSeq,
                            updatedUtc,
                            reconciliation.RuntimeState),
                        updatedUtc);
                }

                if (reconciliation.ShouldLogDiagnosticEvent)
                {
                    RuntimePolicyEnvelope policyEnvelope = _runtimePolicyProvider.GetEnvelope();
                    InsertStartupNotificationSuppressionDiagnosticEvent(
                        connection,
                        tx,
                        nowUtc,
                        reconciliation,
                        policyEnvelope);
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                TryRollback(tx);
                Console.WriteLine($"startup_notification_reconcile_failed error={ex.GetType().Name}:{ex.Message}");
            }
        }
    }

    private void TryReconcileBrightnessActionOnStartup()
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                RuntimeBrightnessAdjustmentSettings brightnessPolicy = _runtimePolicyProvider.GetEnvelope().Settings.BrightnessAdjustment;
                BrightnessActionRuntimeState existingState = ReadBrightnessActionRuntimeState(connection, tx);
                BrightnessStartupReconciliation reconciliation = _overloadBrightnessActionCoordinator.ReconcileOnStartup(
                    existingState,
                    nowUtc,
                    brightnessPolicy);

                if (reconciliation.Reconciled || !existingState.Equals(reconciliation.RuntimeState))
                {
                    long bucketSeq = Math.Max(
                        existingState.LastTransitionSourceBucketSeq,
                        reconciliation.RuntimeState.LastTransitionSourceBucketSeq);
                    string updatedUtc = nowUtc.ToString("O");
                    UpsertRuntimeStateValue(
                        connection,
                        tx,
                        PortaExecutionConstants.BrightnessAdjustment.RuntimeStateKey,
                        BuildBrightnessActionRuntimeStateJson(
                            bucketSeq,
                            updatedUtc,
                            reconciliation.RuntimeState),
                        updatedUtc);
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                TryRollback(tx);
                Console.WriteLine($"startup_brightness_reconcile_failed error={ex.GetType().Name}:{ex.Message}");
            }
        }
    }

    private void WriteSnapshotCore(AgentBrainSnapshot snapshot, InputBucketRow? inputBucketRow)
    {
        snapshot = BucketPrivacyContract.SanitizeForBucketPersistence(snapshot);
        InputBucketRow mapped = inputBucketRow
            ?? throw new InvalidOperationException(
                "PrimitiveComputer contract violation: snapshot persistence requires finalized input bucket row.");
        long bucketSeq = mapped.BucketSeq;

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                RuntimePolicyEnvelope policyEnvelope = _runtimePolicyProvider.GetEnvelope();
                RuntimePolicySettings policy = policyEnvelope.Settings;
                // Finalize contract write order:
                // 1) input_buckets UPSERT
                // 2) baseline gating/update decision (same transaction as bucket finalize)
                // 3) robust bounds+z normalization (finalized bucket only)
                // 4) primitive_buckets UPSERT
                // 5) state_buckets UPSERT
                // 6) runtime_state updates
                // Any failure in this block rolls back the entire bucket finalize (fail-closed determinism).
                UpsertInputBucket(connection, tx, mapped);
                // Baseline updates are finalize-only to keep deterministic finalized-bucket semantics.
                BaselineGatingResult gating = BaselineManager.UpdateForFinalizedBucket(connection, tx, mapped);
                RobustNormalizationSnapshot? robust = BaselineManager.ComputeRobustNormalizationForBucket(connection, tx, mapped);
                PrimitiveComputationResult primitive = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, mapped, robust);

                AgentBrainSnapshot primitiveSnapshot = snapshot with
                {
                    TypingBurstIntensity = primitive.TypingRaw,
                    ScrollVelocityIndex = primitive.ScrollRaw,
                    FocusSwitchRate = primitive.FocusRaw,
                    SmoothedTypingBurstIntensity = primitive.TypingSmoothed,
                    SmoothedScrollVelocityIndex = primitive.ScrollSmoothed,
                    SmoothedFocusSwitchRate = primitive.FocusSmoothed
                };
                AgentBrainSnapshot gatedSnapshot = ApplyBaselineGating(
                    primitiveSnapshot,
                    gating,
                    mapped.QualityScore,
                    mapped.Partial == 1);
                // Phase 2b — meeting truth for this bucket, computed ONCE: feeds both the
                // attention-segment attribution (idle in meeting = attentive) and the break
                // shield below.
                bool meetingInProgress = Porta.Agent.Common.Analysis.AppCategoryClassifier.IsMeetingApp(mapped.DominantAppHash);
                DerivedSessionMetricsResult derivedSession = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
                    connection,
                    tx,
                    mapped,
                    primitive,
                    gating,
                    meetingInProgress);
                OverloadInferenceResult overloadInference = OverloadInferenceComputer.ComputeForFinalizedBucket(
                    gatedSnapshot,
                    mapped,
                    primitive,
                    gating,
                    robust,
                    derivedSession.TaskContinuitySeconds);
                string trustTier = CalibrationTruth.ResolveTrustTier(
                    gating.CalibrationStage,
                    overloadInference.Confidence,
                    overloadInference.Quality,
                    mapped.Partial == 1 || derivedSession.Partial);
                OverloadStabilizationRuntimeState previousOverloadStabilization = ReadOverloadStabilizationRuntimeState(connection, tx);
                OverloadStabilizationResult overloadStabilization = OverloadStabilizationComputer.ComputeForFinalizedBucket(
                    mapped,
                    overloadInference,
                    previousOverloadStabilization,
                    policy.OverloadStabilization);
                DeepWorkStabilizationRuntimeState previousDeepWorkStabilization = ReadDeepWorkStabilizationRuntimeState(connection, tx);
                DeepWorkStabilizationResult deepWorkStabilization = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
                    mapped,
                    overloadInference,
                    derivedSession,
                    gating,
                    previousDeepWorkStabilization,
                    policy.DeepWorkDetection,
                    policy.OverloadStabilization);
                NotificationSuppressionRuntimeState previousNotificationSuppressionState = ReadNotificationSuppressionRuntimeState(
                    connection,
                    tx);
                DeepWorkTransitionExecution deepWorkTransitionExecution = _deepWorkOutputActionCoordinator.HandleDeepWorkTransition(
                    new DeepWorkTransitionContext(
                        BucketSeq: mapped.BucketSeq,
                        BucketEndUtc: DateTimeOffset.FromUnixTimeMilliseconds(Time.bucketStartEnd(mapped.BucketSeq).endUtcMs),
                        PreviousDeepWorkActive: previousDeepWorkStabilization.DeepWorkActive,
                        CurrentDeepWorkActive: deepWorkStabilization.DeepWorkActive
                            && gating.SystemActionsEnabled
                            && CalibrationTruth.AllowsAutomaticActionsForTrust(trustTier),
                        BaselineReady: gating.BaselineReady,
                        LowQualityPaused: deepWorkStabilization.LowQualityPaused,
                        Confidence: overloadInference.Confidence),
                    previousNotificationSuppressionState,
                    policy.NotificationSuppression);
                BrightnessActionRuntimeState previousBrightnessActionState = ReadBrightnessActionRuntimeState(connection, tx);
                OverloadBrightnessTransitionExecution overloadBrightnessTransitionExecution = _overloadBrightnessActionCoordinator.HandleOverloadTransition(
                    new OverloadTransitionContext(
                        BucketSeq: mapped.BucketSeq,
                        BucketEndUtc: DateTimeOffset.FromUnixTimeMilliseconds(Time.bucketStartEnd(mapped.BucketSeq).endUtcMs),
                        PreviousOverloadActive: previousOverloadStabilization.OverloadActive,
                        CurrentOverloadActive: overloadStabilization.OverloadActive
                            && gating.SystemActionsEnabled
                            && CalibrationTruth.AllowsAutomaticActionsForTrust(trustTier),
                        LowQualityPaused: overloadStabilization.LowQualityPaused,
                        OverloadLikelihood: overloadInference.OverloadLikelihood,
                        Confidence: overloadInference.Confidence),
                    previousBrightnessActionState,
                    policy.BrightnessAdjustment);
                long finalizeTsMs = Time.bucketStartEnd(mapped.BucketSeq).endUtcMs;
                BreakFocusWindowSnapshot breakFocusWindow = ReadBreakFocusWindowSnapshot(
                    connection,
                    tx,
                    mapped,
                    derivedSession,
                    overloadStabilization,
                    overloadInference,
                    policy.BreakPolicy);
                // Workstream 1: personal p80 gate + trend, and the acceptance-backoff streak
                // (with its morning / natural-rest resets) — both computed from THIS user's data.
                BreakPersonalizationSnapshot breakPersonalization = ResolveBreakPersonalization(
                    connection, tx, finalizeTsMs);
                int breakDismissStreak = UpdateBreakBackoffForBucket(
                    connection, tx, finalizeTsMs, derivedSession);
                BreakSuggestionDecision breakSuggestionDecision = BreakSuggestionPolicy.ShouldTriggerBreak(
                    gating,
                    overloadStabilization,
                    overloadInference,
                    derivedSession,
                    breakFocusWindow,
                    resolveCooldown: () => ReadBreakPolicyCooldownSnapshot(connection, tx, finalizeTsMs),
                    policy.BreakPolicy,
                    meetingInProgress: meetingInProgress,
                    personalization: breakPersonalization);
                AgentBrainSnapshot inferredSnapshot = ApplyOverloadInference(gatedSnapshot, overloadInference);
                PrimitiveBucketWriteModel primitiveRow = BuildPrimitiveBucketRow(mapped, primitive, robust);
                StateBucketWriteModel stateRow = BuildStateBucketRow(
                    mapped,
                    inferredSnapshot,
                    primitive,
                    robust,
                    gating,
                    trustTier,
                    derivedSession,
                    overloadInference,
                    overloadStabilization,
                    deepWorkStabilization,
                    deepWorkTransitionExecution.RuntimeState,
                    overloadBrightnessTransitionExecution.RuntimeState);
                TryInsertDeepWorkNotificationEvent(
                    connection,
                    tx,
                    mapped,
                    deepWorkTransitionExecution,
                    deepWorkStabilization,
                    policyEnvelope);
                TryInsertOverloadBrightnessEvent(
                    connection,
                    tx,
                    mapped,
                    previousBrightnessActionState,
                    overloadBrightnessTransitionExecution,
                    overloadStabilization,
                    overloadInference,
                    policyEnvelope);
                TryInsertOverloadRecoveryEvent(
                    connection,
                    tx,
                    mapped,
                    overloadStabilization,
                    overloadInference,
                    policyEnvelope);
                UpsertPrimitiveBucket(connection, tx, primitiveRow);
                UpsertStateBucket(connection, tx, stateRow);
                // InsertLegacySnapshot removed: ReadTimeline now reads from state_buckets
                // directly (Step 9). brain_snapshots is no longer written to; a migration
                // deletes all existing rows and runs VACUUM to reclaim disk space.
                bool breakInserted = TryInsertBreakSuggestionPolicyEvent(
                    connection,
                    tx,
                    inferredSnapshot,
                    mapped,
                    gating,
                    trustTier,
                    derivedSession,
                    overloadInference,
                    overloadStabilization,
                    breakSuggestionDecision,
                    policyEnvelope,
                    breakPersonalization,
                    breakDismissStreak);
                PersistBreakPolicyRuntimeState(
                    connection,
                    tx,
                    finalizeTsMs,
                    breakSuggestionDecision,
                    breakInserted,
                    breakFocusWindow,
                    policyEnvelope,
                    breakDismissStreak);
                // Workstream 4: land this bucket's seconds on the active task (if any),
                // split by the pipeline's own attention classification.
                AttributeBucketToActiveTask(
                    connection,
                    tx,
                    Time.bucketSeconds,
                    derivedSession.AttentionSegmentKind);
                // ...and onto the hourly app rollup, which is the only record of this that
                // survives the 14-day prune. Same transaction, same inputs, so the two cannot
                // drift apart.
                AccumulateAppUsageRollup(
                    connection,
                    tx,
                    Time.bucketSeconds,
                    derivedSession.AttentionSegmentKind,
                    mapped.DominantAppHash,
                    mapped.StartUtc);
                TryInsertAutomaticInterventionEvent(connection, tx, inferredSnapshot);
                UpsertRuntimeStateForSnapshot(
                    connection,
                    tx,
                    inferredSnapshot,
                    mapped,
                    gating,
                    trustTier,
                    primitive,
                    derivedSession,
                    overloadInference,
                    overloadStabilization,
                    deepWorkStabilization,
                    deepWorkTransitionExecution.RuntimeState,
                    overloadBrightnessTransitionExecution.RuntimeState);
                PrimitiveComputer.PersistSPrevForFinalizedBucket(
                    connection,
                    tx,
                    mapped.TimestampUtc,
                    primitive);
                PruneIfNeeded(connection, tx, inferredSnapshot.TimestampUtc);
                tx.Commit();
            }
            catch (Exception ex)
            {
                TryRollback(tx);
                IncrementDiagnosticsCounter(connection, "bucket_txn_commit_failed_count");
                Console.WriteLine(
                    $"bucket_txn_failed bucket_seq={bucketSeq} state=pending_finalization error={ex.GetType().Name}:{ex.Message}");
                throw;
            }
        }
    }

    public void WriteControlEvent(AgentControlEvent controlEvent)
    {
        lock (_gate)
        {
            if (IsNonInterventionAction(controlEvent.Action))
            {
                return;
            }

            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            InsertInterventionEvent(connection, tx, controlEvent);
            PruneIfNeeded(connection, tx, controlEvent.TimestampUtc);

            tx.Commit();
        }
    }

    public void WriteSystemChange(AgentSystemChange change)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO system_changes (
                    timestamp_utc,
                    change_type,
                    previous_value_json,
                    new_value_json,
                    undo_info,
                    reason
                ) VALUES (
                    $ts, $change_type, $previous, $new, $undo, $reason
                );
                """;
            cmd.Parameters.AddWithValue("$ts", change.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$change_type", change.ChangeType);
            cmd.Parameters.AddWithValue("$previous", change.PreviousValueJson);
            cmd.Parameters.AddWithValue("$new", change.NewValueJson);
            cmd.Parameters.AddWithValue("$undo", change.UndoInfo);
            cmd.Parameters.AddWithValue("$reason", change.Reason);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, change.TimestampUtc);
            tx.Commit();
        }
    }

    public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO diagnostics_snapshots (
                    timestamp_utc,
                    backend_connected,
                    consent_active,
                    paused,
                    last_error,
                    policy_version,
                    schema_version,
                    agent_version,
                    last_event_timestamp_utc
                ) VALUES (
                    $ts, $backend, $consent, $paused, $last_error, $policy, $schema, $agent, $last_event
                );
                """;
            cmd.Parameters.AddWithValue("$ts", diagnostics.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$backend", diagnostics.BackendConnected ? 1 : 0);
            cmd.Parameters.AddWithValue("$consent", diagnostics.ConsentActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$paused", diagnostics.Paused ? 1 : 0);
            cmd.Parameters.AddWithValue("$last_error", diagnostics.LastError ?? string.Empty);
            cmd.Parameters.AddWithValue("$policy", diagnostics.PolicyVersion);
            cmd.Parameters.AddWithValue("$schema", diagnostics.SchemaVersion);
            cmd.Parameters.AddWithValue("$agent", diagnostics.AgentVersion);
            cmd.Parameters.AddWithValue("$last_event", diagnostics.LastEventTimestampUtc?.ToString("O") ?? string.Empty);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, diagnostics.TimestampUtc);
            tx.Commit();
        }
    }

    public void WriteConsent(AgentConsentRecord consent)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO consent (
                    timestamp_utc,
                    active,
                    version,
                    disclosure_hash
                ) VALUES (
                    $ts, $active, $version, $hash
                );
                """;
            cmd.Parameters.AddWithValue("$ts", consent.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$active", consent.Active ? 1 : 0);
            cmd.Parameters.AddWithValue("$version", consent.Version);
            cmd.Parameters.AddWithValue("$hash", consent.DisclosureHash);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, consent.TimestampUtc);
            tx.Commit();
        }
    }

    public AgentConsentRecord? ReadLatestConsent()
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                timestamp_utc,
                active,
                version,
                disclosure_hash
            FROM consent
            ORDER BY id DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AgentConsentRecord(
            TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
            Active: reader.GetInt32(1) == 1,
            Version: reader.GetString(2),
            DisclosureHash: reader.GetString(3));
    }

    public void WriteSettings(AgentSettingsRecord settings)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO settings (
                    timestamp_utc,
                    privacy_level,
                    regulation_intensity,
                    disabled_categories_json,
                    notification_reduction_enabled,
                    focus_mode_enabled,
                    animation_reduction_enabled,
                    temporary_app_restriction_enabled,
                    baseline_recalibration_started_utc,
                    baseline_recalibration_until_utc
                ) VALUES (
                    $ts, $privacy, $intensity, $disabled, $notif, $focus, $anim, $restrict, $recal_start, $recal_until
                );
                """;
            cmd.Parameters.AddWithValue("$ts", settings.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$privacy", settings.PrivacyLevel);
            cmd.Parameters.AddWithValue("$intensity", settings.RegulationIntensity);
            cmd.Parameters.AddWithValue("$disabled", JsonSerializer.Serialize(settings.DisabledCategories));
            cmd.Parameters.AddWithValue("$notif", settings.NotificationReductionEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$focus", settings.FocusModeEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$anim", settings.AnimationReductionEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$restrict", settings.TemporaryAppRestrictionEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$recal_start", settings.BaselineRecalibrationStartedUtc?.ToString("O") ?? string.Empty);
            cmd.Parameters.AddWithValue("$recal_until", settings.BaselineRecalibrationUntilUtc?.ToString("O") ?? string.Empty);
            cmd.ExecuteNonQuery();

            PruneIfNeeded(connection, tx, settings.TimestampUtc);
            tx.Commit();
        }
    }

    public AgentSettingsRecord? ReadLatestSettings()
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                timestamp_utc,
                privacy_level,
                regulation_intensity,
                disabled_categories_json,
                notification_reduction_enabled,
                focus_mode_enabled,
                animation_reduction_enabled,
                temporary_app_restriction_enabled,
                baseline_recalibration_started_utc,
                baseline_recalibration_until_utc
            FROM settings
            ORDER BY id DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AgentSettingsRecord(
            TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
            PrivacyLevel: reader.GetString(1),
            RegulationIntensity: reader.GetString(2),
            DisabledCategories: DeserializeStringList(reader.GetString(3)),
            NotificationReductionEnabled: reader.GetInt32(4) == 1,
            FocusModeEnabled: reader.GetInt32(5) == 1,
            AnimationReductionEnabled: reader.GetInt32(6) == 1,
            TemporaryAppRestrictionEnabled: reader.GetInt32(7) == 1,
            BaselineRecalibrationStartedUtc: ParseOptionalDateTimeOffset(reader.GetString(8)),
            BaselineRecalibrationUntilUtc: ParseOptionalDateTimeOffset(reader.GetString(9)));
    }

    public void DeleteLocalData()
    {
        lock (_gate)
        {
            try
            {
                using var connection = DbOpen.OpenConnection(_connectionString);
                using SqliteTransaction tx = connection.BeginTransaction();

                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    DELETE FROM input_signal_windows;
                    DELETE FROM input_buckets;
                    DELETE FROM state_buckets;
                    -- primitive_buckets was MISSING here until 2026-08-12, which made this a
                    -- PRIVACY defect and not a disk one: "delete my local data" reported success
                    -- and left 542 MB of the user's own behavioural primitives on the machine
                    -- (measured on the founder's install). The normalized, z-scored primitives
                    -- and their contributor/why JSON are exactly the kind of thing someone
                    -- pressing this button means to be rid of. Same root cause as its absence
                    -- from PruneIfNeeded: this list predates the table by 38 minutes.
                    DELETE FROM primitive_buckets;
                    -- The app rollup is per-application time the user produced. It goes with
                    -- everything else they typed and everything else that watched them.
                    DELETE FROM app_usage_rollup;
                    DELETE FROM events;
                    DELETE FROM interventions;
                    DELETE FROM system_changes;
                    DELETE FROM diagnostics;
                    DELETE FROM diagnostics_snapshots;
                    DELETE FROM consent;
                    DELETE FROM settings;
                    DELETE FROM brain_snapshots;
                    DELETE FROM ground_truth_labels;
                    DELETE FROM prompt_events;
                    DELETE FROM reminders;
                    DELETE FROM tasks;
                    -- Step 9a: project names are user-authored, so the local-data reset takes
                    -- them with everything else the user typed.
                    DELETE FROM projects;
                    DELETE FROM daily_goals;
                    DELETE FROM sqlite_sequence;
                    """;
                cmd.ExecuteNonQuery();
                tx.Commit();

                using var checkpointCmd = connection.CreateCommand();
                checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpointCmd.ExecuteNonQuery();

                using var vacuumCmd = connection.CreateCommand();
                vacuumCmd.CommandText = "VACUUM;";
                vacuumCmd.ExecuteNonQuery();
            }
            catch
            {
                // Fallback: if SQL-level purge fails, attempt file reset.
                TryDeleteDbFiles();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            EnsureSchema();
            _lastAutoEventAction = null;
            _lastAutoEventAtUtc = DateTimeOffset.MinValue;
            _activeIntervention = null;
            _lastPruneUtc = DateTimeOffset.MinValue;
        }
    }

    public AgentBrainSnapshot? ReadLatestSnapshot()
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        using ReadGateLease _ = AcquireReadGate();
        try
        {
            using var connection = DbOpen.OpenReadConnection(_connectionString, busyTimeoutMs: ResponsiveReadBusyTimeoutMs);
            return TryReadLatestSignalDrivenSnapshot(connection) ?? TryReadLatestLegacySnapshot(connection);
        }
        catch
        {
            return null;
        }
    }

    private static AgentBrainSnapshot? TryReadLatestSignalDrivenSnapshot(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                sb.timestamp_utc,
                sb.typing_burst_intensity_raw,
                sb.scroll_velocity_index_raw,
                sb.focus_switch_rate_raw,
                sb.typing_burst_intensity_smoothed,
                sb.scroll_velocity_index_smoothed,
                sb.focus_switch_rate_smoothed,
                sb.overload_likelihood,
                sb.confidence,
                sb.sustained_attention_duration_sec,
                sb.idle_fragmentation_index,
                sb.state_flags_json,
                sb.contributors_json
            FROM state_buckets sb
            ORDER BY sb.timestamp_utc DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        double typingRaw = ReadDoubleOrFallback(reader, 1, 0d);
        double scrollRaw = ReadDoubleOrFallback(reader, 2, 0d);
        double focusRaw = ReadDoubleOrFallback(reader, 3, 0d);
        double typingSmoothed = ReadDoubleOrFallback(reader, 4, typingRaw);
        double scrollSmoothed = ReadDoubleOrFallback(reader, 5, scrollRaw);
        double focusSmoothed = ReadDoubleOrFallback(reader, 6, focusRaw);
        double overloadLikelihood = ReadDoubleOrFallback(reader, 7, 0d);
        DateTimeOffset bucketStartUtc = DateTimeOffset.Parse(reader.GetString(0));
        LegacySnapshotMetadata legacyMetadata = TryReadLatestLegacySnapshotMetadataForBucket(connection, bucketStartUtc)
            ?? LegacySnapshotMetadata.Default;
        SignalDrivenStateMetrics signalState = ComputeSignalDrivenStateMetrics(
            typingSmoothed,
            focusSmoothed,
            overloadLikelihood);
        string stateFlagsJson = ReadStringOrFallback(reader, 11, "{\"schema_ver\":1}");

        return new AgentBrainSnapshot(
            TimestampUtc: bucketStartUtc,
            TypingBurstIntensity: typingRaw,
            ScrollVelocityIndex: scrollRaw,
            FocusSwitchRate: focusRaw,
            SmoothedTypingBurstIntensity: typingSmoothed,
            SmoothedScrollVelocityIndex: scrollSmoothed,
            SmoothedFocusSwitchRate: focusSmoothed,
            FocusStrength: signalState.FocusStrength,
            OverloadLikelihood: overloadLikelihood,
            FatigueProbability: signalState.FatigueProbability,
            CognitiveStability: signalState.CognitiveStability,
            DominantStateName: signalState.DominantStateName,
            DominantStateValue: signalState.DominantStateValue,
            Confidence: ReadDoubleOrFallback(reader, 8, 0d),
            LastAction: legacyMetadata.LastAction,
            ActivationWindowTriggered: legacyMetadata.ActivationWindowTriggered,
            CooldownTriggered: legacyMetadata.CooldownTriggered,
            ActivationWindowOpen: legacyMetadata.ActivationWindowOpen,
            Contributors: legacyMetadata.Contributors,
            PolicyId: legacyMetadata.PolicyId,
            Domain: legacyMetadata.Domain,
            EscalationLevel: legacyMetadata.EscalationLevel,
            PlannedEndTimestampUtc: legacyMetadata.PlannedEndTimestampUtc,
            ActualEndTimestampUtc: legacyMetadata.ActualEndTimestampUtc,
            CooldownEndTimestampUtc: legacyMetadata.CooldownEndTimestampUtc,
            SustainedAttentionDurationSec: ReadDoubleOrFallback(reader, 9, 0d),
            IdleFragmentationIndex: ReadDoubleOrFallback(reader, 10, 0d),
            OverloadActive: ParseOverloadActive(stateFlagsJson),
            StateFlagsJson: stateFlagsJson,
            ContributorsJson: ReadStringOrFallback(reader, 12, "{\"schema_ver\":1}"));
    }

    private static LegacySnapshotMetadata? TryReadLatestLegacySnapshotMetadataForBucket(
        SqliteConnection connection,
        DateTimeOffset bucketStartUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                last_action,
                activation_window_triggered,
                cooldown_triggered,
                activation_window_open,
                contributors_json,
                policy_id,
                domain,
                escalation_level,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                cooldown_end_timestamp_utc
            FROM brain_snapshots
            WHERE timestamp_utc >= $bucket_start_utc AND timestamp_utc < $bucket_end_utc
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$bucket_start_utc", bucketStartUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$bucket_end_utc", bucketStartUtc.AddSeconds(Time.bucketSeconds).ToString("O"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new LegacySnapshotMetadata(
            LastAction: ReadStringOrFallback(reader, 0, "monitor_only"),
            ActivationWindowTriggered: ReadBoolOrFallback(reader, 1, false),
            CooldownTriggered: ReadBoolOrFallback(reader, 2, false),
            ActivationWindowOpen: ReadBoolOrFallback(reader, 3, false),
            Contributors: DeserializeContributors(ReadStringOrFallback(reader, 4, "[]")),
            PolicyId: ReadStringOrFallback(reader, 5, "n/a"),
            Domain: ReadStringOrFallback(reader, 6, "control"),
            EscalationLevel: ReadStringOrFallback(reader, 7, "Inform"),
            PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 8, string.Empty)),
            ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 9, string.Empty)),
            CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 10, string.Empty)));
    }

    private static AgentBrainSnapshot? TryReadLatestLegacySnapshot(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                b.timestamp_utc,
                b.typing_burst_intensity,
                b.scroll_velocity_index,
                b.focus_switch_rate,
                b.smoothed_typing_burst_intensity,
                b.smoothed_scroll_velocity_index,
                b.smoothed_focus_switch_rate,
                b.focus_strength,
                b.overload_likelihood,
                b.fatigue_probability,
                b.cognitive_stability,
                b.dominant_state_name,
                b.dominant_state_value,
                b.confidence,
                b.last_action,
                b.activation_window_triggered,
                b.cooldown_triggered,
                b.activation_window_open,
                b.contributors_json,
                b.policy_id,
                b.domain,
                b.escalation_level,
                b.planned_end_timestamp_utc,
                b.actual_end_timestamp_utc,
                b.cooldown_end_timestamp_utc,
                sb.sustained_attention_duration_sec,
                sb.idle_fragmentation_index,
                sb.state_flags_json,
                sb.contributors_json
            FROM brain_snapshots b
            LEFT JOIN state_buckets sb ON sb.timestamp_utc = b.timestamp_utc
            ORDER BY b.id DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
            TypingBurstIntensity: reader.GetDouble(1),
            ScrollVelocityIndex: reader.GetDouble(2),
            FocusSwitchRate: reader.GetDouble(3),
            SmoothedTypingBurstIntensity: ReadDoubleOrFallback(reader, 4, fallback: reader.GetDouble(1)),
            SmoothedScrollVelocityIndex: ReadDoubleOrFallback(reader, 5, fallback: reader.GetDouble(2)),
            SmoothedFocusSwitchRate: ReadDoubleOrFallback(reader, 6, fallback: reader.GetDouble(3)),
            FocusStrength: reader.GetDouble(7),
            OverloadLikelihood: reader.GetDouble(8),
            FatigueProbability: reader.GetDouble(9),
            CognitiveStability: reader.GetDouble(10),
            DominantStateName: ReadStringOrFallback(reader, 11, ResolveDominantStateName(reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9))),
            DominantStateValue: ReadDoubleOrFallback(reader, 12, Math.Max(reader.GetDouble(7), Math.Max(reader.GetDouble(8), reader.GetDouble(9)))),
            Confidence: reader.GetDouble(13),
            LastAction: reader.GetString(14),
            ActivationWindowTriggered: ReadBoolOrFallback(reader, 15, false),
            CooldownTriggered: ReadBoolOrFallback(reader, 16, false),
            ActivationWindowOpen: ReadBoolOrFallback(reader, 17, false),
            Contributors: DeserializeContributors(reader.GetString(18)),
            PolicyId: ReadStringOrFallback(reader, 19, "n/a"),
            Domain: ReadStringOrFallback(reader, 20, "control"),
            EscalationLevel: ReadStringOrFallback(reader, 21, "Inform"),
            PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 22, string.Empty)),
            ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 23, string.Empty)),
            CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 24, string.Empty)),
            SustainedAttentionDurationSec: ReadDoubleOrFallback(reader, 25, 0d),
            IdleFragmentationIndex: ReadDoubleOrFallback(reader, 26, 0d),
            OverloadActive: ParseOverloadActive(ReadStringOrFallback(reader, 27, "{\"schema_ver\":1}")),
            StateFlagsJson: ReadStringOrFallback(reader, 27, "{\"schema_ver\":1}"),
            ContributorsJson: ReadStringOrFallback(reader, 28, "{\"schema_ver\":1}"));
    }

    public AgentLatestStateDetails? ReadLatestStateDetails()
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        using ReadGateLease _ = AcquireReadGate();
        try
        {
            using var connection = DbOpen.OpenReadConnection(_connectionString, busyTimeoutMs: ResponsiveReadBusyTimeoutMs);
            bool? baselineReady = null;
            double? baselineReadyPct = null;
            double? avgQuality2m = null;
            bool? coldStart = null;
            string? calibrationStage = null;
            bool? systemActionsEnabled = null;
            double? currentQualityScore = null;
            bool? partialData = null;
            double? focusAppEntropy = null;
            IReadOnlyList<string>? missingMajorChannels = null;
            string? breakPendingAction = null;
            string? breakPendingKind = null;
            string? breakPendingReasonCode = null;
            double? breakPendingEffectiveFocusSeconds = null;
            int? breakPendingEffectiveFocusWindowSeconds = null;
            bool? breakDeliveryBoundaryReached = null;
            string? breakDeliveryBoundaryKind = null;
            double? breakShortBreakEffectiveFocusSeconds = null;
            double? breakLongBreakEffectiveFocusSeconds = null;

            using (var baselineCmd = connection.CreateCommand())
            {
                baselineCmd.CommandText =
                    """
                    SELECT value_json
                    FROM runtime_state
                    WHERE key = 'baseline_status'
                    LIMIT 1;
                    """;
                object? baselineScalar = baselineCmd.ExecuteScalar();
                if (baselineScalar != null && baselineScalar != DBNull.Value)
                {
                    string baselineStatusJson = Convert.ToString(baselineScalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(baselineStatusJson))
                    {
                        try
                        {
                            using JsonDocument baselineDoc = JsonDocument.Parse(baselineStatusJson);
                            JsonElement baselineRoot = baselineDoc.RootElement;
                            if (TryReadBoolProperty(baselineRoot, "baseline_ready", out bool baselineReadyParsed))
                            {
                                baselineReady = baselineReadyParsed;
                            }

                            if (TryReadDoubleProperty(baselineRoot, "baseline_ready_pct", out double baselineReadyPctParsed))
                            {
                                baselineReadyPct = baselineReadyPctParsed;
                            }

                            if (TryReadDoubleProperty(baselineRoot, "avg_quality_2m", out double avgQuality2mParsed))
                            {
                                avgQuality2m = avgQuality2mParsed;
                            }

                            if (TryReadBoolProperty(baselineRoot, "cold_start", out bool coldStartParsed))
                            {
                                coldStart = coldStartParsed;
                            }

                            if (TryReadStringProperty(baselineRoot, "calibration_stage", out string? calibrationStageParsed)
                                && !string.IsNullOrWhiteSpace(calibrationStageParsed))
                            {
                                calibrationStage = CalibrationTruth.NormalizeStage(calibrationStageParsed);
                            }

                            if (TryReadBoolProperty(baselineRoot, "system_actions_enabled", out bool systemActionsEnabledParsed))
                            {
                                systemActionsEnabled = systemActionsEnabledParsed;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            using (var breakCmd = connection.CreateCommand())
            {
                breakCmd.CommandText =
                    """
                    SELECT value_json
                    FROM runtime_state
                    WHERE key = $key
                    LIMIT 1;
                    """;
                breakCmd.Parameters.AddWithValue("$key", PortaExecutionConstants.BreakPolicy.RuntimeStateKey);
                object? breakScalar = breakCmd.ExecuteScalar();
                if (breakScalar != null && breakScalar != DBNull.Value)
                {
                    string breakJson = Convert.ToString(breakScalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(breakJson))
                    {
                        try
                        {
                            using JsonDocument breakDoc = JsonDocument.Parse(breakJson);
                            JsonElement breakRoot = breakDoc.RootElement;
                            if (TryReadStringProperty(breakRoot, "pending_action", out string? pendingActionParsed)
                                && !string.IsNullOrWhiteSpace(pendingActionParsed))
                            {
                                breakPendingAction = pendingActionParsed.Trim().ToLowerInvariant();
                                breakPendingKind = PortaBreakSemantics.ResolveBreakKind(breakPendingAction);
                            }

                            if (TryReadStringProperty(breakRoot, "pending_break_kind", out string? pendingBreakKindParsed)
                                && !string.IsNullOrWhiteSpace(pendingBreakKindParsed))
                            {
                                breakPendingKind = PortaBreakSemantics.ResolveBreakKind(pendingBreakKindParsed);
                            }

                            if (TryReadStringProperty(breakRoot, "pending_reason_code", out string? pendingReasonCodeParsed)
                                && !string.IsNullOrWhiteSpace(pendingReasonCodeParsed))
                            {
                                breakPendingReasonCode = pendingReasonCodeParsed.Trim().ToLowerInvariant();
                            }

                            if (TryReadDoubleProperty(breakRoot, "pending_effective_focus_s", out double pendingEffectiveFocusParsed))
                            {
                                breakPendingEffectiveFocusSeconds = Math.Max(0d, pendingEffectiveFocusParsed);
                            }

                            if (TryReadIntProperty(breakRoot, "pending_effective_focus_window_s", out int pendingWindowParsed))
                            {
                                breakPendingEffectiveFocusWindowSeconds = Math.Max(0, pendingWindowParsed);
                            }

                            if (TryReadBoolProperty(breakRoot, "delivery_boundary_reached", out bool deliveryBoundaryParsed))
                            {
                                breakDeliveryBoundaryReached = deliveryBoundaryParsed;
                            }

                            if (TryReadStringProperty(breakRoot, "delivery_boundary_kind", out string? deliveryBoundaryKindParsed)
                                && !string.IsNullOrWhiteSpace(deliveryBoundaryKindParsed))
                            {
                                breakDeliveryBoundaryKind = deliveryBoundaryKindParsed.Trim().ToLowerInvariant();
                            }

                            if (TryReadDoubleProperty(breakRoot, "short_break_effective_focus_s", out double shortBreakParsed)
                                || TryReadDoubleProperty(breakRoot, "microbreak_effective_focus_s", out shortBreakParsed))
                            {
                                breakShortBreakEffectiveFocusSeconds = Math.Max(0d, shortBreakParsed);
                            }

                            if (TryReadDoubleProperty(breakRoot, "long_break_effective_focus_s", out double longBreakParsed)
                                || TryReadDoubleProperty(breakRoot, "short_break_effective_focus_s", out longBreakParsed))
                            {
                                breakLongBreakEffectiveFocusSeconds = Math.Max(0d, longBreakParsed);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            using (var inputCmd = connection.CreateCommand())
            {
                inputCmd.CommandText =
                    """
                    SELECT
                        quality_score,
                        partial,
                        contributors_json,
                        focus_app_entropy
                    FROM input_buckets
                    ORDER BY id DESC
                    LIMIT 1;
                    """;
                using SqliteDataReader inputReader = inputCmd.ExecuteReader();
                if (inputReader.Read())
                {
                    currentQualityScore = ReadDoubleOrFallback(inputReader, 0, 0d);
                    partialData = ReadBoolOrFallback(inputReader, 1, false);
                    string contributorsJson = ReadStringOrFallback(inputReader, 2, "{\"schema_ver\":1}");
                    focusAppEntropy = ReadDoubleOrFallback(inputReader, 3, 0d);
                    try
                    {
                        using JsonDocument contributorsDoc = JsonDocument.Parse(contributorsJson);
                        JsonElement contributorsRoot = contributorsDoc.RootElement;
                        if (contributorsRoot.ValueKind == JsonValueKind.Object
                            && contributorsRoot.TryGetProperty("missing_major_channels", out JsonElement channels)
                            && channels.ValueKind == JsonValueKind.Array)
                        {
                            var parsedChannels = new List<string>();
                            foreach (JsonElement item in channels.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                {
                                    string? channel = item.GetString();
                                    if (!string.IsNullOrWhiteSpace(channel))
                                    {
                                        parsedChannels.Add(channel.Trim().ToLowerInvariant());
                                    }
                                }
                            }

                            missingMajorChannels = parsedChannels
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                        }
                    }
                    catch
                    {
                    }
                }
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    timestamp_utc,
                    overload_likelihood,
                    confidence,
                    sustained_attention_duration_sec,
                    idle_fragmentation_index,
                    state_flags_json,
                    contributors_json
                FROM state_buckets
                ORDER BY id DESC
                LIMIT 1;
                """;

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            string stateFlagsJson = ReadStringOrFallback(reader, 5, "{\"schema_ver\":1}");
            bool overloadActive = false;
            double taskContinuityScore = 0d;
            double taskContinuitySeconds = ReadDoubleOrFallback(reader, 3, 0d);
            string continuityState = "reset";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
                JsonElement root = doc.RootElement;
                overloadActive = TryReadBoolProperty(root, "overload_active", out bool parsed) && parsed;
                taskContinuityScore = TryReadDoubleProperty(root, "task_continuity_score", out double parsedContinuityScore)
                    ? Math.Clamp(parsedContinuityScore, 0d, 1d)
                    : 0d;
                taskContinuitySeconds = TryReadDoubleProperty(root, "task_continuity_seconds", out double parsedContinuitySeconds)
                    ? Math.Max(0d, parsedContinuitySeconds)
                    : taskContinuitySeconds;
                continuityState = TryReadStringProperty(root, "continuity_state", out string? parsedContinuityState)
                    && !string.IsNullOrWhiteSpace(parsedContinuityState)
                        ? parsedContinuityState.Trim().ToLowerInvariant()
                        : continuityState;
            }
            catch
            {
                overloadActive = false;
            }

            string effectiveCalibrationStage = CalibrationTruth.NormalizeStage(
                calibrationStage ?? (baselineReady == true
                    ? CalibrationTruth.StageCalibrated
                    : (coldStart == true ? CalibrationTruth.StageCold : CalibrationTruth.StageProvisional)));
            bool effectiveSystemActionsEnabled = systemActionsEnabled ?? CalibrationTruth.AllowsSystemActions(effectiveCalibrationStage);
            double confidence = ReadDoubleOrFallback(reader, 2, 0d);

            return new AgentLatestStateDetails(
                TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                OverloadLikelihood: ReadDoubleOrFallback(reader, 1, 0d),
                Confidence: confidence,
                SustainedAttentionDurationSec: ReadDoubleOrFallback(reader, 3, 0d),
                IdleFragmentationIndex: ReadDoubleOrFallback(reader, 4, 0d),
                FocusAppEntropy: focusAppEntropy ?? 0d,
                OverloadActive: overloadActive,
                StateFlagsJson: stateFlagsJson,
                ContributorsJson: ReadStringOrFallback(reader, 6, "{\"schema_ver\":1}"),
                BaselineReady: baselineReady,
                BaselineReadyPct: baselineReadyPct,
                AvgQuality2m: avgQuality2m,
                ColdStart: coldStart,
                CalibrationStage: effectiveCalibrationStage,
                SystemActionsEnabled: effectiveSystemActionsEnabled,
                TrustTier: CalibrationTruth.ResolveTrustTier(
                    effectiveCalibrationStage,
                    confidence,
                    currentQualityScore ?? 0d,
                    partialData == true),
                CurrentQualityScore: currentQualityScore,
                PartialData: partialData,
                MissingMajorChannels: missingMajorChannels,
                BreakPendingAction: breakPendingAction,
                BreakPendingKind: breakPendingKind,
                BreakPendingReasonCode: breakPendingReasonCode,
                BreakPendingEffectiveFocusSeconds: breakPendingEffectiveFocusSeconds,
                BreakPendingEffectiveFocusWindowSeconds: breakPendingEffectiveFocusWindowSeconds,
                BreakDeliveryBoundaryReached: breakDeliveryBoundaryReached,
                BreakDeliveryBoundaryKind: breakDeliveryBoundaryKind,
                BreakShortBreakEffectiveFocusSeconds: breakShortBreakEffectiveFocusSeconds,
                BreakLongBreakEffectiveFocusSeconds: breakLongBreakEffectiveFocusSeconds,
                TaskContinuityScore: taskContinuityScore,
                TaskContinuitySeconds: taskContinuitySeconds,
                ContinuityState: continuityState);
            }
            catch
            {
                return null;
            }
    }

    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds)
    {
        if (toUtc < fromUtc)
        {
            return [];
        }

        int bucket = Math.Clamp(bucketSeconds, 1, 3600);

        // When bucket > 1, push the bucketing into SQL so only one row per bucket is
        // returned. The old approach read ALL rows into memory first then reduced them in
        // C# — with 352K rows and a large WAL this blocked the calling thread for seconds,
        // exhausting the .NET thread pool and hanging the HTTP server.
        // When bucket == 1 (no reduction needed) apply a row cap to prevent runaway reads.
        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();

        if (bucket > 1)
        {
            // Groups by (id / stride) — id is the autoincrement primary key, written one row
            // per 5-second bucket, so it is sequential in time order. Integer division is
            // trivially fast (~0.1μs per row vs 5-10μs for julianday/strftime string parsing).
            // state_buckets has no bucket_seq column (that column only exists on input_buckets),
            // so id-based grouping is the correct approach here.
            int rawBucketsPerOutputBucket = Math.Max(1, bucket / Time.bucketSeconds);
            cmd.CommandText =
                """
                SELECT
                    sb.timestamp_utc,
                    sb.typing_burst_intensity_raw,
                    sb.scroll_velocity_index_raw,
                    sb.focus_switch_rate_raw,
                    sb.typing_burst_intensity_smoothed,
                    sb.scroll_velocity_index_smoothed,
                    sb.focus_switch_rate_smoothed,
                    sb.focus_strength,
                    sb.overload_likelihood,
                    sb.fatigue_probability,
                    sb.cognitive_stability,
                    sb.dominant_state_name,
                    sb.dominant_state_value,
                    sb.confidence,
                    sb.sustained_attention_duration_sec,
                    sb.idle_fragmentation_index,
                    sb.activation_window_triggered,
                    sb.cooldown_triggered,
                    sb.activation_window_open,
                    sb.contributors_json,
                    sb.state_flags_json
                FROM state_buckets sb
                INNER JOIN (
                    SELECT MAX(id) AS max_id
                    FROM state_buckets
                    WHERE timestamp_utc >= $from AND timestamp_utc <= $to
                    GROUP BY id / $raw_per_output
                ) latest ON sb.id = latest.max_id
                ORDER BY sb.timestamp_utc ASC;
                """;
            cmd.Parameters.AddWithValue("$raw_per_output", rawBucketsPerOutputBucket);
        }
        else
        {
            cmd.CommandText =
                """
                SELECT
                    sb.timestamp_utc,
                    sb.typing_burst_intensity_raw,
                    sb.scroll_velocity_index_raw,
                    sb.focus_switch_rate_raw,
                    sb.typing_burst_intensity_smoothed,
                    sb.scroll_velocity_index_smoothed,
                    sb.focus_switch_rate_smoothed,
                    sb.focus_strength,
                    sb.overload_likelihood,
                    sb.fatigue_probability,
                    sb.cognitive_stability,
                    sb.dominant_state_name,
                    sb.dominant_state_value,
                    sb.confidence,
                    sb.sustained_attention_duration_sec,
                    sb.idle_fragmentation_index,
                    sb.activation_window_triggered,
                    sb.cooldown_triggered,
                    sb.activation_window_open,
                    sb.contributors_json,
                    sb.state_flags_json
                FROM state_buckets sb
                WHERE sb.timestamp_utc >= $from AND sb.timestamp_utc <= $to
                ORDER BY sb.timestamp_utc ASC
                LIMIT 100000;
                """;
        }

        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$bucket", bucket);

        var rows = new List<AgentBrainSnapshot>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string stateFlagsJson = ReadStringOrFallback(reader, 20, "{\"schema_ver\":1}");
            rows.Add(new AgentBrainSnapshot(
                TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                TypingBurstIntensity: ReadDoubleOrFallback(reader, 1, 0d),
                ScrollVelocityIndex: ReadDoubleOrFallback(reader, 2, 0d),
                FocusSwitchRate: ReadDoubleOrFallback(reader, 3, 0d),
                SmoothedTypingBurstIntensity: ReadDoubleOrFallback(reader, 4, 0d),
                SmoothedScrollVelocityIndex: ReadDoubleOrFallback(reader, 5, 0d),
                SmoothedFocusSwitchRate: ReadDoubleOrFallback(reader, 6, 0d),
                FocusStrength: ReadDoubleOrFallback(reader, 7, 0d),
                OverloadLikelihood: ReadDoubleOrFallback(reader, 8, 0d),
                FatigueProbability: ReadDoubleOrFallback(reader, 9, 0d),
                CognitiveStability: ReadDoubleOrFallback(reader, 10, 0d),
                DominantStateName: ReadStringOrFallback(reader, 11, "focus"),
                DominantStateValue: ReadDoubleOrFallback(reader, 12, 0d),
                Confidence: ReadDoubleOrFallback(reader, 13, 0d),
                LastAction: string.Empty,
                ActivationWindowTriggered: ReadBoolOrFallback(reader, 16, false),
                CooldownTriggered: ReadBoolOrFallback(reader, 17, false),
                ActivationWindowOpen: ReadBoolOrFallback(reader, 18, false),
                Contributors: [],
                SustainedAttentionDurationSec: ReadDoubleOrFallback(reader, 14, 0d),
                IdleFragmentationIndex: ReadDoubleOrFallback(reader, 15, 0d),
                OverloadActive: ParseOverloadActive(stateFlagsJson),
                StateFlagsJson: stateFlagsJson,
                ContributorsJson: ReadStringOrFallback(reader, 19, "{\"schema_ver\":1}")));
        }

        return rows;
    }

    /// <summary>
    /// Digest redesign (2026-08-11) — the SHAPE of the day, one row per local hour that recorded
    /// anything: focused / at the machine / away, in seconds.
    ///
    /// Uses the capture pipeline's OWN attention classification, exactly as
    /// <see cref="AttributeBucketToActiveTask"/> does, so the timeline and the per-task numbers can
    /// never tell different stories: away is away, distraction kinds are "at the machine", and
    /// everything else (focus, meeting, context excursion) is focused. No new inference.
    ///
    /// An hour with NO buckets is simply absent from the result. That is deliberate and load-bearing:
    /// absent means nothing was recorded (the machine was off), which is not the same claim as
    /// "away" — away says the user was there and idle. The email renders an absent hour as empty
    /// track, so nothing there reads as nothing.
    /// </summary>
    public IReadOnlyList<AgentDayTimelineHour> ReadDayTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan localOffset)
    {
        var byHour = new Dictionary<int, (double Focus, double Active, double Away)>();
        if (toUtc <= fromUtc)
        {
            return Array.Empty<AgentDayTimelineHour>();
        }

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT timestamp_utc,
                   LOWER(COALESCE(json_extract(state_flags_json, '$.attention_segment_kind'), 'focus'))
            FROM state_buckets
            WHERE timestamp_utc >= $from AND timestamp_utc < $to
            """;
        cmd.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$to", toUtc.ToUniversalTime().ToString("O"));

        double perBucket = Time.bucketSeconds;
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)
                || !DateTimeOffset.TryParse(
                    reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset stampUtc))
            {
                continue;
            }

            string kind = reader.IsDBNull(1) ? "focus" : reader.GetString(1);
            int hour = stampUtc.ToOffset(localOffset).Hour;
            (double focus, double active, double away) = byHour.TryGetValue(hour, out var current)
                ? current
                : (0d, 0d, 0d);

            if (AttentionSegmentKinds.IsAway(kind))
            {
                away += perBucket;
            }
            else if (AttentionSegmentKinds.IsDistraction(kind))
            {
                active += perBucket;
            }
            else
            {
                focus += perBucket;
            }

            byHour[hour] = (focus, active, away);
        }

        return byHour
            .OrderBy(pair => pair.Key)
            .Select(pair => new AgentDayTimelineHour(
                HourLocal: pair.Key,
                FocusSeconds: Math.Round(pair.Value.Focus, 1),
                ActiveSeconds: Math.Round(pair.Value.Active, 1),
                AwaySeconds: Math.Round(pair.Value.Away, 1)))
            .ToList();
    }

    public IReadOnlyDictionary<string, double> ReadFocusAppDwellSeconds(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        if (toUtc <= fromUtc)
        {
            return totals;
        }

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT focus_app_hash, window_start_utc, window_end_utc
            FROM input_signal_windows
            WHERE window_start_utc >= $from AND window_start_utc < $to AND focus_app_hash <> ''
            """;
        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string hash = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrEmpty(hash))
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(reader.GetString(1), out DateTimeOffset start)
                || !DateTimeOffset.TryParse(reader.GetString(2), out DateTimeOffset end))
            {
                continue;
            }

            double seconds = (end - start).TotalSeconds;
            if (seconds <= 0d)
            {
                continue;
            }

            // Clamp a single window's contribution so a clock jump or bad row can't dominate a day.
            seconds = Math.Min(seconds, 600d);
            totals[hash] = totals.TryGetValue(hash, out double existing) ? existing + seconds : seconds;
        }

        return totals;
    }

    public IReadOnlyList<AgentInputSignalWindow> ReadInputSignalWindows(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit = 50000)
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        if (toUtc < fromUtc)
        {
            return [];
            }

            int boundedLimit = Math.Clamp(limit, 1, 200000);
            var rows = new List<AgentInputSignalWindow>(Math.Min(boundedLimit, 4096));

            using ReadGateLease _ = AcquireReadGate();
            using var connection = DbOpen.OpenReadConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    accepted_at_utc,
                    schema_ver,
                    agent_instance_id,
                    session_id,
                    window_start_utc,
                    window_end_utc,
                    keyboard_keydown_count,
                    keyboard_backspace_count,
                    mouse_wheel_delta,
                    mouse_move_dx,
                    mouse_move_dy,
                    focus_app_hash,
                    idle_seconds,
                    last_input_event_utc,
                    keyboard_iki_mean_ms,
                    keyboard_iki_std_ms,
                    keyboard_iki_cv,
                    keyboard_burst_count,
                    keyboard_burst_rate,
                    keyboard_correction_spike_count,
                    keyboard_correction_spike_rate,
                    mouse_move_sample_count,
                    mouse_path_length_px,
                    mouse_speed_mean_px_per_sec,
                    mouse_jerk_p95,
                    mouse_micro_correction_count,
                    mouse_micro_correction_rate,
                    mouse_wheel_event_count,
                    mouse_scroll_burst_count,
                    mouse_scroll_burst_rate,
                    mouse_scroll_speed_mean,
                    focus_observation_count,
                    focus_switch_count,
                    focus_active_app_count,
                    focus_app_entropy,
                    idle_observation_count,
                    idle_seconds_within_window,
                    micro_idle_count,
                    micro_idle_seconds,
                    break_seconds_within_window,
                    short_idle_boundary_observed,
                    notification_toasts_enabled
                FROM input_signal_windows
                WHERE window_end_utc >= $from AND window_start_utc <= $to
                ORDER BY window_start_utc ASC, id ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string focusAppHash = ReadStringOrFallback(reader, 11, string.Empty);
                rows.Add(new AgentInputSignalWindow(
                    AcceptedAtUtc: DateTimeOffset.Parse(reader.GetString(0)),
                    SchemaVer: reader.GetInt32(1),
                    AgentInstanceId: reader.GetString(2),
                    SessionId: reader.GetString(3),
                    WindowStartUtc: DateTimeOffset.Parse(reader.GetString(4)),
                    WindowEndUtc: DateTimeOffset.Parse(reader.GetString(5)),
                    KeyboardKeydownCount: reader.GetInt32(6),
                    KeyboardBackspaceCount: reader.GetInt32(7),
                    MouseWheelDelta: reader.GetInt32(8),
                    MouseMoveDx: reader.GetDouble(9),
                    MouseMoveDy: reader.GetDouble(10),
                    FocusAppHash: string.IsNullOrWhiteSpace(focusAppHash) ? null : focusAppHash,
                    IdleSeconds: reader.GetDouble(12),
                    LastInputEventUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 13, string.Empty)),
                    KeyboardIkiMeanMs: ReadNullableDouble(reader, 14),
                    KeyboardIkiStdMs: ReadNullableDouble(reader, 15),
                    KeyboardIkiCv: ReadNullableDouble(reader, 16),
                    KeyboardBurstCount: reader.GetInt32(17),
                    KeyboardBurstRate: ReadNullableDouble(reader, 18),
                    KeyboardCorrectionSpikeCount: reader.GetInt32(19),
                    KeyboardCorrectionSpikeRate: ReadNullableDouble(reader, 20),
                    MouseMoveSampleCount: reader.GetInt32(21),
                    MousePathLengthPx: ReadNullableDouble(reader, 22),
                    MouseSpeedMeanPxPerSec: ReadNullableDouble(reader, 23),
                    MouseJerkP95: ReadNullableDouble(reader, 24),
                    MouseMicroCorrectionCount: reader.GetInt32(25),
                    MouseMicroCorrectionRate: ReadNullableDouble(reader, 26),
                    MouseWheelEventCount: reader.GetInt32(27),
                    MouseScrollBurstCount: reader.GetInt32(28),
                    MouseScrollBurstRate: ReadNullableDouble(reader, 29),
                    MouseScrollSpeedMean: ReadNullableDouble(reader, 30),
                    FocusObservationCount: reader.GetInt32(31),
                    FocusSwitchCount: reader.GetInt32(32),
                    FocusActiveAppCount: reader.GetInt32(33),
                    FocusAppEntropy: ReadNullableDouble(reader, 34),
                    IdleObservationCount: reader.GetInt32(35),
                    IdleSecondsWithinWindow: ReadNullableDouble(reader, 36),
                    MicroIdleCount: reader.GetInt32(37),
                    MicroIdleSeconds: ReadNullableDouble(reader, 38),
                    BreakSecondsWithinWindow: ReadNullableDouble(reader, 39),
                    ShortIdleBoundaryObserved: ReadBoolOrFallback(reader, 40, false),
                    NotificationToastsEnabled: reader.IsDBNull(41) ? null : reader.GetInt32(41) == 1));
            }

            return rows;
    }

    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null)
    {
        int boundedLimit = Math.Clamp(limit, 1, 500);
        var rows = new List<AgentControlEvent>(boundedLimit);

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                id,
                ts_utc,
                timestamp_utc,
                action,
                trigger_state,
                trigger_value,
                confidence,
                duration_seconds,
                escalation,
                reversible,
                why,
                contributors_json,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state,
                idempotency_key,
                why_json,
                state_flags_json,
                (SELECT command_type FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS command_type,
                (SELECT execution_state FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS execution_state,
                (SELECT decided_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS decided_at_utc,
                (SELECT applied_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS applied_at_utc,
                (SELECT acked_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS acked_at_utc,
                (SELECT error_code FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS error_code,
                (SELECT executor_agent_instance_id FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS executor_agent_instance_id
            FROM events
            WHERE ($since = '' OR ts_utc >= $since)
              AND action <> 'none'
              AND action <> 'monitor_only'
            ORDER BY ts_utc DESC, id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", boundedLimit);
        cmd.Parameters.AddWithValue("$since", sinceUtc?.ToString("O") ?? string.Empty);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadControlEvent(reader));
        }

        return rows;
    }

    public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200)
    {
        int boundedLimit = Math.Clamp(limit, 1, 1000);
        long safeSinceId = Math.Max(0, sinceIdExclusive);
        var rows = new List<AgentControlEvent>(boundedLimit);

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                id,
                ts_utc,
                timestamp_utc,
                action,
                trigger_state,
                trigger_value,
                confidence,
                duration_seconds,
                escalation,
                reversible,
                why,
                contributors_json,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state,
                idempotency_key,
                why_json,
                state_flags_json,
                (SELECT command_type FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS command_type,
                (SELECT execution_state FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS execution_state,
                (SELECT decided_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS decided_at_utc,
                (SELECT applied_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS applied_at_utc,
                (SELECT acked_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS acked_at_utc,
                (SELECT error_code FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS error_code,
                (SELECT executor_agent_instance_id FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS executor_agent_instance_id
            FROM events
            WHERE id > $since_id
              AND action <> 'none'
              AND action <> 'monitor_only'
            ORDER BY id ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$since_id", safeSinceId);
        cmd.Parameters.AddWithValue("$limit", boundedLimit);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadControlEvent(reader));
        }

        return rows;
    }

    public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000)
    {
        if (toUtc < fromUtc)
        {
            return [];
        }

        int boundedLimit = Math.Clamp(limit, 1, 10000);
        var rows = new List<AgentControlEvent>();

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                id,
                ts_utc,
                timestamp_utc,
                action,
                trigger_state,
                trigger_value,
                confidence,
                duration_seconds,
                escalation,
                reversible,
                why,
                contributors_json,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state,
                idempotency_key,
                why_json,
                state_flags_json,
                (SELECT command_type FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS command_type,
                (SELECT execution_state FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS execution_state,
                (SELECT decided_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS decided_at_utc,
                (SELECT applied_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS applied_at_utc,
                (SELECT acked_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS acked_at_utc,
                (SELECT error_code FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS error_code,
                (SELECT executor_agent_instance_id FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS executor_agent_instance_id
            FROM events
            WHERE ts_utc >= $from AND ts_utc <= $to
              AND action <> 'none'
              AND action <> 'monitor_only'
            ORDER BY ts_utc ASC, id ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", boundedLimit);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadControlEvent(reader));
        }

        return rows;
    }

    public IReadOnlyList<AgentPendingIntervention> ReadPendingInterventions(string sessionId, int limit = 20)
    {
        string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : sessionId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return [];
        }

        lock (_gate)
        {
            int boundedLimit = Math.Clamp(limit, 1, 100);
            var rows = new List<(AgentPendingIntervention Pending, bool ClaimSession)>(boundedLimit);

            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    SELECT
                        i.id,
                        i.intervention_id,
                        i.session_id,
                        i.action,
                        i.command_type,
                        i.payload_json,
                        i.decided_at_utc,
                        i.execution_state,
                        i.policy_id,
                        i.domain,
                        i.confidence,
                        i.escalation_level,
                        i.what_changed
                    FROM interventions i
                    WHERE i.command_type <> ''
                      AND (
                          i.execution_state = 'decided'
                          OR (
                              i.command_type IN ('show_break_overlay', 'show_break_toast', 'show_stretch_break_toast')
                              AND i.execution_state IN ('overlay_started', 'accepted', 'overlay_recovered')
                          )
                      )
                      AND i.lifecycle_state <> 'deactivated'
                      AND (i.session_id = '' OR i.session_id = $session_id)
                      AND (i.planned_end_timestamp_utc = '' OR i.planned_end_timestamp_utc >= $now_utc)
                      AND i.id = (
                          SELECT MAX(newer.id)
                          FROM interventions newer
                          WHERE newer.intervention_id = i.intervention_id
                            AND newer.command_type <> ''
                            AND (newer.session_id = '' OR newer.session_id = $session_id)
                      )
                    ORDER BY i.decided_at_utc ASC, i.id ASC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$session_id", normalizedSessionId);
                cmd.Parameters.AddWithValue("$now_utc", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$limit", boundedLimit);

                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    long rowId = reader.GetInt64(0);
                    string storedSessionId = ReadStringOrFallback(reader, 2, string.Empty);
                    bool claimSession = string.IsNullOrWhiteSpace(storedSessionId);
                    string effectiveSessionId = claimSession ? normalizedSessionId : storedSessionId;
                    DateTimeOffset decidedAtUtc = ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 6, string.Empty))
                        ?? DateTimeOffset.UtcNow;
                    rows.Add((
                        new AgentPendingIntervention(
                            RowId: rowId,
                            InterventionId: ReadStringOrFallback(reader, 1, string.Empty),
                            SessionId: effectiveSessionId,
                            Action: reader.GetString(3),
                            CommandType: reader.GetString(4),
                            PayloadJson: ReadStringOrFallback(reader, 5, "{}"),
                            DecidedAtUtc: decidedAtUtc,
                            ExecutionState: reader.GetString(7),
                            PolicyId: ReadStringOrFallback(reader, 8, "n/a"),
                            Domain: ReadStringOrFallback(reader, 9, "control"),
                            Confidence: reader.GetDouble(10),
                            EscalationLevel: ReadStringOrFallback(reader, 11, "Inform"),
                            WhatChanged: ReadStringOrFallback(reader, 12, string.Empty)),
                        claimSession));
                }

                foreach ((AgentPendingIntervention pending, bool claimSession) in rows)
                {
                    if (!claimSession)
                    {
                        continue;
                    }

                    using var update = connection.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText =
                        """
                        UPDATE interventions
                        SET session_id = $session_id
                        WHERE id = $id
                          AND (session_id IS NULL OR session_id = '');
                        """;
                    update.Parameters.AddWithValue("$session_id", normalizedSessionId);
                    update.Parameters.AddWithValue("$id", pending.RowId);
                    update.ExecuteNonQuery();
                }

                tx.Commit();
                return rows.Select(static row => row.Pending).ToList();
            }
            catch
            {
                TryRollback(tx);
                throw;
            }
        }
    }

    // ── Phase 2c: reminders ─────────────────────────────────────────────────────────────
    // Service-side so reminders survive user-agent restarts, widget crashes, and updates.
    // NOT part of the 14-day PruneIfNeeded (a reminder set 3 weeks out must still fire);
    // done/dismissed rows are cleaned up after 30 days by ReadNextReminder's piggyback
    // cleanup, and the whole table is wiped by the full local-data reset.

    /// <summary>Creates a reminder. Returns null when the pending cap is reached.</summary>
    public AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc, DateTimeOffset nowUtc)
    {
        string cleanTitle = (title ?? string.Empty).Trim();
        if (cleanTitle.Length == 0)
        {
            cleanTitle = "Reminder";
        }

        if (cleanTitle.Length > PortaExecutionConstants.Reminders.MaxTitleLength)
        {
            cleanTitle = cleanTitle[..PortaExecutionConstants.Reminders.MaxTitleLength];
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            using var countCmd = connection.CreateCommand();
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM reminders WHERE state IN ('pending','fired');";
            long pending = Convert.ToInt64(countCmd.ExecuteScalar());
            if (pending >= PortaExecutionConstants.Reminders.MaxPending)
            {
                tx.Commit();
                return null;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO reminders (title, due_at_utc, created_at_utc, state, snooze_count, fired_at_utc)
                VALUES ($title, $due, $created, 'pending', 0, '');
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$title", cleanTitle);
            cmd.Parameters.AddWithValue("$due", dueAtUtc.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$created", nowUtc.ToUniversalTime().ToString("O"));
            long id = Convert.ToInt64(cmd.ExecuteScalar());
            tx.Commit();

            return new AgentReminder(
                Id: id,
                Title: cleanTitle,
                DueAtUtc: dueAtUtc.ToUniversalTime(),
                CreatedAtUtc: nowUtc.ToUniversalTime(),
                State: "pending",
                SnoozeCount: 0,
                FiredAtUtc: null);
        }
    }

    /// <summary>
    /// Returns the reminder the widget should show: the earliest FIRED (due, unacknowledged)
    /// one if any, else the earliest PENDING one. Also performs the lazy state transition
    /// (pending past due → fired) and the 30-day cleanup of done/dismissed rows — polling
    /// this method IS the reminder clock; no background timer needed.
    /// </summary>
    public AgentReminder? ReadNextReminder(DateTimeOffset nowUtc)
    {
        string nowRaw = nowUtc.ToUniversalTime().ToString("O");
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            using var fireCmd = connection.CreateCommand();
            fireCmd.Transaction = tx;
            fireCmd.CommandText =
                """
                UPDATE reminders SET state = 'fired', fired_at_utc = $now
                WHERE state = 'pending' AND due_at_utc <= $now;

                DELETE FROM reminders
                WHERE state IN ('done','dismissed') AND created_at_utc < $cleanupCutoff;
                """;
            fireCmd.Parameters.AddWithValue("$now", nowRaw);
            fireCmd.Parameters.AddWithValue(
                "$cleanupCutoff",
                nowUtc.AddDays(-PortaExecutionConstants.Reminders.DoneRetentionDays).ToUniversalTime().ToString("O"));
            fireCmd.ExecuteNonQuery();

            using var readCmd = connection.CreateCommand();
            readCmd.Transaction = tx;
            readCmd.CommandText =
                """
                SELECT id, title, due_at_utc, created_at_utc, state, snooze_count, fired_at_utc
                FROM reminders
                WHERE state IN ('fired','pending')
                ORDER BY CASE state WHEN 'fired' THEN 0 ELSE 1 END, due_at_utc ASC
                LIMIT 1;
                """;
            AgentReminder? next = null;
            using (SqliteDataReader reader = readCmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    string firedRaw = reader.GetString(6);
                    next = new AgentReminder(
                        Id: reader.GetInt64(0),
                        Title: reader.GetString(1),
                        DueAtUtc: DateTimeOffset.Parse(reader.GetString(2)),
                        CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(3)),
                        State: reader.GetString(4),
                        SnoozeCount: reader.GetInt32(5),
                        FiredAtUtc: string.IsNullOrEmpty(firedRaw) ? null : DateTimeOffset.Parse(firedRaw));
                }
            }

            tx.Commit();
            return next;
        }
    }

    /// <summary>
    /// Acts on a reminder: done | dismiss | snooze (+SnoozeMinutes, repeatable).
    /// Returns false when the id is unknown or already closed.
    /// </summary>
    public bool AcknowledgeReminder(long id, string action, DateTimeOffset nowUtc)
    {
        string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            switch (normalized)
            {
                case "done":
                case "dismiss":
                    cmd.CommandText =
                        """
                        UPDATE reminders SET state = $state
                        WHERE id = $id AND state IN ('pending','fired');
                        """;
                    cmd.Parameters.AddWithValue("$state", normalized == "done" ? "done" : "dismissed");
                    break;
                case "snooze":
                    cmd.CommandText =
                        """
                        UPDATE reminders
                        SET state = 'pending',
                            due_at_utc = $due,
                            snooze_count = snooze_count + 1,
                            fired_at_utc = ''
                        WHERE id = $id AND state IN ('pending','fired');
                        """;
                    cmd.Parameters.AddWithValue(
                        "$due",
                        nowUtc.AddMinutes(PortaExecutionConstants.Reminders.SnoozeMinutes).ToUniversalTime().ToString("O"));
                    break;
                default:
                    tx.Commit();
                    return false;
            }

            cmd.Parameters.AddWithValue("$id", id);
            int rows = cmd.ExecuteNonQuery();
            tx.Commit();
            return rows > 0;
        }
    }

    // ── Workstream 4: timed tasks (plan vs. reality) ──

    private const int MaxOpenTasks = 20;

    /// <summary>
    /// Step 9a (2026-08-12) — was 30 days. Extended on Yadin's instruction after measuring what a
    /// task row actually costs: 135 bytes, three a day, so a year is about 148 KB. Beside the
    /// 800 MB the 5-second capture pipeline holds, task history is free, and the projects page
    /// needs "how many days does this project span" to mean something.
    ///
    /// 400 rather than 365, so a project a year old does not vanish the day before its anniversary.
    /// Nothing depends on the sweep for correctness — every read is date-windowed by its own WHERE
    /// clause — so this changes disk use and nothing else.
    /// </summary>
    private const int TaskDoneRetentionDays = 400;
    public const int TaskExtendSeconds = 900;

    public AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc)
        => CreateTask(title, plannedSeconds, nowUtc, projectId: 0);

    public AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc, long projectId)
    {
        string cleanTitle = (title ?? string.Empty).Trim();
        if (cleanTitle.Length == 0 || plannedSeconds < 60 || plannedSeconds > 8 * 3600)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            using (var countCmd = connection.CreateCommand())
            {
                countCmd.Transaction = tx;
                countCmd.CommandText = "SELECT COUNT(*) FROM tasks WHERE state IN ('pending','active');";
                long open = (long)(countCmd.ExecuteScalar() ?? 0L);
                if (open >= MaxOpenTasks)
                {
                    tx.Commit();
                    return null;
                }
            }

            long id;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO tasks (created_at_utc, title, planned_seconds, state, order_index, project_id)
                    VALUES ($created, $title, $planned, 'pending',
                        (SELECT COALESCE(MAX(order_index), 0) + 1 FROM tasks),
                        -- An id that does not resolve to a live project is stored as untagged
                        -- rather than as a dangling pointer: the picker is the only writer, but a
                        -- stale id from a card left open across an archive must not invent a tag.
                        COALESCE((SELECT id FROM projects WHERE id = $project AND archived_at_utc = ''), 0));
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$created", nowUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$title", cleanTitle);
                cmd.Parameters.AddWithValue("$planned", plannedSeconds);
                cmd.Parameters.AddWithValue("$project", projectId);
                id = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            AgentTask? created = ReadTaskRow(connection, tx, id);
            tx.Commit();
            return created;
        }
    }

    /// <summary>
    /// Today's list: everything still open plus tasks closed since <paramref name="dayStartUtc"/>.
    /// Piggybacks the 30-day cleanup of closed rows, mirroring the reminders pattern.
    /// </summary>
    public IReadOnlyList<AgentTask> ReadTasks(DateTimeOffset dayStartUtc, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = tx;
                cleanup.CommandText =
                    """
                    DELETE FROM tasks
                    WHERE state IN ('done','abandoned') AND created_at_utc < $cutoff;
                    """;
                cleanup.Parameters.AddWithValue(
                    "$cutoff", nowUtc.AddDays(-TaskDoneRetentionDays).ToUniversalTime().ToString("O"));
                cleanup.ExecuteNonQuery();
            }

            var tasks = new List<AgentTask>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    $"""
                    {TaskSelect}
                    WHERE t.state IN ('pending','active')
                       OR (t.state IN ('done','abandoned') AND t.ended_at_utc >= $dayStart)
                    ORDER BY CASE t.state WHEN 'active' THEN 0 WHEN 'pending' THEN 1 ELSE 2 END,
                             t.order_index ASC, t.id ASC;
                    """;
                cmd.Parameters.AddWithValue("$dayStart", dayStartUtc.ToUniversalTime().ToString("O"));
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tasks.Add(MapTaskRow(reader));
                }
            }

            tx.Commit();
            return tasks;
        }
    }

    /// <summary>
    /// Acts on a task: start | done | abandon | extend. Start enforces the one-active
    /// invariant HERE (not in the UI): any currently active task returns to pending with
    /// its accumulated reality intact — starting task B while A runs is a switch, not an
    /// error. Returns the updated task, or null for unknown id / illegal transition.
    /// </summary>
    public AgentTask? ActOnTask(long id, string action, DateTimeOffset nowUtc, string? deferUntilDay = null)
    {
        string verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        string nowRaw = nowUtc.ToUniversalTime().ToString("O");
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            switch (verb)
            {
                case "start":
                    using (var pause = connection.CreateCommand())
                    {
                        pause.Transaction = tx;
                        pause.CommandText =
                            "UPDATE tasks SET state = 'pending' WHERE state = 'active' AND id <> $id;";
                        pause.Parameters.AddWithValue("$id", id);
                        pause.ExecuteNonQuery();
                    }

                    cmd.CommandText =
                        """
                        UPDATE tasks
                        SET state = 'active',
                            started_at_utc = CASE WHEN started_at_utc = '' THEN $now ELSE started_at_utc END
                        WHERE id = $id AND state = 'pending';
                        """;
                    break;
                case "done":
                case "abandon":
                    cmd.CommandText =
                        """
                        UPDATE tasks SET state = $state, ended_at_utc = $now
                        WHERE id = $id AND state IN ('pending','active');
                        """;
                    cmd.Parameters.AddWithValue("$state", verb == "done" ? "done" : "abandoned");
                    break;
                case "extend":
                    cmd.CommandText =
                        $"""
                        UPDATE tasks SET extended_seconds = extended_seconds + {TaskExtendSeconds}
                        WHERE id = $id AND state = 'active';
                        """;
                    break;
                // Complaint 3 (2026-08-11) — "Tomorrow" on the close-out card. Records WHEN the task
                // is due back rather than changing its state: it stays open, so deferring can never
                // lose work, and the evening card reads this to stop asking about it today.
                //
                // Applies to pending AND active, because the task being deferred is often the one
                // currently running. It does NOT stop the clock: if the user keeps working on it,
                // the measured time is still real and still lands on the task. Deferring is a
                // statement about the plan, not about the measurement.
                case "defer":
                    cmd.CommandText =
                        """
                        UPDATE tasks SET deferred_until_day = $day
                        WHERE id = $id AND state IN ('pending','active');
                        """;
                    cmd.Parameters.AddWithValue("$day", deferUntilDay ?? string.Empty);
                    break;
                default:
                    tx.Commit();
                    return null;
            }

            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.CommandText.Contains("$now", StringComparison.Ordinal))
            {
                cmd.Parameters.AddWithValue("$now", nowRaw);
            }

            int rows = cmd.ExecuteNonQuery();
            AgentTask? updated = rows > 0 ? ReadTaskRow(connection, tx, id) : null;
            tx.Commit();
            return updated;
        }
    }

    // ── Step 9a: projects ──
    //
    // LOCAL ONLY. Nothing here is added to any payload; the digest and the narrative are byte-for-
    // byte what they were before 9a.

    private const int MaxProjects = 200;
    private const int MaxProjectNameLength = 60;

    /// <summary>
    /// Creates a project, or returns the EXISTING one when the name already exists in any casing.
    /// Create-or-get rather than create-or-fail: the picker's "+ new" is the only caller, and a
    /// user who types a name he already has means that project — telling him off would be the
    /// wrong answer, and creating a second "nolan" would split a client's total in half.
    /// Returns null only for an empty name or once the cap is reached.
    /// </summary>
    public AgentProject? CreateProject(string name, DateTimeOffset nowUtc)
    {
        string clean = NormalizeProjectName(name);
        if (clean.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            AgentProject? existing = ReadProjectByName(connection, tx, clean);
            if (existing != null)
            {
                // An archived project whose name is typed again comes BACK rather than colliding
                // with the unique index. The user is asking for it by name; that is un-archiving.
                if (existing.Archived)
                {
                    using var revive = connection.CreateCommand();
                    revive.Transaction = tx;
                    revive.CommandText = "UPDATE projects SET archived_at_utc = '' WHERE id = $id;";
                    revive.Parameters.AddWithValue("$id", existing.Id);
                    revive.ExecuteNonQuery();
                    existing = ReadProjectById(connection, tx, existing.Id);
                }

                tx.Commit();
                return existing;
            }

            using (var countCmd = connection.CreateCommand())
            {
                countCmd.Transaction = tx;
                countCmd.CommandText = "SELECT COUNT(*) FROM projects;";
                long total = (long)(countCmd.ExecuteScalar() ?? 0L);
                if (total >= MaxProjects)
                {
                    tx.Commit();
                    return null;
                }
            }

            long id;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO projects (name, created_at_utc, archived_at_utc, order_index)
                    VALUES ($name, $created, '', (SELECT COALESCE(MAX(order_index), 0) + 1 FROM projects));
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$name", clean);
                cmd.Parameters.AddWithValue("$created", nowUtc.ToUniversalTime().ToString("O"));
                id = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            AgentProject? created = ReadProjectById(connection, tx, id);
            tx.Commit();
            return created;
        }
    }

    /// <summary>
    /// The picker's list, most-recently-USED first — the order that makes a six-item cap workable,
    /// because the projects you are working on this week float to the top on their own. Recency is
    /// the newest task created against the project, falling back to when the project was made.
    /// Archived projects are excluded unless asked for (Control Center asks).
    /// </summary>
    public IReadOnlyList<AgentProject> ReadProjects(bool includeArchived = false)
    {
        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT p.id, p.name, p.created_at_utc, p.archived_at_utc, p.order_index
            FROM projects p
            WHERE $includeArchived = 1 OR p.archived_at_utc = ''
            ORDER BY COALESCE(
                         (SELECT MAX(t.created_at_utc) FROM tasks t WHERE t.project_id = p.id),
                         p.created_at_utc) DESC,
                     p.id DESC
            LIMIT {MaxProjects};
            """;
        cmd.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);

        var projects = new List<AgentProject>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            projects.Add(MapProjectRow(reader));
        }

        return projects;
    }

    /// <summary>
    /// Re-tags a task. projectId 0 clears the tag back to untagged, which must always be possible:
    /// untagged is a valid permanent state, not an error to be corrected.
    ///
    /// Re-tagging is allowed at any time, including after time has been credited — Yadin's
    /// decision (2026-08-12). Every measured minute moves with the task, all of it or none of it,
    /// so a minute still belongs to exactly one project. The digest already sent for an earlier
    /// day is a snapshot of what was known then and is never regenerated.
    /// </summary>
    public AgentTask? SetTaskProject(long taskId, long projectId)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    UPDATE tasks
                    SET project_id =
                        COALESCE((SELECT id FROM projects WHERE id = $project AND archived_at_utc = ''), 0)
                    WHERE id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", taskId);
                cmd.Parameters.AddWithValue("$project", projectId);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    tx.Commit();
                    return null;
                }
            }

            AgentTask? updated = ReadTaskRow(connection, tx, taskId);
            tx.Commit();
            return updated;
        }
    }

    /// <summary>
    /// Renames a project. Returns null when the name is empty or already belongs to a DIFFERENT
    /// project — the unique index would refuse it anyway, and a silent failure here would look
    /// like a rename that worked.
    /// </summary>
    public AgentProject? RenameProject(long id, string name)
    {
        string clean = NormalizeProjectName(name);
        if (clean.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            AgentProject? clash = ReadProjectByName(connection, tx, clean);
            if (clash != null && clash.Id != id)
            {
                tx.Commit();
                return null;
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE projects SET name = $name WHERE id = $id;";
                cmd.Parameters.AddWithValue("$name", clean);
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    tx.Commit();
                    return null;
                }
            }

            AgentProject? renamed = ReadProjectById(connection, tx, id);
            tx.Commit();
            return renamed;
        }
    }

    /// <summary>
    /// Archives or un-archives. Never deletes: the project's tasks keep pointing at it, and some
    /// of that time has already been reported. Archiving takes it out of the picker and leaves
    /// every measured minute exactly where it was.
    /// </summary>
    public AgentProject? SetProjectArchived(long id, bool archived, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE projects SET archived_at_utc = $archived WHERE id = $id;";
                cmd.Parameters.AddWithValue(
                    "$archived", archived ? nowUtc.ToUniversalTime().ToString("O") : string.Empty);
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    tx.Commit();
                    return null;
                }
            }

            AgentProject? updated = ReadProjectById(connection, tx, id);
            tx.Commit();
            return updated;
        }
    }

    /// <summary>
    /// Collapses internal whitespace and clamps the length. Two names that differ only by spacing
    /// are the same project to a human, so they must be the same project to the unique index too.
    /// </summary>
    private static string NormalizeProjectName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        string collapsed = string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaxProjectNameLength
            ? collapsed[..MaxProjectNameLength].TrimEnd()
            : collapsed;
    }

    private const string ProjectColumns = "id, name, created_at_utc, archived_at_utc, order_index";

    private static AgentProject? ReadProjectById(SqliteConnection connection, SqliteTransaction tx, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {ProjectColumns} FROM projects WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapProjectRow(reader) : null;
    }

    private static AgentProject? ReadProjectByName(SqliteConnection connection, SqliteTransaction tx, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {ProjectColumns} FROM projects WHERE name = $name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$name", name);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapProjectRow(reader) : null;
    }

    private static AgentProject MapProjectRow(SqliteDataReader reader)
        => new(
            Id: reader.GetInt64(0),
            Name: reader.GetString(1),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(2)),
            Archived: !string.IsNullOrEmpty(reader.GetString(3)),
            OrderIndex: reader.GetInt32(4));

    // Step 9a — the project name is resolved in the SELECT rather than by a second call, because
    // the widget repaints every 4s. A LEFT JOIN, so an untagged task (project_id = 0) and a task
    // pointing at an archived project both still come back; the name is simply empty for the
    // former. Every task read goes through TaskSelect so the two cannot drift.
    private const string TaskColumns =
        "t.id, t.title, t.planned_seconds, t.state, t.order_index, t.created_at_utc, t.started_at_utc, " +
        "t.ended_at_utc, t.active_seconds, t.focused_seconds, t.offtask_seconds, t.extended_seconds, " +
        "t.deferred_until_day, t.project_id, COALESCE(p.name, '')";

    private const string TaskSelect =
        "SELECT " + TaskColumns + " FROM tasks t LEFT JOIN projects p ON p.id = t.project_id";

    private static AgentTask? ReadTaskRow(SqliteConnection connection, SqliteTransaction tx, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{TaskSelect} WHERE t.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapTaskRow(reader) : null;
    }

    private static AgentTask MapTaskRow(SqliteDataReader reader)
    {
        string started = reader.GetString(6);
        string ended = reader.GetString(7);
        return new AgentTask(
            Id: reader.GetInt64(0),
            Title: reader.GetString(1),
            PlannedSeconds: reader.GetInt32(2),
            State: reader.GetString(3),
            OrderIndex: reader.GetInt32(4),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(5)),
            StartedAtUtc: string.IsNullOrEmpty(started) ? null : DateTimeOffset.Parse(started),
            EndedAtUtc: string.IsNullOrEmpty(ended) ? null : DateTimeOffset.Parse(ended),
            ActiveSeconds: reader.GetDouble(8),
            FocusedSeconds: reader.GetDouble(9),
            OfftaskSeconds: reader.GetDouble(10),
            ExtendedSeconds: reader.GetInt32(11),
            DeferredUntilDay: reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            ProjectId: reader.IsDBNull(13) ? 0L : reader.GetInt64(13),
            ProjectName: reader.IsDBNull(14) ? string.Empty : reader.GetString(14));
    }

    /// <summary>
    /// Attribution (4.2): each finalized bucket's seconds land on the active task using the
    /// pipeline's OWN attention classification — no new inference. Away contributes nothing
    /// (the countdown pauses with the user); distraction kinds count as off-task; everything
    /// else (focus, meeting, context excursion) counts as focused. One row update per 5s
    /// bucket, only while a task is active.
    /// </summary>
    internal static void AttributeBucketToActiveTask(
        SqliteConnection connection,
        SqliteTransaction tx,
        double bucketSeconds,
        string? attentionSegmentKind)
    {
        if (bucketSeconds <= 0d || AttentionSegmentKinds.IsAway(attentionSegmentKind))
        {
            return;
        }

        bool offtask = AttentionSegmentKinds.IsDistraction(attentionSegmentKind);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            UPDATE tasks
            SET active_seconds = active_seconds + $seconds,
                focused_seconds = focused_seconds + $focused,
                offtask_seconds = offtask_seconds + $offtask
            WHERE state = 'active';
            """;
        cmd.Parameters.AddWithValue("$seconds", bucketSeconds);
        cmd.Parameters.AddWithValue("$focused", offtask ? 0d : bucketSeconds);
        cmd.Parameters.AddWithValue("$offtask", offtask ? bucketSeconds : 0d);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 2026-08-12 — the same bucket, landed on the hourly app rollup so it survives the 14-day
    /// prune. Runs in the SAME transaction as the task attribution above and off the same inputs,
    /// so the rollup can never disagree with the per-task numbers.
    ///
    /// Away is skipped for the same reason it is skipped above: away is not an app. The active
    /// task is read rather than assumed, and 0 when there is none — time not on a task is still
    /// time in an application, and it is most of a normal day.
    ///
    /// This is the ONLY place the task→app link is ever recorded. It cannot be reconstructed
    /// afterwards: input_signal_windows has no task column and tasks log only their first start.
    /// If this write is ever moved or made conditional, that history stops existing.
    /// </summary>
    /// <remarks>
    /// The app here is the bucket's DOMINANT app, which is the unit the pipeline already
    /// attributes by. The digest's "where your time went" sums per-window dwell instead, so the
    /// two can differ by a little on a bucket where focus switched mid-way. That is a real
    /// difference and not a bug: neither number is inferred, they are simply measured at different
    /// grains. Do not "reconcile" them by inventing a split.
    /// </remarks>
    internal static void AccumulateAppUsageRollup(
        SqliteConnection connection,
        SqliteTransaction tx,
        double bucketSeconds,
        string? attentionSegmentKind,
        string? dominantAppHash,
        string? bucketStartUtcRaw)
    {
        string app = (dominantAppHash ?? string.Empty).Trim();
        if (bucketSeconds <= 0d || app.Length == 0 || AttentionSegmentKinds.IsAway(attentionSegmentKind))
        {
            return;
        }

        // Fail closed: a bucket whose start cannot be read gets no rollup row rather than one
        // filed under a guessed hour.
        if (!DateTimeOffset.TryParse(
                bucketStartUtcRaw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTimeOffset bucketStartUtc))
        {
            return;
        }

        bool offtask = AttentionSegmentKinds.IsDistraction(attentionSegmentKind);

        long taskId = 0;
        using (var active = connection.CreateCommand())
        {
            active.Transaction = tx;
            active.CommandText = "SELECT id FROM tasks WHERE state = 'active' LIMIT 1;";
            object? scalar = active.ExecuteScalar();
            if (scalar is not null && scalar != DBNull.Value)
            {
                taskId = Convert.ToInt64(scalar);
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO app_usage_rollup
                (hour_start_utc, task_id, app_hash, focused_seconds, offtask_seconds, active_seconds, backfilled)
            VALUES ($hour, $task, $app, $focused, $offtask, $seconds, 0)
            ON CONFLICT(hour_start_utc, task_id, app_hash) DO UPDATE SET
                focused_seconds = focused_seconds + excluded.focused_seconds,
                offtask_seconds = offtask_seconds + excluded.offtask_seconds,
                active_seconds  = active_seconds  + excluded.active_seconds,
                -- A live write over a backfilled row makes the row measured: the backfill only
                -- ever claimed active_seconds, so once real numbers land the caveat is gone.
                backfilled      = 0;
            """;
        cmd.Parameters.AddWithValue("$hour", HourStartUtc(bucketStartUtc));
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$app", app);
        cmd.Parameters.AddWithValue("$focused", offtask ? 0d : bucketSeconds);
        cmd.Parameters.AddWithValue("$offtask", offtask ? bucketSeconds : 0d);
        cmd.Parameters.AddWithValue("$seconds", bucketSeconds);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The UTC hour a bucket belongs to, formatted exactly as the backfill's string surgery
    /// produces it so the two writers land on the same primary key.
    /// </summary>
    internal static string HourStartUtc(DateTimeOffset instant)
    {
        DateTimeOffset utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero)
            .ToString("O");
    }

    /// <summary>
    /// The rollup, resolved through tasks to projects at READ time — which is what makes a
    /// re-tag instant and permanent. One row per local hour, per project, per application.
    ///
    /// <paramref name="localOffset"/> is passed in for the same reason ReadDayTimeline takes it:
    /// the local hour is a local concept and this store holds UTC.
    ///
    /// A row whose task has aged out of the 400-day window resolves to untagged rather than
    /// vanishing — the time was still real, only its label is gone.
    /// </summary>
    public IReadOnlyList<AgentAppUsageHour> ReadAppUsage(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan localOffset)
    {
        var rows = new List<AgentAppUsageHour>();
        if (toUtc <= fromUtc)
        {
            return rows;
        }

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT r.hour_start_utc,
                   COALESCE(t.project_id, 0) AS project_id,
                   COALESCE(p.name, '')      AS project_name,
                   r.task_id,
                   r.app_hash,
                   r.focused_seconds,
                   r.offtask_seconds,
                   r.active_seconds,
                   r.backfilled
            FROM app_usage_rollup r
            LEFT JOIN tasks    t ON t.id = r.task_id
            LEFT JOIN projects p ON p.id = t.project_id
            WHERE r.hour_start_utc >= $from AND r.hour_start_utc < $to
            ORDER BY r.hour_start_utc;
            """;
        cmd.Parameters.AddWithValue("$from", HourStartUtc(fromUtc));
        cmd.Parameters.AddWithValue("$to", toUtc.ToUniversalTime().ToString("O"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(reader.GetString(0), out DateTimeOffset hourUtc))
            {
                continue;
            }

            DateTimeOffset local = hourUtc.ToOffset(localOffset);
            rows.Add(new AgentAppUsageHour(
                HourStartUtc: hourUtc,
                LocalDate: local.ToString("yyyy-MM-dd"),
                HourLocal: local.Hour,
                ProjectId: reader.GetInt64(1),
                ProjectName: reader.GetString(2),
                TaskId: reader.GetInt64(3),
                AppHash: reader.GetString(4),
                FocusedSeconds: reader.GetDouble(5),
                OfftaskSeconds: reader.GetDouble(6),
                ActiveSeconds: reader.GetDouble(7),
                Backfilled: reader.GetInt32(8) == 1));
        }

        return rows;
    }

    // ── Workstream D: conversational check-in goals (one row per local goal-day) ──

    private const string DailyGoalColumns =
        "id, local_date, goal_text, tracking_pref, midday_answer, midday_at_utc, eod_at_utc, plan_tomorrow, created_at_utc";

    /// <summary>Morning check-in: create or update today's goal + tracking preference. Leaves any
    /// midday/EOD state on an existing row untouched.</summary>
    public AgentDailyGoal? UpsertMorningGoal(string localDate, string goalText, string trackingPref, DateTimeOffset nowUtc)
    {
        string date = (localDate ?? string.Empty).Trim();
        if (date.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO daily_goals (local_date, created_at_utc, goal_text, tracking_pref)
                    VALUES ($date, $now, $goal, $pref)
                    ON CONFLICT(local_date) DO UPDATE SET
                        goal_text = $goal,
                        tracking_pref = $pref;
                    """;
                cmd.Parameters.AddWithValue("$date", date);
                cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$goal", (goalText ?? string.Empty).Trim());
                cmd.Parameters.AddWithValue("$pref", (trackingPref ?? string.Empty).Trim());
                cmd.ExecuteNonQuery();
            }

            AgentDailyGoal? goal = ReadDailyGoalRow(connection, tx, date);
            tx.Commit();
            return goal;
        }
    }

    public AgentDailyGoal? GetDailyGoal(string localDate)
    {
        string date = (localDate ?? string.Empty).Trim();
        if (date.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            AgentDailyGoal? goal = ReadDailyGoalRow(connection, tx, date);
            tx.Commit();
            return goal;
        }
    }

    /// <summary>Midday check-in: record the yes|partly|not_yet answer. Returns null if there is no
    /// goal row for the day or the answer is not recognized.</summary>
    public AgentDailyGoal? RecordMiddayGoalAnswer(string localDate, string answer, DateTimeOffset nowUtc)
    {
        string date = (localDate ?? string.Empty).Trim();
        string normalized = NormalizeMiddayAnswer(answer);
        if (date.Length == 0 || normalized.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            int rows;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "UPDATE daily_goals SET midday_answer = $ans, midday_at_utc = $now WHERE local_date = $date;";
                cmd.Parameters.AddWithValue("$ans", normalized);
                cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$date", date);
                rows = cmd.ExecuteNonQuery();
            }

            AgentDailyGoal? goal = rows > 0 ? ReadDailyGoalRow(connection, tx, date) : null;
            tx.Commit();
            return goal;
        }
    }

    /// <summary>End-of-day check-in: mark EOD reached and save tomorrow's one-line plan. Creates the
    /// day's row if the user never set a morning goal.</summary>
    public AgentDailyGoal? RecordEndOfDayPlan(string localDate, string planTomorrow, DateTimeOffset nowUtc)
    {
        string date = (localDate ?? string.Empty).Trim();
        if (date.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO daily_goals (local_date, created_at_utc, eod_at_utc, plan_tomorrow)
                    VALUES ($date, $now, $now, $plan)
                    ON CONFLICT(local_date) DO UPDATE SET
                        eod_at_utc = $now,
                        plan_tomorrow = $plan;
                    """;
                cmd.Parameters.AddWithValue("$date", date);
                cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$plan", (planTomorrow ?? string.Empty).Trim());
                cmd.ExecuteNonQuery();
            }

            AgentDailyGoal? goal = ReadDailyGoalRow(connection, tx, date);
            tx.Commit();
            return goal;
        }
    }

    internal static string NormalizeMiddayAnswer(string? answer)
    {
        return (answer ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "yes" => "yes",
            "partly" => "partly",
            "not_yet" or "not yet" or "notyet" => "not_yet",
            _ => string.Empty
        };
    }

    private static AgentDailyGoal? ReadDailyGoalRow(SqliteConnection connection, SqliteTransaction tx, string localDate)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {DailyGoalColumns} FROM daily_goals WHERE local_date = $date;";
        cmd.Parameters.AddWithValue("$date", localDate);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapDailyGoalRow(reader) : null;
    }

    private static AgentDailyGoal MapDailyGoalRow(SqliteDataReader reader)
    {
        static DateTimeOffset? ParseUtc(string raw) =>
            string.IsNullOrWhiteSpace(raw) || !DateTimeOffset.TryParse(raw, out DateTimeOffset value) ? null : value;

        return new AgentDailyGoal(
            Id: reader.GetInt64(0),
            LocalDate: reader.GetString(1),
            GoalText: reader.GetString(2),
            TrackingPref: reader.GetString(3),
            MiddayAnswer: reader.GetString(4),
            MiddayAtUtc: ParseUtc(reader.GetString(5)),
            EndOfDayAtUtc: ParseUtc(reader.GetString(6)),
            PlanTomorrow: reader.GetString(7),
            CreatedAtUtc: ParseUtc(reader.GetString(8)) ?? DateTimeOffset.MinValue);
    }

    // Phase 1b: append a durable ground-truth label. Deliberately does NOT call PruneIfNeeded —
    // ground_truth_labels is exempt from the 14-day prune and must persist for months.
    public void RecordGroundTruthLabel(GroundTruthLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            WriteGroundTruthLabelRow(connection, tx, label);
            tx.Commit();
        }
    }

    // Inserts one label row on the GIVEN connection/transaction. Callers that already hold a
    // write transaction (e.g. AcknowledgeIntervention) MUST use this — never RecordGroundTruthLabel,
    // which opens its own connection and would deadlock SQLite against the held write lock.
    private static void WriteGroundTruthLabelRow(SqliteConnection connection, SqliteTransaction tx, GroundTruthLabel label)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO ground_truth_labels (
                created_at_utc, bucket_seq, bucket_timestamp_utc,
                label_source, prompt_kind, response_kind, label_target, label_value,
                response_latency_ms, intervention_id, features_json,
                pressure_level, overload_likelihood, confidence,
                dominant_state, calibration_stage, trust_tier, schema_ver)
            VALUES (
                $created, $bseq, $bts,
                $src, $pk, $rk, $tgt, $val,
                $lat, $iid, $feat,
                $pl, $ol, $conf,
                $ds, $cs, $tt, $sv);
            """;
        cmd.Parameters.AddWithValue("$created", label.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$bseq", label.BucketSeq);
        cmd.Parameters.AddWithValue("$bts", label.BucketTimestampUtc ?? string.Empty);
        cmd.Parameters.AddWithValue("$src", label.LabelSource ?? string.Empty);
        cmd.Parameters.AddWithValue("$pk", label.PromptKind ?? string.Empty);
        cmd.Parameters.AddWithValue("$rk", label.ResponseKind ?? string.Empty);
        cmd.Parameters.AddWithValue("$tgt", label.LabelTarget ?? string.Empty);
        cmd.Parameters.AddWithValue("$val", label.LabelValue);
        cmd.Parameters.AddWithValue("$lat", label.ResponseLatencyMs);
        cmd.Parameters.AddWithValue("$iid", label.InterventionId ?? string.Empty);
        cmd.Parameters.AddWithValue("$feat", string.IsNullOrEmpty(label.FeaturesJson) ? "{}" : label.FeaturesJson);
        cmd.Parameters.AddWithValue("$pl", label.PressureLevel);
        cmd.Parameters.AddWithValue("$ol", label.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$conf", label.Confidence);
        cmd.Parameters.AddWithValue("$ds", label.DominantState ?? string.Empty);
        cmd.Parameters.AddWithValue("$cs", label.CalibrationStage ?? string.Empty);
        cmd.Parameters.AddWithValue("$tt", label.TrustTier ?? string.Empty);
        cmd.Parameters.AddWithValue("$sv", label.SchemaVer);
        cmd.ExecuteNonQuery();
    }

    // Phase 1b: lightweight count so we can confirm labels are actually accumulating.
    public AgentLabelDiagnostics ReadLabelDiagnostics()
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string since1d = now.AddDays(-1).ToString("O");
            string since7d = now.AddDays(-7).ToString("O");

            long total = 0;
            long last1d = 0;
            long last7d = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT
                        COUNT(*) AS total,
                        COALESCE(SUM(CASE WHEN created_at_utc >= $since1d THEN 1 ELSE 0 END), 0) AS last1d,
                        COALESCE(SUM(CASE WHEN created_at_utc >= $since7d THEN 1 ELSE 0 END), 0) AS last7d
                    FROM ground_truth_labels;
                    """;
                cmd.Parameters.AddWithValue("$since1d", since1d);
                cmd.Parameters.AddWithValue("$since7d", since7d);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    total = reader.GetInt64(0);
                    last1d = reader.GetInt64(1);
                    last7d = reader.GetInt64(2);
                }
            }

            var byTarget = new List<AgentLabelTargetCount>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT label_target, COUNT(*) AS cnt, COALESCE(SUM(label_value), 0) AS net
                    FROM ground_truth_labels
                    WHERE created_at_utc >= $since1d
                    GROUP BY label_target
                    ORDER BY cnt DESC;
                    """;
                cmd.Parameters.AddWithValue("$since1d", since1d);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    byTarget.Add(new AgentLabelTargetCount(
                        Target: reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        Count: reader.GetInt64(1),
                        NetValue: reader.GetInt64(2)));
                }
            }

            // Prompt response rate (shown vs answered) from prompt_events, so an operator can tell
            // whether people are actually engaging with the one-tap prompts. Counts only.
            long shown1d = 0, answered1d = 0, shown7d = 0, answered7d = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT
                        COALESCE(SUM(CASE WHEN created_at_utc >= $since1d THEN 1 ELSE 0 END), 0) AS shown_1d,
                        COALESCE(SUM(CASE WHEN created_at_utc >= $since1d AND outcome = 'answered' THEN 1 ELSE 0 END), 0) AS answered_1d,
                        COALESCE(SUM(CASE WHEN created_at_utc >= $since7d THEN 1 ELSE 0 END), 0) AS shown_7d,
                        COALESCE(SUM(CASE WHEN created_at_utc >= $since7d AND outcome = 'answered' THEN 1 ELSE 0 END), 0) AS answered_7d
                    FROM prompt_events;
                    """;
                cmd.Parameters.AddWithValue("$since1d", since1d);
                cmd.Parameters.AddWithValue("$since7d", since7d);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    shown1d = reader.GetInt64(0);
                    answered1d = reader.GetInt64(1);
                    shown7d = reader.GetInt64(2);
                    answered7d = reader.GetInt64(3);
                }
            }

            return new AgentLabelDiagnostics(
                total, last1d, last7d, byTarget,
                PromptsShownLast24h: shown1d,
                PromptsAnsweredLast24h: answered1d,
                PromptsShownLast7d: shown7d,
                PromptsAnsweredLast7d: answered7d);
        }
    }

    // Phase 1b — anti-spam: record that an ACTIVE one-tap prompt was SHOWN to the user. This is the
    // signal the governor's cap + cooldown key off — recorded whether or not the user ends up
    // answering — so an ignored prompt still counts and Ari backs off instead of re-asking.
    public void RecordPromptShown(string labelTarget, string promptKind, DateTimeOffset shownAtUtc)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO prompt_events
                    (prompt_id, created_at_utc, label_target, prompt_kind, outcome, resolved_at_utc, schema_ver)
                VALUES
                    ($pid, $created, $target, $kind, 'shown', '', 1);
                """;
            cmd.Parameters.AddWithValue("$pid", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$created", shownAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$target", (labelTarget ?? string.Empty).Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$kind", (promptKind ?? string.Empty).Trim());
            cmd.ExecuteNonQuery();
        }
    }

    // Phase 1b — anti-spam: mark the most recent still-'shown' prompt for this target as answered, so
    // the back-off streak resets once the user engages. Best-effort: an answer with no matching shown
    // row (e.g. an upgrade mid-cycle) simply records nothing here.
    public void ResolvePromptAnswered(string labelTarget, DateTimeOffset answeredAtUtc)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE prompt_events
                SET outcome = 'answered', resolved_at_utc = $resolved
                WHERE id = (
                    SELECT id FROM prompt_events
                    WHERE label_target = $target AND outcome = 'shown'
                    ORDER BY created_at_utc DESC
                    LIMIT 1
                );
                """;
            cmd.Parameters.AddWithValue("$resolved", answeredAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$target", (labelTarget ?? string.Empty).Trim().ToLowerInvariant());
            cmd.ExecuteNonQuery();
        }
    }

    // Phase 1b — anti-spam: recent cadence of ACTIVE one-tap prompts, read from prompt_events so the
    // governor's cap + cooldown engage on SHOWN prompts (not only answered ones). UnansweredPromptsLast24h
    // drives progressive back-off: the more shown-but-unanswered prompts recently, the longer Ari waits.
    public AgentLabelPromptCadence ReadLabelPromptCadence()
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    COALESCE(SUM(CASE WHEN created_at_utc >= $since24h THEN 1 ELSE 0 END), 0) AS shown_24h,
                    COALESCE(SUM(CASE WHEN created_at_utc >= $since24h AND outcome <> 'answered' THEN 1 ELSE 0 END), 0) AS unanswered_24h,
                    MAX(created_at_utc) AS last_at
                FROM prompt_events;
                """;
            cmd.Parameters.AddWithValue("$since24h", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                int shown24h = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
                int unanswered24h = reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1);
                DateTimeOffset? lastAt = null;
                if (!reader.IsDBNull(2))
                {
                    string raw = reader.GetString(2);
                    if (DateTimeOffset.TryParse(
                            raw,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out DateTimeOffset parsed))
                    {
                        lastAt = parsed;
                    }
                }

                return new AgentLabelPromptCadence(shown24h, lastAt, unanswered24h);
            }

            return new AgentLabelPromptCadence(0, null, 0);
        }
    }

    private const string SignupNudgeStateKey = "signup_nudge_state";

    // Phase 1c — signup nudge cadence: how many whole days of local data exist (from the earliest
    // state bucket) plus the persisted nudge caps (how many shown, and when last). Read-only.
    public AgentSignupNudgeContext ReadSignupNudgeContext()
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);

            int daysOfData = 0;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT MIN(timestamp_utc) FROM state_buckets;";
                object? scalar = cmd.ExecuteScalar();
                if (scalar != null && scalar != DBNull.Value)
                {
                    string raw = Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    if (DateTimeOffset.TryParse(
                            raw,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out DateTimeOffset firstUtc))
                    {
                        daysOfData = Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - firstUtc).TotalDays));
                    }
                }
            }

            (int count, DateTimeOffset? lastAt) = ReadSignupNudgeStateLocked(connection);
            return new AgentSignupNudgeContext(daysOfData, count, lastAt);
        }
    }

    // Phase 1c — record that a signup nudge was shown: bumps the lifetime count and stamps the time,
    // so the governor's weekly + lifetime caps engage. Persisted in runtime_state (survives restart).
    public void RecordSignupNudgeShown(DateTimeOffset shownAtUtc)
    {
        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            (int count, _) = ReadSignupNudgeStateLocked(connection);
            count += 1;

            string valueJson = $"{{\"count\":{count},\"lastAtUtc\":\"{shownAtUtc:O}\"}}";
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO runtime_state (key, value_json, updated_utc)
                VALUES ($key, $value_json, $updated_utc)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$key", SignupNudgeStateKey);
            cmd.Parameters.AddWithValue("$value_json", valueJson);
            cmd.Parameters.AddWithValue("$updated_utc", shownAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    private static (int Count, DateTimeOffset? LastAtUtc) ReadSignupNudgeStateLocked(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", SignupNudgeStateKey);
        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return (0, null);
        }

        string json = Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        int count = 0;
        DateTimeOffset? lastAt = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;
            if (root.TryGetProperty("count", out System.Text.Json.JsonElement c) && c.TryGetInt32(out int parsedCount))
            {
                count = Math.Max(0, parsedCount);
            }

            if (root.TryGetProperty("lastAtUtc", out System.Text.Json.JsonElement l)
                && l.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(
                    l.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsedLast))
            {
                lastAt = parsedLast;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return (count, lastAt);
    }

    public bool AcknowledgeIntervention(AgentInterventionAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        string interventionId = (ack.InterventionId ?? string.Empty).Trim();
        string sessionId = (ack.SessionId ?? string.Empty).Trim();
        string agentInstanceId = (ack.AgentInstanceId ?? string.Empty).Trim();
        string normalizedResult = NormalizeExecutionResult(ack.Result);
        if (string.IsNullOrWhiteSpace(interventionId)
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(agentInstanceId)
            || string.IsNullOrWhiteSpace(normalizedResult))
        {
            return false;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                var rows = new List<(long Id, string SessionId, string ExecutionState, string Action, string CommandType, string PolicyId, string Domain, string WhatChanged, string HowToUndo)>();

                using (var read = connection.CreateCommand())
                {
                    read.Transaction = tx;
                    read.CommandText =
                        """
                        SELECT
                            id,
                            session_id,
                            execution_state,
                            action,
                            command_type,
                            policy_id,
                            domain,
                            what_changed,
                            how_to_undo
                        FROM interventions
                        WHERE intervention_id = $intervention_id
                          AND command_type <> ''
                          AND (session_id = '' OR session_id = $session_id)
                        ORDER BY id DESC;
                        """;
                    read.Parameters.AddWithValue("$intervention_id", interventionId);
                    read.Parameters.AddWithValue("$session_id", sessionId);

                    using SqliteDataReader reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((
                            Id: reader.GetInt64(0),
                            SessionId: ReadStringOrFallback(reader, 1, string.Empty),
                            ExecutionState: ReadStringOrFallback(reader, 2, "decided"),
                            Action: ReadStringOrFallback(reader, 3, string.Empty),
                            CommandType: ReadStringOrFallback(reader, 4, string.Empty),
                            PolicyId: ReadStringOrFallback(reader, 5, "n/a"),
                            Domain: ReadStringOrFallback(reader, 6, "control"),
                            WhatChanged: ReadStringOrFallback(reader, 7, string.Empty),
                            HowToUndo: ReadStringOrFallback(reader, 8, string.Empty)));
                    }
                }

                if (rows.Count == 0)
                {
                    TryRollback(tx);
                    return false;
                }

                List<long> openRowIds = rows
                    .Where(static row => IsPendingInterventionState(row.ExecutionState))
                    .Select(static row => row.Id)
                    .ToList();

                if (openRowIds.Count == 0)
                {
                    (long Id, string SessionId, string ExecutionState, string Action, string CommandType, string PolicyId, string Domain, string WhatChanged, string HowToUndo) latestRow = rows[0];
                    if (string.Equals(latestRow.ExecutionState, normalizedResult, StringComparison.Ordinal))
                    {
                        if (string.IsNullOrWhiteSpace(latestRow.SessionId))
                        {
                            using var claim = connection.CreateCommand();
                            claim.Transaction = tx;
                            claim.CommandText =
                                """
                                UPDATE interventions
                                SET session_id = $session_id
                                WHERE id = $id
                                  AND (session_id IS NULL OR session_id = '');
                                """;
                            claim.Parameters.AddWithValue("$session_id", sessionId);
                            claim.Parameters.AddWithValue("$id", latestRow.Id);
                            claim.ExecuteNonQuery();
                        }

                        tx.Commit();
                        return true;
                    }

                    TryRollback(tx);
                    return false;
                }

                (long Id, string SessionId, string ExecutionState, string Action, string CommandType, string PolicyId, string Domain, string WhatChanged, string HowToUndo) targetRow = rows
                    .First(static row => IsPendingInterventionState(row.ExecutionState));

                if (!CanTransitionInterventionState(targetRow.ExecutionState, normalizedResult))
                {
                    TryRollback(tx);
                    return false;
                }

                string appliedAtUtc = ack.AppliedAtUtc.ToUniversalTime().ToString("O");
                using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText =
                    """
                    UPDATE interventions
                    SET
                        session_id = CASE
                            WHEN session_id IS NULL OR session_id = '' THEN $session_id
                            ELSE session_id
                        END,
                        execution_state = $execution_state,
                        applied_at_utc = CASE
                            WHEN $set_applied_at_utc = 1 THEN $applied_at_utc
                            ELSE CASE
                                WHEN $clear_applied_at_utc = 1 THEN ''
                                ELSE applied_at_utc
                            END
                        END,
                        acked_at_utc = $applied_at_utc,
                        error_code = $error_code,
                        retry_count = CASE
                            WHEN $execution_state = 'failed' THEN retry_count + 1
                            ELSE retry_count
                        END,
                        executor_agent_instance_id = $agent_instance_id
                    WHERE intervention_id = $intervention_id
                      AND command_type <> ''
                      AND execution_state IN ('decided', 'overlay_started', 'accepted', 'overlay_recovered')
                      AND (session_id = '' OR session_id = $session_id);
                    """;
                update.Parameters.AddWithValue("$session_id", sessionId);
                update.Parameters.AddWithValue("$execution_state", normalizedResult);
                update.Parameters.AddWithValue("$applied_at_utc", appliedAtUtc);
                update.Parameters.AddWithValue("$set_applied_at_utc", ShouldSetAppliedAtUtcForResult(normalizedResult) ? 1 : 0);
                update.Parameters.AddWithValue("$clear_applied_at_utc", normalizedResult is "dismissed" or "skipped" or "snoozed" or "suppressed_by_preference" ? 1 : 0);
                update.Parameters.AddWithValue("$error_code", ack.ErrorCode?.Trim() ?? string.Empty);
                update.Parameters.AddWithValue("$agent_instance_id", agentInstanceId);
                update.Parameters.AddWithValue("$intervention_id", interventionId);
                bool updated = update.ExecuteNonQuery() == openRowIds.Count;
                if (updated)
                {
                    ApplyInterventionAckSideEffects(
                        connection,
                        tx,
                        targetRow.Action,
                        targetRow.CommandType,
                        ack with { Result = normalizedResult },
                        _runtimePolicyProvider.GetEnvelope());
                    ApplyBreakBackoffAckSideEffects(
                        connection,
                        tx,
                        targetRow.Action,
                        normalizedResult,
                        ack.AppliedAtUtc,
                        (_runtimePolicyProvider.GetEnvelope()?.Settings.BreakPolicy
                            ?? DefaultBreakPolicySettings()).CooldownSeconds);
                    InsertInterventionLifecycleEvent(
                        connection,
                        tx,
                        targetRow,
                        ack with { Result = normalizedResult });

                    // Phase 1b (passive label capture): a genuine user choice on a break/recovery
                    // nudge becomes a durable ground-truth label. Best-effort and on THIS tx.
                    TryRecordPassiveNudgeLabel(
                        connection,
                        tx,
                        targetRow.Id,
                        targetRow.Action,
                        interventionId,
                        ack with { Result = normalizedResult });
                }
                tx.Commit();
                return updated;
            }
            catch
            {
                TryRollback(tx);
                throw;
            }
        }
    }

    // Phase 1b (passive capture): map a genuine user choice on a break/recovery nudge to a durable
    // ground-truth label, tagged with the decision-time context Ari acted on. Best-effort — a label
    // failure must never break the intervention ack, so the whole body is wrapped and swallowed.
    private static void TryRecordPassiveNudgeLabel(
        SqliteConnection connection,
        SqliteTransaction tx,
        long interventionRowId,
        string action,
        string interventionId,
        AgentInterventionAck ack)
    {
        try
        {
            int labelValue;
            switch (ack.Result)
            {
                case "accepted":
                    labelValue = 1;   // user chose the break -> Ari was right to suggest it
                    break;
                case "dismissed":
                case "skipped":
                    labelValue = -1;  // user declined -> the break was not needed
                    break;
                case "snoozed":
                    labelValue = 0;   // deferred -> ambiguous
                    break;
                default:
                    return;           // applied / overlay_* / suppressed -> not a ground-truth choice
            }

            // Only break/recovery nudges the user actually chooses on become labels; auto-applied
            // system actions never reach here with these results, but gate defensively anyway.
            string breakKind = (ack.BreakKind ?? string.Empty).Trim();
            if (!PortaBreakSemantics.IsBreakSuggestionAction(action) && breakKind.Length == 0)
            {
                return;
            }

            // Read the decision-time context frozen on the intervention row (what Ari saw).
            string bucketTs = string.Empty;
            string triggerState = string.Empty;
            string contributorsJson = "{}";
            double triggerValue = 0d;
            double confidence = 0d;
            using (var ctx = connection.CreateCommand())
            {
                ctx.Transaction = tx;
                ctx.CommandText =
                    """
                    SELECT timestamp_utc, trigger_state, trigger_value, confidence, contributors_json
                    FROM interventions
                    WHERE id = $id
                    LIMIT 1;
                    """;
                ctx.Parameters.AddWithValue("$id", interventionRowId);
                using SqliteDataReader r = ctx.ExecuteReader();
                if (r.Read())
                {
                    bucketTs = ReadStringOrFallback(r, 0, string.Empty);
                    triggerState = ReadStringOrFallback(r, 1, string.Empty);
                    triggerValue = ReadDoubleOrFallback(r, 2, 0d);
                    confidence = ReadDoubleOrFallback(r, 3, 0d);
                    contributorsJson = ReadStringOrFallback(r, 4, "{}");
                }
            }

            var label = new GroundTruthLabel(
                CreatedAtUtc: DateTimeOffset.UtcNow,
                BucketSeq: -1,
                BucketTimestampUtc: bucketTs,
                LabelSource: "break_nudge",
                PromptKind: string.IsNullOrWhiteSpace(action) ? "break" : action,
                ResponseKind: ack.Result,
                LabelTarget: "break_need",
                LabelValue: labelValue,
                ResponseLatencyMs: -1,
                InterventionId: interventionId ?? string.Empty,
                FeaturesJson: string.IsNullOrWhiteSpace(contributorsJson) ? "{}" : contributorsJson,
                PressureLevel: TryReadJsonDouble(contributorsJson, "pressure_level", triggerValue),
                OverloadLikelihood: TryReadJsonDouble(contributorsJson, "overload_likelihood", triggerValue),
                Confidence: confidence,
                DominantState: triggerState,
                CalibrationStage: string.Empty,
                TrustTier: string.Empty,
                SchemaVer: 1);

            WriteGroundTruthLabelRow(connection, tx, label);
        }
        catch (Exception)
        {
            // Best-effort: never let label capture break the intervention ack.
        }
    }

    private static double TryReadJsonDouble(string json, string key, double fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out JsonElement el)
                && el.ValueKind == JsonValueKind.Number
                && el.TryGetDouble(out double value)
                && double.IsFinite(value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        return fallback;
    }

    public AgentStorageInfo ReadStorageInfo()
    {
        lock (_gate)
        {
            long dbBytes = 0;
            try
            {
                if (File.Exists(_dbPath))
                {
                    dbBytes = new FileInfo(_dbPath).Length;
                }
            }
            catch
            {
            }

            return new AgentStorageInfo(
                RetentionDays: DefaultRetentionDays,
                DatabaseBytes: dbBytes);
        }
    }

    public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null)
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        DateOnly day = dateUtc ?? DateOnly.FromDateTime(DateTime.UtcNow);
        DateTimeOffset startUtc = new(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset endUtc = startUtc.AddDays(1);

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);

        int totalInterventions = ReadSingleInt(connection,
            """
            SELECT COUNT(1) FROM events
            WHERE timestamp_utc >= $start AND timestamp_utc < $end;
            """,
            startUtc, endUtc);

        double avgFocus = ReadSingleDouble(connection,
            """
            SELECT COALESCE(AVG(focus_strength), 0)
            FROM state_buckets
            WHERE timestamp_utc >= $start AND timestamp_utc < $end;
            """,
            startUtc, endUtc);

        double peakOverload = ReadSingleDouble(connection,
            """
            SELECT COALESCE(MAX(overload_likelihood), 0)
            FROM state_buckets
            WHERE timestamp_utc >= $start AND timestamp_utc < $end;
            """,
            startUtc, endUtc);

        double idleFragmentation = ReadSingleDouble(connection,
            """
            SELECT COALESCE(AVG(idle_fragmentation_index), 0)
            FROM state_buckets
            WHERE timestamp_utc >= $start AND timestamp_utc < $end;
            """,
            startUtc, endUtc);

        int? mostVolatileHour = ReadMostVolatileHour(connection, startUtc, endUtc);

        return new AgentTodaySummary(
            DateUtc: day,
            TotalInterventions: totalInterventions,
            AverageFocusStrength: avgFocus,
            PeakOverloadLikelihood: peakOverload,
            IdleFragmentationIndex: idleFragmentation,
            MostVolatileHourUtc: mostVolatileHour);
    }

    public AgentDistractionSummary ReadCurrentDistractionSummary()
    {
        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT value_json
            FROM runtime_state
            WHERE key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.DerivedSessionMetrics.RuntimeStateKey);

        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return new AgentDistractionSummary(0d, 0d, 0d, 0d, 0, 0);
        }

        string json = Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AgentDistractionSummary(0d, 0d, 0d, 0d, 0, 0);
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return new AgentDistractionSummary(0d, 0d, 0d, 0d, 0, 0);
            }

            double ReadDouble(string key)
            {
                return root.TryGetProperty(key, out System.Text.Json.JsonElement el)
                    && el.ValueKind == System.Text.Json.JsonValueKind.Number
                    && el.TryGetDouble(out double v) ? Math.Max(0d, v) : 0d;
            }

            int ReadInt(string key)
            {
                return root.TryGetProperty(key, out System.Text.Json.JsonElement el)
                    && el.ValueKind == System.Text.Json.JsonValueKind.Number
                    && el.TryGetInt32(out int v) ? Math.Max(0, v) : 0;
            }

            return new AgentDistractionSummary(
                SocialMediaAccumulatedSeconds: ReadDouble("social_media_accumulated_seconds"),
                GamingLauncherAccumulatedSeconds: ReadDouble("gaming_launcher_accumulated_seconds"),
                GamingActiveAccumulatedSeconds: ReadDouble("gaming_active_accumulated_seconds"),
                PassiveConsumptionAccumulatedSeconds: ReadDouble("passive_consumption_accumulated_seconds"),
                CommMonitoringReturnCount: ReadInt("comm_monitoring_return_count"),
                SocialMediaReturnCount: ReadInt("social_media_return_count"));
        }
        catch
        {
            return new AgentDistractionSummary(0d, 0d, 0d, 0d, 0, 0);
        }
    }

    public string ExportBundle(
        IReadOnlyList<string> logFilePaths,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths,
        IReadOnlyDictionary<string, string> supplementalJsonFiles)
    {
        lock (_gate)
        {
            string exportRoot = BuildExportRoot();
            Directory.CreateDirectory(exportRoot);
            string exportDir = Path.Combine(exportRoot, $"porta-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(exportDir);

            string dbTarget = Path.Combine(exportDir, "core-state.db");
            if (File.Exists(_dbPath))
            {
                File.Copy(_dbPath, dbTarget, overwrite: true);
            }

            string logsDir = Path.Combine(exportDir, "logs");
            Directory.CreateDirectory(logsDir);
            foreach (string logFilePath in logFilePaths)
            {
                string safeName = Path.GetFileName(logFilePath);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                string destination = Path.Combine(logsDir, safeName);
                if (!File.Exists(logFilePath))
                {
                    File.WriteAllText(destination, string.Empty);
                    continue;
                }

                File.Copy(logFilePath, destination, overwrite: true);
            }

            if (File.Exists(consentPath))
            {
                File.Copy(consentPath, Path.Combine(exportDir, "consent-state-v2.json"), overwrite: true);
            }

            if (File.Exists(settingsPath))
            {
                File.Copy(settingsPath, Path.Combine(exportDir, "effective-settings-v1.json"), overwrite: true);
            }

            string configExportDir = Path.Combine(exportDir, "config");
            Directory.CreateDirectory(configExportDir);
            var exportedConfigNames = new List<string>();
            foreach (string configPath in configPaths)
            {
                if (!File.Exists(configPath))
                {
                    continue;
                }

                string safeName = Path.GetFileName(configPath);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                string destination = Path.Combine(configExportDir, safeName);
                File.Copy(configPath, destination, overwrite: true);
                exportedConfigNames.Add(safeName);
            }

            string versionsJson = JsonSerializer.Serialize(versions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(exportDir, "versions.json"), versionsJson);

            foreach ((string fileName, string json) in supplementalJsonFiles)
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                string safeName = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                File.WriteAllText(Path.Combine(exportDir, safeName), json ?? "{}");
            }

            string metadata = JsonSerializer.Serialize(new
            {
                generated_at_utc = DateTimeOffset.UtcNow,
                schema = "v2",
                db_path = _dbPath,
                retention_days = DefaultRetentionDays,
                config_files = exportedConfigNames,
                included_log_files = logFilePaths
                    .Where(File.Exists)
                    .Select(Path.GetFileName)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .ToArray(),
                supplemental_json_files = supplementalJsonFiles.Keys
                    .Select(Path.GetFileName)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .ToArray()
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(exportDir, "metadata.json"), metadata);

            string zipPath = $"{exportDir}.zip";
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(exportDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            return zipPath;
        }
    }

    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection)
    {
        // deferred: false starts the write transaction immediately (BEGIN IMMEDIATE semantics).
        return connection.BeginTransaction(deferred: false);
    }

    private static void TryRollback(SqliteTransaction tx)
    {
        try
        {
            tx.Rollback();
        }
        catch
        {
        }
    }

    private static void IncrementDiagnosticsCounter(SqliteConnection connection, string key)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO diagnostics (key, value, updated_utc)
                VALUES ($key, '1', $updated_utc)
                ON CONFLICT(key) DO UPDATE SET
                    value = CASE
                        WHEN diagnostics.value GLOB '-?[0-9]*' THEN CAST(diagnostics.value AS INTEGER) + 1
                        ELSE 1
                    END,
                    updated_utc = excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }

    private static void UpsertRuntimeStateForSnapshot(
        SqliteConnection connection,
        SqliteTransaction tx,
        AgentBrainSnapshot snapshot,
        InputBucketRow mapped,
        BaselineGatingResult gating,
        string trustTier,
        PrimitiveComputationResult primitive,
        DerivedSessionMetricsResult derivedSession,
        OverloadInferenceResult overloadInference,
        OverloadStabilizationResult overloadStabilization,
        DeepWorkStabilizationResult deepWorkStabilization,
        NotificationSuppressionRuntimeState notificationSuppressionRuntimeState,
        BrightnessActionRuntimeState brightnessActionRuntimeState)
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            bucket_seq = mapped.BucketSeq,
            start_utc = mapped.StartUtc,
            end_utc = Time.toIsoUtcMsString(Time.bucketStartEnd(mapped.BucketSeq).endUtcMs),
            timestamp_utc = mapped.TimestampUtc,
            overload_likelihood = overloadInference.OverloadLikelihood,
            confidence = overloadInference.Confidence,
            overload_active = overloadStabilization.OverloadActive,
            deep_work_active = deepWorkStabilization.DeepWorkActive,
            typing_burst_intensity_raw = primitive.TypingRaw,
            scroll_velocity_index_raw = primitive.ScrollRaw,
            focus_switch_rate_raw = primitive.FocusRaw,
            typing_burst_intensity_smoothed = primitive.TypingSmoothed,
            scroll_velocity_index_smoothed = primitive.ScrollSmoothed,
            focus_switch_rate_smoothed = primitive.FocusSmoothed,
            overload_inference = new
            {
                source = overloadInference.Source,
                components_registry_ver = overloadInference.ComponentsRegistryVersion,
                used_snapshot_fallback = overloadInference.UsedSnapshotFallback,
                f_component = overloadInference.FComponent,
                i_component = overloadInference.IComponent,
                v_component = overloadInference.VComponent,
                quality = overloadInference.Quality,
                agreement = overloadInference.Agreement,
                primitive_coverage = overloadInference.PrimitiveCoverage,
                missing_primitive_count = overloadInference.MissingPrimitiveCount
            },
            sustained_attention_duration_sec = derivedSession.SustainedAttentionDurationSec,
            idle_fragmentation_index = derivedSession.IdleFragmentationIndex,
            derived_session_partial = derivedSession.Partial,
            dominant_app_hash = derivedSession.DominantAppHash,
            task_continuity_score = derivedSession.TaskContinuityScore,
            task_continuity_seconds = derivedSession.TaskContinuitySeconds,
            focus_episode_candidate_seconds = derivedSession.FocusEpisodeCandidateSeconds,
            focus_episode_active = derivedSession.TaskContinuitySeconds > 0d ? 1 : 0,
            continuity_state = derivedSession.ContinuityState,
            continuity_confidence = derivedSession.ContinuityConfidence,
            continuity_instability_seconds = derivedSession.ContinuityInstabilitySeconds,
            attention_segment_kind = derivedSession.AttentionSegmentKind,
            attention_segment_seconds = derivedSession.AttentionSegmentSeconds,
            attention_segment_reason_code = derivedSession.AttentionSegmentReasonCode,
            attention_segment_reason_text = derivedSession.AttentionSegmentReasonText,
            distraction_episode_active = derivedSession.DistractionEpisodeActive ? 1 : 0,
            distraction_episode_seconds = derivedSession.DistractionEpisodeSeconds,
            focus_episode_reason_code = derivedSession.FocusEpisodeReasonCode,
            focus_episode_reason_text = derivedSession.FocusEpisodeReasonText,
            sa_seconds = derivedSession.SustainedAttentionDurationSec,
            sa_context_app_hash = derivedSession.DominantAppHash,
            sa_last_update_bucket_seq = mapped.BucketSeq,
            sa_freeze_reason = derivedSession.SaFreezeReason,
            sa_untrusted = derivedSession.SaUntrusted,
            baseline_ready = gating.BaselineReady,
            cold_start = gating.ColdStart,
            confidence_cap = gating.ConfidenceCap,
            guidance_enabled = gating.GuidanceEnabled,
            suggestions_enabled = gating.SuggestionsEnabled,
            policies_enabled = gating.PoliciesEnabled,
            overload_stabilization = new
            {
                contract_ver = PortaExecutionConstants.OverloadStabilization.ContractVersion,
                overload_active = overloadStabilization.OverloadActive,
                debug_mode = overloadStabilization.DebugMode,
                transition = overloadStabilization.Transition,
                transitioned = overloadStabilization.Transitioned,
                previous_overload_active = overloadStabilization.PreviousOverloadActive,
                gap_exceeded = overloadStabilization.GapExceeded,
                low_quality_paused = overloadStabilization.LowQualityPaused,
                eligible = overloadStabilization.Eligible,
                on_signal = overloadStabilization.OnSignal,
                off_signal = overloadStabilization.OffSignal,
                between_thresholds = overloadStabilization.BetweenThresholds,
                gap_ms = overloadStabilization.GapMs,
                on_dwell_ms = overloadStabilization.RuntimeState.OnDwellMs,
                off_dwell_ms = overloadStabilization.RuntimeState.OffDwellMs,
                last_good_ts_ms = overloadStabilization.RuntimeState.LastGoodTsMs,
                last_bucket_seq = overloadStabilization.RuntimeState.LastBucketSeq,
                last_transition_ts_ms = overloadStabilization.RuntimeState.LastTransitionTsMs
            },
            deep_work_stabilization = new
            {
                contract_ver = PortaExecutionConstants.DeepWorkDetection.ContractVersion,
                deep_work_active = deepWorkStabilization.DeepWorkActive,
                debug_mode = deepWorkStabilization.DebugMode,
                transition = deepWorkStabilization.Transition,
                transitioned = deepWorkStabilization.Transitioned,
                previous_deep_work_active = deepWorkStabilization.PreviousDeepWorkActive,
                gap_exceeded = deepWorkStabilization.GapExceeded,
                low_quality_paused = deepWorkStabilization.LowQualityPaused,
                baseline_ready_gate = deepWorkStabilization.BaselineReadyGate,
                deep_work_candidate = deepWorkStabilization.DeepWorkCandidate,
                deep_work_exit_candidate = deepWorkStabilization.DeepWorkExitCandidate,
                on_signal = deepWorkStabilization.OnSignal,
                off_signal = deepWorkStabilization.OffSignal,
                intensity_in_range = deepWorkStabilization.IntensityInRange,
                gap_ms = deepWorkStabilization.GapMs,
                on_dwell_ms = deepWorkStabilization.RuntimeState.OnDwellMs,
                off_dwell_ms = deepWorkStabilization.RuntimeState.OffDwellMs,
                last_good_ts_ms = deepWorkStabilization.RuntimeState.LastGoodTsMs,
                last_bucket_seq = deepWorkStabilization.RuntimeState.LastBucketSeq,
                last_transition_ts_ms = deepWorkStabilization.RuntimeState.LastTransitionTsMs,
                confidence = deepWorkStabilization.Confidence,
                f_component = deepWorkStabilization.FComponent,
                i_component = deepWorkStabilization.IComponent,
                v_component = deepWorkStabilization.VComponent,
                sustained_attention_duration_sec = deepWorkStabilization.SustainedAttentionDurationSec
            },
            trust_tier = CalibrationTruth.NormalizeTrustTier(trustTier),
            overload_brightness_action = new
            {
                contract_ver = PortaExecutionConstants.BrightnessAdjustment.ContractVersion,
                supported = brightnessActionRuntimeState.BrightnessSupported,
                active = brightnessActionRuntimeState.Active,
                prev_level = brightnessActionRuntimeState.PreviousLevel,
                target_level = brightnessActionRuntimeState.TargetLevel,
                current_level = brightnessActionRuntimeState.CurrentLevel,
                ramp_direction = brightnessActionRuntimeState.RampDirection,
                last_change_utc_ms = brightnessActionRuntimeState.LastChangeUtcMs,
                cooldown_until_utc_ms = brightnessActionRuntimeState.CooldownUntilUtcMs,
                backoff_until_utc_ms = brightnessActionRuntimeState.BackoffUntilUtcMs,
                last_error_code = brightnessActionRuntimeState.LastErrorCode,
                logged_unsupported = brightnessActionRuntimeState.LoggedUnsupported,
                action_state = brightnessActionRuntimeState.ActionState
            },
            cooldown_end_timestamp_utc = snapshot.CooldownEndTimestampUtc?.ToString("O") ?? string.Empty
        });

        string finalizedSeqValueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            bucket_seq = mapped.BucketSeq,
            updated_utc = mapped.TimestampUtc
        });

        UpsertRuntimeStateValue(connection, tx, "bucket_finalizer:last_snapshot", valueJson, mapped.TimestampUtc);
        UpsertRuntimeStateValue(connection, tx, "bucket_finalizer:last_finalized_bucket_seq", finalizedSeqValueJson, mapped.TimestampUtc);

        string derivedSessionValueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.DerivedSessionMetrics.ContractVersion,
            bucket_seq = mapped.BucketSeq,
            task_continuity_score = derivedSession.TaskContinuityScore,
            task_continuity_seconds = derivedSession.TaskContinuitySeconds,
            focus_episode_candidate_seconds = derivedSession.FocusEpisodeCandidateSeconds,
            focus_episode_active = derivedSession.TaskContinuitySeconds > 0d ? 1 : 0,
            continuity_state = derivedSession.ContinuityState,
            continuity_confidence = derivedSession.ContinuityConfidence,
            continuity_anchor_app_hash = derivedSession.DominantAppHash,
            continuity_last_update_bucket_seq = mapped.BucketSeq,
            continuity_idle_pause_seconds = derivedSession.SaIdlePauseSeconds,
            continuity_context_switch_seconds = derivedSession.SaContextSwitchSeconds,
            continuity_instability_seconds = derivedSession.ContinuityInstabilitySeconds,
            continuity_reason_code = derivedSession.SaFreezeReason,
            attention_segment_kind = derivedSession.AttentionSegmentKind,
            attention_segment_seconds = derivedSession.AttentionSegmentSeconds,
            attention_segment_reason_code = derivedSession.AttentionSegmentReasonCode,
            attention_segment_reason_text = derivedSession.AttentionSegmentReasonText,
            distraction_episode_active = derivedSession.DistractionEpisodeActive ? 1 : 0,
            distraction_episode_seconds = derivedSession.DistractionEpisodeSeconds,
            focus_episode_reason_code = derivedSession.FocusEpisodeReasonCode,
            focus_episode_reason_text = derivedSession.FocusEpisodeReasonText,
            sa_seconds = derivedSession.SustainedAttentionDurationSec,
            sa_context_app_hash = derivedSession.DominantAppHash,
            sa_last_update_bucket_seq = mapped.BucketSeq,
            sa_idle_pause_seconds = derivedSession.SaIdlePauseSeconds,
            sa_context_switch_seconds = derivedSession.SaContextSwitchSeconds,
            sa_freeze_reason = derivedSession.SaFreezeReason,
            sa_untrusted = derivedSession.SaUntrusted,
            sustained_attention_duration_sec = derivedSession.SustainedAttentionDurationSec,
            idle_fragmentation_index = derivedSession.IdleFragmentationIndex,
            partial = derivedSession.Partial,
            dominant_app_hash = derivedSession.DominantAppHash,
            // ── Block C — distraction detection runtime state persistence ──────────
            // Distraction pattern detection state (Block C/E)
            micro_burst_ewma_switch_rate = derivedSession.DistrEwmaSwitchRate,
            micro_burst_accumulated_seconds = derivedSession.DistrMicroBurstAccumulatedSeconds,
            ping_pong_recent_hashes_json = derivedSession.DistrRecentDominantHashesJson,
            ping_pong_cycle_count = derivedSession.DistrPingPongCycleCount,
            ping_pong_window_start_seconds = derivedSession.DistrPingPongWindowStartSeconds,
            candidate_anchor_app_hash = derivedSession.DistrCandidateAnchorAppHash,
            candidate_anchor_accumulated_seconds = derivedSession.DistrCandidateAnchorAccumulatedSeconds,
            session_active_interaction_seconds = derivedSession.DistrSessionActiveInteractionSeconds,
            anchor_locked_seconds = derivedSession.DistrAnchorLockedSeconds,
            ewma_average_dwell_seconds = derivedSession.DistrEwmaAverageDwellSeconds,
            shallow_dwell_accumulated_seconds = derivedSession.DistrShallowDwellAccumulatedSeconds,
            windows_off_anchor_seconds = derivedSession.DistrWindowsOffAnchorSeconds,
            windows_off_anchor_buckets_back = derivedSession.DistrWindowsOffAnchorBucketsBack,
            unfocused_session_fired = derivedSession.DistrUnfocusedSessionFired ? 1 : 0,
            passive_consumption_accumulated_seconds = derivedSession.DistrPassiveConsumptionAccumulatedSeconds,
            comm_monitoring_return_count = derivedSession.DistrCommMonitoringReturnCount,
            comm_monitoring_window_start_seconds = derivedSession.DistrCommMonitoringWindowStartSeconds,
            comm_monitoring_last_dominant_category = derivedSession.DistrCommMonitoringLastDominantCategory,
            social_media_accumulated_seconds = derivedSession.DistrSocialMediaAccumulatedSeconds,
            gaming_launcher_accumulated_seconds = derivedSession.DistrGamingLauncherAccumulatedSeconds,
            gaming_active_accumulated_seconds = derivedSession.DistrGamingActiveAccumulatedSeconds,
            social_media_return_count = derivedSession.DistrSocialMediaReturnCount,
            social_media_window_start_seconds = derivedSession.DistrSocialMediaWindowStartSeconds,
            social_media_last_dominant_category = derivedSession.DistrSocialMediaLastDominantCategory,
            focus_category_absent_seconds = derivedSession.DistrFocusCategoryAbsentSeconds,
            session_start_distraction_seconds = derivedSession.DistrSessionStartDistractionSeconds,
            session_start_checked = derivedSession.DistrSessionStartChecked ? 1 : 0
        });
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.DerivedSessionMetrics.RuntimeStateKey,
            derivedSessionValueJson,
            mapped.TimestampUtc);
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.OverloadStabilization.ActiveRuntimeStateKey,
            BuildOverloadActiveRuntimeStateJson(mapped, overloadStabilization),
            mapped.TimestampUtc);
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.OverloadStabilization.RuntimeStateKey,
            BuildOverloadStabilizationRuntimeStateJson(mapped, overloadStabilization),
            mapped.TimestampUtc);
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.DeepWorkDetection.ActiveRuntimeStateKey,
            BuildDeepWorkActiveRuntimeStateJson(mapped, deepWorkStabilization),
            mapped.TimestampUtc);
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.DeepWorkDetection.RuntimeStateKey,
            BuildDeepWorkStabilizationRuntimeStateJson(mapped, deepWorkStabilization),
            mapped.TimestampUtc);
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.NotificationSuppression.RuntimeStateKey,
            BuildNotificationSuppressionRuntimeStateJson(mapped, notificationSuppressionRuntimeState),
            mapped.TimestampUtc);
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.BrightnessAdjustment.RuntimeStateKey,
            BuildBrightnessActionRuntimeStateJson(mapped, brightnessActionRuntimeState),
            mapped.TimestampUtc);
    }

    private static void UpsertRuntimeStateValue(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        string valueJson,
        string updatedUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, $updated_utc)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.Parameters.AddWithValue("$updated_utc", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyInterventionAckSideEffects(
        SqliteConnection connection,
        SqliteTransaction tx,
        string action,
        string commandType,
        AgentInterventionAck ack,
        RuntimePolicyEnvelope? policyEnvelope = null)
    {
        RuntimeNotificationSuppressionSettings notificationPolicy = policyEnvelope?.Settings.NotificationSuppression
            ?? new RuntimeNotificationSuppressionSettings(
                PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds,
                PortaExecutionConstants.NotificationSuppression.EnableCooldownSeconds,
                PortaExecutionConstants.NotificationSuppression.MinOnSeconds);
        string normalizedCommand = string.IsNullOrWhiteSpace(commandType)
            ? string.Empty
            : commandType.Trim().ToLowerInvariant();
        if (normalizedCommand != "suppress_notifications"
            && normalizedCommand != "restore_notifications")
        {
            return;
        }

        NotificationSuppressionRuntimeState currentState = ReadNotificationSuppressionRuntimeState(connection, tx);
        long appliedAtUtcMs = ack.AppliedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        string appliedAtUtc = ack.AppliedAtUtc.ToUniversalTime().ToString("O");
        string normalizedResult = NormalizeExecutionResult(ack.Result);
        string errorCode = string.IsNullOrWhiteSpace(ack.ErrorCode)
            ? "unknown_notification_error"
            : ack.ErrorCode.Trim();
        NotificationSuppressionRuntimeState updatedState = currentState;

        if (normalizedCommand == "suppress_notifications")
        {
            updatedState = normalizedResult switch
            {
                "applied" or "verified" => currentState with
                {
                    SchemaVer = 1,
                    Active = true,
                    Snapshot = null,
                    SnapshotCapturedAtUtc = string.Empty,
                    SuppressedAtUtc = appliedAtUtc,
                    RestoredAtUtc = string.Empty,
                    EnableCooldownUntilUtcMs = checked(appliedAtUtcMs + (notificationPolicy.EnableCooldownSeconds * 1000L)),
                    MinOnUntilUtcMs = checked(appliedAtUtcMs + (notificationPolicy.MinOnSeconds * 1000L)),
                    LastErrorCode = string.Empty,
                    BackoffUntilUtcMs = 0,
                    RestorePending = false,
                    ActionState = normalizedResult == "verified" ? "verified_active" : "active"
                },
                "failed" => currentState with
                {
                    SchemaVer = 1,
                    Active = false,
                    LastErrorCode = errorCode,
                    BackoffUntilUtcMs = checked(appliedAtUtcMs + (notificationPolicy.FailureBackoffSeconds * 1000L)),
                    RestorePending = false,
                    ActionState = "apply_failed"
                },
                _ => currentState
            };
        }
        else if (normalizedCommand == "restore_notifications")
        {
            updatedState = normalizedResult switch
            {
                "applied" or "verified" => currentState with
                {
                    SchemaVer = 1,
                    Active = false,
                    Snapshot = null,
                    SnapshotCapturedAtUtc = currentState.SnapshotCapturedAtUtc,
                    RestoredAtUtc = appliedAtUtc,
                    MinOnUntilUtcMs = 0,
                    LastErrorCode = string.Empty,
                    BackoffUntilUtcMs = 0,
                    RestorePending = false,
                    ActionState = normalizedResult == "verified" ? "verified_inactive" : "inactive"
                },
                "failed" => currentState with
                {
                    SchemaVer = 1,
                    Active = currentState.Active,
                    LastErrorCode = errorCode,
                    BackoffUntilUtcMs = checked(appliedAtUtcMs + (notificationPolicy.FailureBackoffSeconds * 1000L)),
                    RestorePending = true,
                    ActionState = "active_with_restore_error"
                },
                _ => currentState
            };
        }

        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.NotificationSuppression.RuntimeStateKey,
            BuildNotificationSuppressionRuntimeStateJson(0, appliedAtUtc, updatedState),
            appliedAtUtc);
    }

    private static string BuildOverloadActiveRuntimeStateJson(
        InputBucketRow mapped,
        OverloadStabilizationResult overloadStabilization)
    {
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.OverloadStabilization.ContractVersion,
            value = overloadStabilization.OverloadActive,
            overload_active = overloadStabilization.OverloadActive,
            bucket_seq = mapped.BucketSeq,
            updated_utc = mapped.TimestampUtc
        });
    }

    private static string BuildOverloadStabilizationRuntimeStateJson(
        InputBucketRow mapped,
        OverloadStabilizationResult overloadStabilization)
    {
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.OverloadStabilization.ContractVersion,
            overload_active = overloadStabilization.OverloadActive,
            on_dwell_ms = overloadStabilization.RuntimeState.OnDwellMs,
            off_dwell_ms = overloadStabilization.RuntimeState.OffDwellMs,
            last_good_ts_ms = overloadStabilization.RuntimeState.LastGoodTsMs,
            last_bucket_seq = overloadStabilization.RuntimeState.LastBucketSeq,
            last_transition_ts_ms = overloadStabilization.RuntimeState.LastTransitionTsMs,
            debug_mode = overloadStabilization.DebugMode,
            transition = overloadStabilization.Transition,
            transitioned = overloadStabilization.Transitioned,
            previous_overload_active = overloadStabilization.PreviousOverloadActive,
            gap_exceeded = overloadStabilization.GapExceeded,
            low_quality_paused = overloadStabilization.LowQualityPaused,
            eligible = overloadStabilization.Eligible,
            on_signal = overloadStabilization.OnSignal,
            off_signal = overloadStabilization.OffSignal,
            between_thresholds = overloadStabilization.BetweenThresholds,
            gap_ms = overloadStabilization.GapMs,
            overload = overloadStabilization.OverloadLikelihood,
            confidence = overloadStabilization.Confidence,
            quality_score = overloadStabilization.QualityScore
        });
    }

    private static string BuildDeepWorkActiveRuntimeStateJson(
        InputBucketRow mapped,
        DeepWorkStabilizationResult deepWorkStabilization)
    {
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.DeepWorkDetection.ContractVersion,
            value = deepWorkStabilization.DeepWorkActive,
            deep_work_active = deepWorkStabilization.DeepWorkActive,
            deep_work_started_at_utc_ms = deepWorkStabilization.RuntimeState.DeepWorkStartedAtUtcMs,
            bucket_seq = mapped.BucketSeq,
            updated_utc = mapped.TimestampUtc
        });
    }

    private static string BuildDeepWorkStabilizationRuntimeStateJson(
        InputBucketRow mapped,
        DeepWorkStabilizationResult deepWorkStabilization)
    {
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.DeepWorkDetection.ContractVersion,
            deep_work_active = deepWorkStabilization.DeepWorkActive,
            dw_on_dwell_ms = deepWorkStabilization.RuntimeState.OnDwellMs,
            dw_off_dwell_ms = deepWorkStabilization.RuntimeState.OffDwellMs,
            dw_last_good_ts_ms = deepWorkStabilization.RuntimeState.LastGoodTsMs,
            last_bucket_seq = deepWorkStabilization.RuntimeState.LastBucketSeq,
            dw_last_transition_ts_ms = deepWorkStabilization.RuntimeState.LastTransitionTsMs,
            deep_work_started_at_utc_ms = deepWorkStabilization.RuntimeState.DeepWorkStartedAtUtcMs,
            debug_mode = deepWorkStabilization.DebugMode,
            transition = deepWorkStabilization.Transition,
            transitioned = deepWorkStabilization.Transitioned,
            previous_deep_work_active = deepWorkStabilization.PreviousDeepWorkActive,
            gap_exceeded = deepWorkStabilization.GapExceeded,
            low_quality_paused = deepWorkStabilization.LowQualityPaused,
            baseline_ready_gate = deepWorkStabilization.BaselineReadyGate,
            deep_work_candidate = deepWorkStabilization.DeepWorkCandidate,
            deep_work_exit_candidate = deepWorkStabilization.DeepWorkExitCandidate,
            on_signal = deepWorkStabilization.OnSignal,
            off_signal = deepWorkStabilization.OffSignal,
            intensity_in_range = deepWorkStabilization.IntensityInRange,
            gap_ms = deepWorkStabilization.GapMs,
            confidence = deepWorkStabilization.Confidence,
            f_component = deepWorkStabilization.FComponent,
            i_component = deepWorkStabilization.IComponent,
            v_component = deepWorkStabilization.VComponent,
            sustained_attention_duration_sec = deepWorkStabilization.SustainedAttentionDurationSec
        });
    }

    private static string BuildNotificationSuppressionRuntimeStateJson(
        InputBucketRow mapped,
        NotificationSuppressionRuntimeState notificationSuppressionRuntimeState)
    {
        return BuildNotificationSuppressionRuntimeStateJson(
            mapped.BucketSeq,
            mapped.TimestampUtc,
            notificationSuppressionRuntimeState);
    }

    private static string BuildNotificationSuppressionRuntimeStateJson(
        long bucketSeq,
        string updatedUtc,
        NotificationSuppressionRuntimeState notificationSuppressionRuntimeState)
    {
        object? snapshotPayload = null;
        if (notificationSuppressionRuntimeState.Snapshot is NotificationControllerSnapshot snapshot)
        {
            snapshotPayload = new
            {
                schema_ver = snapshot.SchemaVer,
                contract_ver = snapshot.ContractVersion,
                captured_at_utc = snapshot.CapturedAtUtc.ToString("O"),
                is_valid = snapshot.IsValid,
                provider = snapshot.Provider,
                focus_assist_mode = snapshot.FocusAssistMode,
                quiet_hours_enabled = snapshot.QuietHoursEnabled,
                toast_banners_enabled = snapshot.ToastBannersEnabled,
                raw_state_json = snapshot.RawStateJson
            };
        }

        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.NotificationSuppression.ContractVersion,
            active = notificationSuppressionRuntimeState.Active,
            snapshot = snapshotPayload,
            snapshot_captured_at_utc = notificationSuppressionRuntimeState.SnapshotCapturedAtUtc,
            suppressed_at_utc = notificationSuppressionRuntimeState.SuppressedAtUtc,
            restored_at_utc = notificationSuppressionRuntimeState.RestoredAtUtc,
            enable_cooldown_until_utc_ms = Math.Max(0, notificationSuppressionRuntimeState.EnableCooldownUntilUtcMs),
            min_on_until_utc_ms = Math.Max(0, notificationSuppressionRuntimeState.MinOnUntilUtcMs),
            last_error_code = notificationSuppressionRuntimeState.LastErrorCode,
            backoff_until_utc_ms = Math.Max(0, notificationSuppressionRuntimeState.BackoffUntilUtcMs),
            last_transition_source_bucket_seq = Math.Max(0, notificationSuppressionRuntimeState.LastTransitionSourceBucketSeq),
            restore_pending = notificationSuppressionRuntimeState.RestorePending ? 1 : 0,
            action_state = notificationSuppressionRuntimeState.ActionState ?? string.Empty,
            bucket_seq = Math.Max(0, bucketSeq),
            updated_utc = updatedUtc
        });
    }

    private static string BuildBrightnessActionRuntimeStateJson(
        InputBucketRow mapped,
        BrightnessActionRuntimeState brightnessActionRuntimeState)
    {
        return BuildBrightnessActionRuntimeStateJson(
            mapped.BucketSeq,
            mapped.TimestampUtc,
            brightnessActionRuntimeState);
    }

    private static string BuildBrightnessActionRuntimeStateJson(
        long bucketSeq,
        string updatedUtc,
        BrightnessActionRuntimeState brightnessActionRuntimeState)
    {
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.BrightnessAdjustment.ContractVersion,
            supported = brightnessActionRuntimeState.BrightnessSupported,
            active = brightnessActionRuntimeState.Active,
            prev_level = brightnessActionRuntimeState.PreviousLevel,
            target_level = brightnessActionRuntimeState.TargetLevel,
            current_level = brightnessActionRuntimeState.CurrentLevel,
            ramp_direction = brightnessActionRuntimeState.RampDirection ?? "idle",
            last_change_utc_ms = Math.Max(0, brightnessActionRuntimeState.LastChangeUtcMs),
            cooldown_until_utc_ms = Math.Max(0, brightnessActionRuntimeState.CooldownUntilUtcMs),
            last_error_code = brightnessActionRuntimeState.LastErrorCode,
            backoff_until_utc_ms = Math.Max(0, brightnessActionRuntimeState.BackoffUntilUtcMs),
            logged_unsupported = brightnessActionRuntimeState.LoggedUnsupported ? 1 : 0,
            last_transition_source_bucket_seq = Math.Max(0, brightnessActionRuntimeState.LastTransitionSourceBucketSeq),
            action_state = brightnessActionRuntimeState.ActionState ?? string.Empty,
            bucket_seq = Math.Max(0, bucketSeq),
            updated_utc = updatedUtc
        });
    }

    private static BrightnessActionRuntimeState ReadBrightnessActionRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        string? currentJson = ReadRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.BrightnessAdjustment.RuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(currentJson)
            && TryParseBrightnessActionRuntimeState(currentJson, out BrightnessActionRuntimeState parsedCurrent))
        {
            return parsedCurrent;
        }

        return BrightnessActionRuntimeState.CreateDefault();
    }

    private static NotificationSuppressionRuntimeState ReadNotificationSuppressionRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        string? currentJson = ReadRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.NotificationSuppression.RuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(currentJson)
            && TryParseNotificationSuppressionRuntimeState(currentJson, out NotificationSuppressionRuntimeState parsedCurrent))
        {
            return parsedCurrent;
        }

        return NotificationSuppressionRuntimeState.CreateDefault();
    }

    private static bool TryParseBrightnessActionRuntimeState(
        string json,
        out BrightnessActionRuntimeState state)
    {
        state = BrightnessActionRuntimeState.CreateDefault();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool active = TryReadBoolProperty(root, "active", out bool activeProp) && activeProp;
            bool brightnessSupported = TryReadBoolProperty(root, "supported", out bool supportedPropNew)
                ? supportedPropNew
                : (!TryReadBoolProperty(root, "brightness_supported", out bool supportedPropLegacy) || supportedPropLegacy);
            int previousLevel = (int)ReadInt64Property(root, "prev_level", "previous_level");
            int targetLevel = (int)ReadInt64Property(root, "target_level");
            int currentLevel = (int)ReadInt64Property(root, "current_level");
            string rampDirection = ReadStringProperty(root, "ramp_direction");
            if (string.IsNullOrWhiteSpace(rampDirection))
            {
                rampDirection = "idle";
            }
            long lastChangeUtcMs = ReadInt64Property(root, "last_change_utc_ms");
            long cooldownUntilUtcMs = ReadInt64Property(root, "cooldown_until_utc_ms");
            string lastErrorCode = ReadStringProperty(root, "last_error_code");
            long backoffUntilUtcMs = ReadInt64Property(root, "backoff_until_utc_ms");
            long lastTransitionSourceBucketSeq = ReadInt64Property(root, "last_transition_source_bucket_seq");
            bool loggedUnsupported = TryReadBoolProperty(root, "logged_unsupported", out bool loggedUnsupportedProp) && loggedUnsupportedProp;
            string actionState = ReadStringProperty(root, "action_state");

            state = new BrightnessActionRuntimeState(
                SchemaVer: 1,
                Active: active,
                BrightnessSupported: brightnessSupported,
                PreviousLevel: previousLevel,
                TargetLevel: targetLevel,
                CurrentLevel: currentLevel,
                RampDirection: rampDirection,
                LastChangeUtcMs: Math.Max(0, lastChangeUtcMs),
                CooldownUntilUtcMs: Math.Max(0, cooldownUntilUtcMs),
                LastErrorCode: lastErrorCode,
                BackoffUntilUtcMs: Math.Max(0, backoffUntilUtcMs),
                LastTransitionSourceBucketSeq: Math.Max(0, lastTransitionSourceBucketSeq),
                LoggedUnsupported: loggedUnsupported,
                ActionState: string.IsNullOrWhiteSpace(actionState) ? (active ? "active" : "inactive") : actionState);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DeepWorkStabilizationRuntimeState ReadDeepWorkStabilizationRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        string? currentJson = ReadRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.DeepWorkDetection.RuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(currentJson)
            && TryParseDeepWorkStabilizationRuntimeState(currentJson, out DeepWorkStabilizationRuntimeState parsedCurrent))
        {
            return parsedCurrent;
        }

        string? activeJson = ReadRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.DeepWorkDetection.ActiveRuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(activeJson)
            && TryParseDeepWorkActiveValue(activeJson, out bool active, out long startedAtUtcMs))
        {
            return new DeepWorkStabilizationRuntimeState(
                DeepWorkActive: active,
                OnDwellMs: 0,
                OffDwellMs: 0,
                LastGoodTsMs: 0,
                LastBucketSeq: 0,
                LastTransitionTsMs: 0,
                DeepWorkStartedAtUtcMs: Math.Max(0, startedAtUtcMs));
        }

        return new DeepWorkStabilizationRuntimeState(
            DeepWorkActive: false,
            OnDwellMs: 0,
            OffDwellMs: 0,
            LastGoodTsMs: 0,
            LastBucketSeq: 0,
            LastTransitionTsMs: 0,
            DeepWorkStartedAtUtcMs: 0);
    }

    private static bool TryParseNotificationSuppressionRuntimeState(
        string json,
        out NotificationSuppressionRuntimeState state)
    {
        state = NotificationSuppressionRuntimeState.CreateDefault();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool active = TryReadBoolProperty(root, "active", out bool activeProp) && activeProp;
            string snapshotCapturedAtUtc = ReadStringProperty(root, "snapshot_captured_at_utc");
            string suppressedAtUtc = ReadStringProperty(root, "suppressed_at_utc");
            string restoredAtUtc = ReadStringProperty(root, "restored_at_utc");
            long enableCooldownUntilUtcMs = ReadInt64Property(root, "enable_cooldown_until_utc_ms");
            long minOnUntilUtcMs = ReadInt64Property(root, "min_on_until_utc_ms");
            string lastErrorCode = ReadStringProperty(root, "last_error_code");
            long backoffUntilUtcMs = ReadInt64Property(root, "backoff_until_utc_ms");
            long lastTransitionSourceBucketSeq = ReadInt64Property(root, "last_transition_source_bucket_seq");
            bool restorePending = TryReadBoolProperty(root, "restore_pending", out bool restorePendingProp) && restorePendingProp;
            string actionState = ReadStringProperty(root, "action_state");

            NotificationControllerSnapshot? snapshot = null;
            if (root.TryGetProperty("snapshot", out JsonElement snapshotNode)
                && snapshotNode.ValueKind == JsonValueKind.Object)
            {
                int schemaVer = (int)Math.Max(1, ReadInt64Property(snapshotNode, "schema_ver"));
                string contractVer = ReadStringProperty(snapshotNode, "contract_ver");
                string capturedAtUtcRaw = ReadStringProperty(snapshotNode, "captured_at_utc");
                if (!DateTimeOffset.TryParse(capturedAtUtcRaw, out DateTimeOffset capturedAtUtc))
                {
                    capturedAtUtc = DateTimeOffset.UnixEpoch;
                }

                bool isValid = TryReadBoolProperty(snapshotNode, "is_valid", out bool isValidProp) && isValidProp;
                string provider = ReadStringProperty(snapshotNode, "provider");
                string focusAssistMode = ReadStringProperty(snapshotNode, "focus_assist_mode");
                bool quietHoursEnabled = TryReadBoolProperty(snapshotNode, "quiet_hours_enabled", out bool quietProp) && quietProp;
                bool toastBannersEnabled = !TryReadBoolProperty(snapshotNode, "toast_banners_enabled", out bool toastProp) || toastProp;
                string rawStateJson = ReadStringProperty(snapshotNode, "raw_state_json");

                snapshot = new NotificationControllerSnapshot(
                    SchemaVer: schemaVer,
                    ContractVersion: string.IsNullOrWhiteSpace(contractVer) ? "v1" : contractVer,
                    CapturedAtUtc: capturedAtUtc,
                    IsValid: isValid,
                    Provider: provider,
                    FocusAssistMode: focusAssistMode,
                    QuietHoursEnabled: quietHoursEnabled,
                    ToastBannersEnabled: toastBannersEnabled,
                    RawStateJson: string.IsNullOrWhiteSpace(rawStateJson) ? "{}" : rawStateJson);
            }

            state = new NotificationSuppressionRuntimeState(
                SchemaVer: 1,
                Active: active,
                Snapshot: snapshot,
                SnapshotCapturedAtUtc: snapshotCapturedAtUtc,
                SuppressedAtUtc: suppressedAtUtc,
                RestoredAtUtc: restoredAtUtc,
                EnableCooldownUntilUtcMs: Math.Max(0, enableCooldownUntilUtcMs),
                MinOnUntilUtcMs: Math.Max(0, minOnUntilUtcMs),
                LastErrorCode: lastErrorCode,
                BackoffUntilUtcMs: Math.Max(0, backoffUntilUtcMs),
                LastTransitionSourceBucketSeq: Math.Max(0, lastTransitionSourceBucketSeq),
                RestorePending: restorePending,
                ActionState: string.IsNullOrWhiteSpace(actionState) ? (active ? "active" : "inactive") : actionState);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public AgentNotificationSuppressionStatus ReadNotificationSuppressionStatus()
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        using ReadGateLease _ = AcquireReadGate();
        try
        {
            using var connection = DbOpen.OpenReadConnection(_connectionString, busyTimeoutMs: ResponsiveReadBusyTimeoutMs);
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT value_json
                    FROM runtime_state
                    WHERE key = $key
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.NotificationSuppression.RuntimeStateKey);
                object? scalar = cmd.ExecuteScalar();
                string? json = scalar == null || scalar == DBNull.Value
                    ? null
                    : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture);

                if (string.IsNullOrWhiteSpace(json)
                    || !TryParseNotificationSuppressionRuntimeState(json, out NotificationSuppressionRuntimeState parsed))
                {
                    return new AgentNotificationSuppressionStatus(
                        Active: false,
                        Ready: true,
                        ActionState: "inactive",
                        LastError: null,
                        BackoffUntilUtc: null,
                        EnableCooldownUntilUtc: null,
                        MinOnUntilUtc: null,
                        RestorePending: false);
                }

                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                bool inBackoff = parsed.BackoffUntilUtcMs > nowUtc.ToUnixTimeMilliseconds();
                string? lastError = string.IsNullOrWhiteSpace(parsed.LastErrorCode) ? null : parsed.LastErrorCode;
                bool ready = !inBackoff && string.IsNullOrWhiteSpace(lastError);
                return new AgentNotificationSuppressionStatus(
                    Active: parsed.Active,
                    Ready: ready,
                    ActionState: parsed.ActionState,
                    LastError: lastError,
                    BackoffUntilUtc: ToOptionalUtc(parsed.BackoffUntilUtcMs),
                    EnableCooldownUntilUtc: ToOptionalUtc(parsed.EnableCooldownUntilUtcMs),
                    MinOnUntilUtc: ToOptionalUtc(parsed.MinOnUntilUtcMs),
                    RestorePending: parsed.RestorePending);
            }
            catch
            {
                return new AgentNotificationSuppressionStatus(
                    Active: false,
                    Ready: true,
                    ActionState: "inactive",
                    LastError: null,
                    BackoffUntilUtc: null,
                    EnableCooldownUntilUtc: null,
                    MinOnUntilUtc: null,
                    RestorePending: false);
            }
    }

    public AgentBrightnessActionStatus ReadBrightnessActionStatus()
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        using ReadGateLease _ = AcquireReadGate();
        try
        {
            using var connection = DbOpen.OpenReadConnection(_connectionString, busyTimeoutMs: ResponsiveReadBusyTimeoutMs);
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT value_json
                    FROM runtime_state
                    WHERE key = $key
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.BrightnessAdjustment.RuntimeStateKey);
                object? scalar = cmd.ExecuteScalar();
                string? json = scalar == null || scalar == DBNull.Value
                    ? null
                    : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture);

                if (string.IsNullOrWhiteSpace(json)
                    || !TryParseBrightnessActionRuntimeState(json, out BrightnessActionRuntimeState parsed))
                {
                    return new AgentBrightnessActionStatus(
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
                }

                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                bool inBackoff = parsed.BackoffUntilUtcMs > nowUtc.ToUnixTimeMilliseconds();
                string? lastError = string.IsNullOrWhiteSpace(parsed.LastErrorCode) ? null : parsed.LastErrorCode;
                bool actionReady = parsed.BrightnessSupported && !inBackoff && lastError is null;
                return new AgentBrightnessActionStatus(
                    Supported: parsed.BrightnessSupported,
                    ActionReady: actionReady,
                    Active: parsed.Active,
                    RampDirection: parsed.RampDirection,
                    PreviousLevel: parsed.PreviousLevel,
                    TargetLevel: parsed.TargetLevel,
                    CurrentLevel: parsed.CurrentLevel,
                    LastError: lastError,
                    BackoffUntilUtc: ToOptionalUtc(parsed.BackoffUntilUtcMs),
                    CooldownUntilUtc: ToOptionalUtc(parsed.CooldownUntilUtcMs));
            }
            catch
            {
                return new AgentBrightnessActionStatus(
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
            }
    }

    public bool ReadDbMigrationFailed()
    {
        // WAL mode: reads run concurrently with writes; no lock needed.
        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT value
            FROM diagnostics
            WHERE key = 'db_migration_failed'
            LIMIT 1;
            """;

        object? scalar = cmd.ExecuteScalar();
        string value = scalar == null || scalar == DBNull.Value
            ? string.Empty
            : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal);
    }

    private static OverloadStabilizationRuntimeState ReadOverloadStabilizationRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        string? currentJson = ReadRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.OverloadStabilization.RuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(currentJson)
            && TryParseOverloadStabilizationRuntimeState(currentJson, out OverloadStabilizationRuntimeState parsedCurrent))
        {
            return parsedCurrent;
        }

        string? activeJson = ReadRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.OverloadStabilization.ActiveRuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(activeJson)
            && TryParseOverloadActiveValue(activeJson, out bool active))
        {
            return new OverloadStabilizationRuntimeState(
                OverloadActive: active,
                OnDwellMs: 0,
                OffDwellMs: 0,
                LastGoodTsMs: 0,
                LastBucketSeq: 0,
                LastTransitionTsMs: 0);
        }

        return new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 0,
            OffDwellMs: 0,
            LastGoodTsMs: 0,
            LastBucketSeq: 0,
            LastTransitionTsMs: 0);
    }

    private static string? ReadRuntimeStateValue(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT value_json
            FROM runtime_state
            WHERE key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return null;
        }

        return Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryParseOverloadStabilizationRuntimeState(
        string json,
        out OverloadStabilizationRuntimeState state)
    {
        state = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool active = TryReadBool(root, out bool value)
                ? value
                : TryReadBoolProperty(root, "overload_active", out bool activeProp) && activeProp;
            long lastBucketSeq = ReadInt64Property(root, "last_bucket_seq", "bucket_seq");
            long onDwellMs = ReadInt64Property(root, "on_dwell_ms", "dwell_on_ms");
            long offDwellMs = ReadInt64Property(root, "off_dwell_ms", "dwell_off_ms");
            long lastGoodTsMs = ReadInt64Property(root, "last_good_ts_ms", "last_updated_utc_ms");
            long lastTransitionTsMs = ReadInt64Property(root, "last_transition_ts_ms");
            state = new OverloadStabilizationRuntimeState(
                OverloadActive: active,
                OnDwellMs: Math.Max(0, onDwellMs),
                OffDwellMs: Math.Max(0, offDwellMs),
                LastGoodTsMs: Math.Max(0, lastGoodTsMs),
                LastBucketSeq: Math.Max(0, lastBucketSeq),
                LastTransitionTsMs: Math.Max(0, lastTransitionTsMs));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseDeepWorkStabilizationRuntimeState(
        string json,
        out DeepWorkStabilizationRuntimeState state)
    {
        state = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool active = TryReadBool(root, out bool value)
                ? value
                : TryReadBoolProperty(root, "deep_work_active", out bool activeProp) && activeProp;
            long lastBucketSeq = ReadInt64Property(root, "last_bucket_seq", "bucket_seq");
            long onDwellMs = ReadInt64Property(root, "dw_on_dwell_ms", "on_dwell_ms", "dwell_on_ms");
            long offDwellMs = ReadInt64Property(root, "dw_off_dwell_ms", "off_dwell_ms", "dwell_off_ms");
            long lastGoodTsMs = ReadInt64Property(root, "dw_last_good_ts_ms", "last_good_ts_ms", "last_updated_utc_ms");
            long lastTransitionTsMs = ReadInt64Property(root, "dw_last_transition_ts_ms", "last_transition_ts_ms");
            long deepWorkStartedAtUtcMs = ReadInt64Property(root, "deep_work_started_at_utc_ms");
            state = new DeepWorkStabilizationRuntimeState(
                DeepWorkActive: active,
                OnDwellMs: Math.Max(0, onDwellMs),
                OffDwellMs: Math.Max(0, offDwellMs),
                LastGoodTsMs: Math.Max(0, lastGoodTsMs),
                LastBucketSeq: Math.Max(0, lastBucketSeq),
                LastTransitionTsMs: Math.Max(0, lastTransitionTsMs),
                DeepWorkStartedAtUtcMs: Math.Max(0, deepWorkStartedAtUtcMs));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseOverloadActiveValue(string json, out bool overloadActive)
    {
        overloadActive = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (TryReadBool(root, out bool value))
            {
                overloadActive = value;
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryReadBoolProperty(root, "value", out bool valueProp))
                {
                    overloadActive = valueProp;
                    return true;
                }

                if (TryReadBoolProperty(root, "overload_active", out bool activeProp))
                {
                    overloadActive = activeProp;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryParseDeepWorkActiveValue(string json, out bool deepWorkActive, out long deepWorkStartedAtUtcMs)
    {
        deepWorkActive = false;
        deepWorkStartedAtUtcMs = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (TryReadBool(root, out bool value))
            {
                deepWorkActive = value;
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                deepWorkStartedAtUtcMs = Math.Max(0, ReadInt64Property(root, "deep_work_started_at_utc_ms"));
                if (TryReadBoolProperty(root, "value", out bool valueProp))
                {
                    deepWorkActive = valueProp;
                    return true;
                }

                if (TryReadBoolProperty(root, "deep_work_active", out bool activeProp))
                {
                    deepWorkActive = activeProp;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool ParseOverloadActive(string stateFlagsJson)
    {
        return TryParseOverloadActiveValue(stateFlagsJson, out bool active) && active;
    }

    private static bool TryReadBool(JsonElement value, out bool parsed)
    {
        parsed = false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            parsed = value.GetBoolean();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
        {
            parsed = numeric != 0;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out bool fromString))
        {
            parsed = fromString;
            return true;
        }

        return false;
    }

    private static bool TryReadBoolProperty(JsonElement root, string propertyName, out bool parsed)
    {
        parsed = false;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return TryReadBool(property, out parsed);
    }

    private static string ReadStringProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement property))
        {
            return string.Empty;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        if (property.ValueKind == JsonValueKind.Number
            || property.ValueKind == JsonValueKind.True
            || property.ValueKind == JsonValueKind.False)
        {
            return property.ToString();
        }

        return string.Empty;
    }

    private static bool TryReadStringProperty(JsonElement root, string propertyName, out string? parsed)
    {
        parsed = null;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        parsed = property.GetString();
        return true;
    }

    private static bool TryReadDoubleProperty(JsonElement root, string propertyName, out double parsed)
    {
        parsed = 0d;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double numeric))
        {
            parsed = numeric;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                System.Globalization.CultureInfo.InvariantCulture,
                out double fromString))
        {
            parsed = fromString;
            return true;
        }

        return false;
    }

    private static DateTimeOffset? ToOptionalUtc(long utcMs)
    {
        if (utcMs <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(utcMs);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadIntProperty(JsonElement root, string propertyName, out int parsed)
    {
        parsed = 0;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int numeric))
        {
            parsed = numeric;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int fromString))
        {
            parsed = fromString;
            return true;
        }

        return false;
    }

    private static long ReadInt64Property(JsonElement root, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric))
            {
                return numeric;
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(
                    value.GetString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long fromString))
            {
                return fromString;
            }
        }

        return 0;
    }

    private static AgentBrainSnapshot ApplyBaselineGating(
        AgentBrainSnapshot snapshot,
        BaselineGatingResult gating,
        double signalQualityScore,
        bool partialData)
    {
        double cappedConfidence = Math.Clamp(Math.Min(snapshot.Confidence, gating.ConfidenceCap), 0d, 1d);
        string trustTier = CalibrationTruth.ResolveTrustTier(
            gating.CalibrationStage,
            cappedConfidence,
            signalQualityScore,
            partialData);
        AgentBrainSnapshot gated = snapshot with
        {
            Confidence = cappedConfidence
        };

        if (!IsPolicyDrivenAction(gated.LastAction))
        {
            return gated;
        }

        string gatedAction = ApplyCalibrationStageActionGate(gated.LastAction, gated.Domain, gating, trustTier);
        if (string.Equals(gatedAction, gated.LastAction, StringComparison.Ordinal))
        {
            return gated;
        }

        IReadOnlyList<string> contributors = gated.Contributors
            .Concat([
                $"calibration_stage_gate:{CalibrationTruth.NormalizeStage(gating.CalibrationStage)}",
                $"trust_tier_gate:{CalibrationTruth.NormalizeTrustTier(trustTier)}"
            ])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return gated with
        {
            LastAction = gatedAction,
            ActivationWindowTriggered = gated.ActivationWindowTriggered
                && !string.Equals(gatedAction, "monitor_only", StringComparison.Ordinal),
            CooldownTriggered = gated.CooldownTriggered
                && !string.Equals(gatedAction, "monitor_only", StringComparison.Ordinal),
            ActivationWindowOpen = gated.ActivationWindowOpen
                && !string.Equals(gatedAction, "monitor_only", StringComparison.Ordinal),
            Contributors = contributors,
            PolicyId = "calibration-stage-gate-v2",
            Domain = string.IsNullOrWhiteSpace(gated.Domain) ? ResolveDomainForCalibrationAction(gatedAction, gated.Domain) : gated.Domain,
            EscalationLevel = ResolveEscalationLevel(gatedAction),
            PlannedEndTimestampUtc = null,
            CooldownEndTimestampUtc = null
        };
    }

    private static string ApplyCalibrationStageActionGate(
        string action,
        string domain,
        BaselineGatingResult gating,
        string trustTier)
    {
        if (!IsPolicyDrivenAction(action))
        {
            return action;
        }

        if (IsSystemAction(action))
        {
            if (gating.SystemActionsEnabled && CalibrationTruth.AllowsAutomaticActionsForTrust(trustTier))
            {
                return action;
            }

            if (gating.SuggestionsEnabled && CalibrationTruth.AllowsSuggestionsForTrust(trustTier))
            {
                return AllowsCalibrationSuggestionFallback(domain, action)
                    ? MapDomainToSuggestion(domain, action)
                    : MapDomainToInform(domain, action);
            }

            return gating.GuidanceEnabled && CalibrationTruth.AllowsGuidanceForTrust(trustTier)
                ? MapDomainToInform(domain, action)
                : "monitor_only";
        }

        if (IsSuggestionAction(action))
        {
            if (gating.SuggestionsEnabled && CalibrationTruth.AllowsSuggestionsForTrust(trustTier))
            {
                return action;
            }

            return gating.GuidanceEnabled && CalibrationTruth.AllowsGuidanceForTrust(trustTier)
                ? MapDomainToInform(domain, action)
                : "monitor_only";
        }

        if (IsInformAction(action))
        {
            return gating.GuidanceEnabled && CalibrationTruth.AllowsGuidanceForTrust(trustTier)
                ? action
                : "monitor_only";
        }

        return gating.GuidanceEnabled && CalibrationTruth.AllowsGuidanceForTrust(trustTier)
            ? MapDomainToInform(domain, action)
            : "monitor_only";
    }

    private static PrimitiveBucketWriteModel BuildPrimitiveBucketRow(
        InputBucketRow mapped,
        PrimitiveComputationResult primitive,
        RobustNormalizationSnapshot? robust)
    {
        (long startUtcMs, long endUtcMs) = Time.bucketStartEnd(mapped.BucketSeq);
        object robustJson = robust == null
            ? new
            {
                schema_ver = 1,
                available = false
            }
            : new
            {
                schema_ver = 1,
                available = true,
                typing_component = robust.TypingComponent,
                scroll_component = robust.ScrollComponent,
                focus_component = robust.FocusComponent,
                metrics = robust.Metrics.ToDictionary(
                    static pair => pair.Key,
                    static pair => new
                    {
                        raw_value = pair.Value.RawValue,
                        winsorized_value = pair.Value.WinsorizedValue,
                        z = pair.Value.Z,
                        metric_ready = pair.Value.MetricReady,
                        was_winsorized = pair.Value.WasWinsorized,
                        non_finite = pair.Value.NonFinite
                    },
                    StringComparer.Ordinal)
            };
        string primitivesJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            primitive_registry_ver = PrimitiveRegistry.RegistryVersion,
            typing_burst_intensity_raw = primitive.TypingRaw,
            scroll_velocity_index_raw = primitive.ScrollRaw,
            focus_switch_rate_raw = primitive.FocusRaw,
            typing_burst_intensity_smoothed = primitive.TypingSmoothed,
            scroll_velocity_index_smoothed = primitive.ScrollSmoothed,
            focus_switch_rate_smoothed = primitive.FocusSmoothed,
            primitive_registry = PrimitiveRegistry.Metrics.Select(static metric => new
            {
                primitive_key = metric.PrimitiveKey,
                source_metric_keys = metric.SourceMetricKeys,
                channel_dependency = metric.ChannelDependency,
                ewma_alpha = metric.EwmaAlpha,
                applies_missing_channel_rule = metric.AppliesMissingChannelRule
            }),
            channels = new
            {
                typing_available = primitive.TypingChannelAvailable,
                scroll_available = primitive.ScrollChannelAvailable,
                focus_available = primitive.FocusChannelAvailable
            },
            robust_normalization = robustJson
        });
        string whyJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            primitive_registry_ver = PrimitiveRegistry.RegistryVersion,
            source = "robust_z_clamped",
            transform = new
            {
                sigmoid_scale = PortaExecutionConstants.PrimitiveNormalization.SigmoidScale,
                ewma_alpha = PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha,
                missing_channel_decay = PortaExecutionConstants.PrimitiveNormalization.MissingChannelDecay
            },
            debug_scores = new
            {
                typing_burst_intensity = primitive.TypingRaw,
                scroll_velocity_index = primitive.ScrollRaw,
                focus_switch_rate = primitive.FocusRaw
            }
        });
        string stateFlagsJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            partial = mapped.Partial == 1,
            quality_score = mapped.QualityScore,
            typing_channel_available = primitive.TypingChannelAvailable,
            scroll_channel_available = primitive.ScrollChannelAvailable,
            focus_channel_available = primitive.FocusChannelAvailable
        });

        return new PrimitiveBucketWriteModel(
            BucketSeq: mapped.BucketSeq,
            StartUtc: Time.toIsoUtcMsString(startUtcMs),
            EndUtc: Time.toIsoUtcMsString(endUtcMs),
            PrimitivesJson: primitivesJson,
            ContributorsJson: mapped.ContributorsJson,
            WhyJson: whyJson,
            StateFlagsJson: stateFlagsJson,
            Quality: mapped.QualityScore,
            Partial: mapped.Partial);
    }

    private static StateBucketWriteModel BuildStateBucketRow(
        InputBucketRow mapped,
        AgentBrainSnapshot snapshot,
        PrimitiveComputationResult primitive,
        RobustNormalizationSnapshot? robust,
        BaselineGatingResult gating,
        string trustTier,
        DerivedSessionMetricsResult derivedSession,
        OverloadInferenceResult overloadInference,
        OverloadStabilizationResult overloadStabilization,
        DeepWorkStabilizationResult deepWorkStabilization,
        NotificationSuppressionRuntimeState notificationSuppressionRuntimeState,
        BrightnessActionRuntimeState brightnessActionRuntimeState)
    {
        _ = robust;
        string contributorsJson = BuildOverloadContributorsJson(snapshot.Contributors, overloadInference);
        string stateFlagsJson = BuildStateBucketStateFlagsJson(
            coldStart: gating.ColdStart,
            partial: mapped.Partial == 1 || derivedSession.Partial,
            saResetReason: derivedSession.SaFreezeReason,
            taskContinuityScore: derivedSession.TaskContinuityScore,
            taskContinuitySeconds: derivedSession.TaskContinuitySeconds,
            focusEpisodeCandidateSeconds: derivedSession.FocusEpisodeCandidateSeconds,
            focusEpisodeActive: derivedSession.TaskContinuitySeconds > 0d,
            continuityState: derivedSession.ContinuityState,
            continuityConfidence: derivedSession.ContinuityConfidence,
            continuityAnchorAppHash: derivedSession.DominantAppHash,
            continuityInstabilitySeconds: derivedSession.ContinuityInstabilitySeconds,
            attentionSegmentKind: derivedSession.AttentionSegmentKind,
            attentionSegmentSeconds: derivedSession.AttentionSegmentSeconds,
            attentionSegmentReasonCode: derivedSession.AttentionSegmentReasonCode,
            attentionSegmentReasonText: derivedSession.AttentionSegmentReasonText,
            distractionEpisodeActive: derivedSession.DistractionEpisodeActive,
            distractionEpisodeSeconds: derivedSession.DistractionEpisodeSeconds,
            focusEpisodeReasonCode: derivedSession.FocusEpisodeReasonCode,
            focusEpisodeReasonText: derivedSession.FocusEpisodeReasonText,
            signalQualityScore: mapped.QualityScore,
            modelConfidence: overloadInference.Confidence,
            pressureLevel: overloadInference.PressureLevel,
            overloadInferenceSource: overloadInference.Source,
            calibrationStage: gating.CalibrationStage,
            trustTier: trustTier,
            guidanceEnabled: gating.GuidanceEnabled,
            suggestionsEnabled: gating.SuggestionsEnabled,
            systemActionsEnabled: gating.SystemActionsEnabled,
            overloadActive: overloadStabilization.OverloadActive,
            lowQuality: overloadStabilization.LowQualityPaused,
            dwellOnMs: overloadStabilization.RuntimeState.OnDwellMs,
            dwellOffMs: overloadStabilization.RuntimeState.OffDwellMs,
            gapMs: overloadStabilization.GapMs,
            deepWorkActive: deepWorkStabilization.DeepWorkActive,
            deepWorkDwellOnMs: deepWorkStabilization.RuntimeState.OnDwellMs,
            deepWorkDwellOffMs: deepWorkStabilization.RuntimeState.OffDwellMs,
            deepWorkGapMs: deepWorkStabilization.GapMs,
            deepWorkStartedAtUtcMs: deepWorkStabilization.RuntimeState.DeepWorkStartedAtUtcMs,
            deepWorkCandidate: deepWorkStabilization.DeepWorkCandidate,
            deepWorkExitCandidate: deepWorkStabilization.DeepWorkExitCandidate,
            deepWorkLowQualityPaused: deepWorkStabilization.LowQualityPaused,
            notificationSuppressionActive: notificationSuppressionRuntimeState.Active,
            notificationSuppressionActionState: notificationSuppressionRuntimeState.ActionState,
            notificationSuppressionRestorePending: notificationSuppressionRuntimeState.RestorePending,
            notificationSuppressionLastErrorCode: notificationSuppressionRuntimeState.LastErrorCode,
            notificationSuppressionEnableCooldownUntilUtcMs: notificationSuppressionRuntimeState.EnableCooldownUntilUtcMs,
            notificationSuppressionMinOnUntilUtcMs: notificationSuppressionRuntimeState.MinOnUntilUtcMs,
            notificationSuppressionBackoffUntilUtcMs: notificationSuppressionRuntimeState.BackoffUntilUtcMs,
            brightnessAdjusted: brightnessActionRuntimeState.Active,
            brightnessActionState: brightnessActionRuntimeState.ActionState,
            brightnessSupported: brightnessActionRuntimeState.BrightnessSupported,
            brightnessLastErrorCode: brightnessActionRuntimeState.LastErrorCode,
            brightnessCooldownUntilUtcMs: brightnessActionRuntimeState.CooldownUntilUtcMs,
            brightnessBackoffUntilUtcMs: brightnessActionRuntimeState.BackoffUntilUtcMs,
            overloadStateDebug: new
            {
                mode = overloadStabilization.DebugMode,
                transition = overloadStabilization.Transition,
                transitioned = overloadStabilization.Transitioned,
                previous_overload_active = overloadStabilization.PreviousOverloadActive,
                gap_exceeded = overloadStabilization.GapExceeded,
                low_quality_paused = overloadStabilization.LowQualityPaused,
                eligible = overloadStabilization.Eligible,
                on_signal = overloadStabilization.OnSignal,
                off_signal = overloadStabilization.OffSignal,
                between_thresholds = overloadStabilization.BetweenThresholds,
                gap_ms = overloadStabilization.GapMs,
                overload = overloadStabilization.OverloadLikelihood,
                confidence = overloadStabilization.Confidence,
                quality_score = overloadStabilization.QualityScore,
                on_dwell_ms = overloadStabilization.RuntimeState.OnDwellMs,
                off_dwell_ms = overloadStabilization.RuntimeState.OffDwellMs
            },
            deepWorkStateDebug: new
            {
                mode = deepWorkStabilization.DebugMode,
                transition = deepWorkStabilization.Transition,
                transitioned = deepWorkStabilization.Transitioned,
                previous_deep_work_active = deepWorkStabilization.PreviousDeepWorkActive,
                gap_exceeded = deepWorkStabilization.GapExceeded,
                low_quality_paused = deepWorkStabilization.LowQualityPaused,
                baseline_ready_gate = deepWorkStabilization.BaselineReadyGate,
                deep_work_candidate = deepWorkStabilization.DeepWorkCandidate,
                deep_work_exit_candidate = deepWorkStabilization.DeepWorkExitCandidate,
                on_signal = deepWorkStabilization.OnSignal,
                off_signal = deepWorkStabilization.OffSignal,
                intensity_in_range = deepWorkStabilization.IntensityInRange,
                gap_ms = deepWorkStabilization.GapMs,
                confidence = deepWorkStabilization.Confidence,
                f_component = deepWorkStabilization.FComponent,
                i_component = deepWorkStabilization.IComponent,
                v_component = deepWorkStabilization.VComponent,
                sustained_attention_duration_sec = deepWorkStabilization.SustainedAttentionDurationSec,
                on_dwell_ms = deepWorkStabilization.RuntimeState.OnDwellMs,
                off_dwell_ms = deepWorkStabilization.RuntimeState.OffDwellMs
            });

        return new StateBucketWriteModel(
            TimestampUtc: mapped.StartUtc,
            TypingRaw: primitive.TypingRaw,
            ScrollRaw: primitive.ScrollRaw,
            FocusRaw: primitive.FocusRaw,
            TypingSmoothed: primitive.TypingSmoothed,
            ScrollSmoothed: primitive.ScrollSmoothed,
            FocusSmoothed: primitive.FocusSmoothed,
            FocusStrength: snapshot.FocusStrength,
            OverloadLikelihood: overloadInference.OverloadLikelihood,
            FatigueProbability: snapshot.FatigueProbability,
            CognitiveStability: snapshot.CognitiveStability,
            DominantStateName: snapshot.DominantStateName,
            DominantStateValue: snapshot.DominantStateValue,
            Confidence: overloadInference.Confidence,
            SustainedAttentionDurationSec: derivedSession.SustainedAttentionDurationSec,
            IdleFragmentationIndex: derivedSession.IdleFragmentationIndex,
            ActivationWindowTriggered: snapshot.ActivationWindowTriggered ? 1 : 0,
            CooldownTriggered: snapshot.CooldownTriggered ? 1 : 0,
            ActivationWindowOpen: snapshot.ActivationWindowOpen ? 1 : 0,
            ContributorsJson: contributorsJson,
            StateFlagsJson: stateFlagsJson);
    }

    internal static string BuildOverloadContributorsJson(
        IReadOnlyList<string> contributors,
        OverloadInferenceResult overloadInference)
    {
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            quality = overloadInference.Quality,
            agreement = overloadInference.Agreement,
            pressure_level = overloadInference.PressureLevel,
            overload_likelihood = overloadInference.OverloadLikelihood,
            components = new
            {
                F = new
                {
                    value = overloadInference.FComponent,
                    top = overloadInference.FTopPrimitives.Select(static item => new
                    {
                        key = item.Key,
                        value = item.Value,
                        w = item.Weight,
                        contrib = item.Contribution
                    })
                },
                I = new
                {
                    value = overloadInference.IComponent,
                    top = overloadInference.ITopPrimitives.Select(static item => new
                    {
                        key = item.Key,
                        value = item.Value,
                        w = item.Weight,
                        contrib = item.Contribution
                    })
                },
                V = new
                {
                    value = overloadInference.VComponent,
                    top = overloadInference.VTopPrimitives.Select(static item => new
                    {
                        key = item.Key,
                        value = item.Value,
                        w = item.Weight,
                        contrib = item.Contribution
                    })
                }
            },
            contributors = contributors
        });
    }

    internal static string BuildStateBucketStateFlagsJson(
        bool coldStart,
        bool partial,
        string saResetReason,
        string overloadInferenceSource,
        string calibrationStage,
        string trustTier,
        bool guidanceEnabled,
        bool suggestionsEnabled,
        bool systemActionsEnabled,
        bool overloadActive,
        bool lowQuality,
        long dwellOnMs,
        long dwellOffMs,
        long gapMs,
        bool deepWorkActive,
        long deepWorkDwellOnMs,
        long deepWorkDwellOffMs,
        long deepWorkGapMs,
        long deepWorkStartedAtUtcMs,
        bool deepWorkCandidate,
        bool deepWorkExitCandidate,
        bool deepWorkLowQualityPaused,
        bool notificationSuppressionActive,
        string notificationSuppressionActionState,
        bool notificationSuppressionRestorePending,
        string notificationSuppressionLastErrorCode,
        long notificationSuppressionEnableCooldownUntilUtcMs,
        long notificationSuppressionMinOnUntilUtcMs,
        long notificationSuppressionBackoffUntilUtcMs,
        bool brightnessAdjusted,
        string brightnessActionState,
        bool brightnessSupported,
        string brightnessLastErrorCode,
        long brightnessCooldownUntilUtcMs,
        long brightnessBackoffUntilUtcMs,
        object? overloadStateDebug = null,
        object? deepWorkStateDebug = null)
    {
        return BuildStateBucketStateFlagsJson(
            coldStart,
            partial,
            saResetReason,
            0d,
            0d,
            0d,
            false,
            "reset",
            0d,
            string.Empty,
            0d,
            "focus",
            0d,
            "steady_focus",
            "Steady focus episode.",
            false,
            0d,
            "focus_episode_active",
            "Focus episode active.",
            0d,
            0d,
            0d,
            overloadInferenceSource,
            calibrationStage,
            trustTier,
            guidanceEnabled,
            suggestionsEnabled,
            systemActionsEnabled,
            overloadActive,
            lowQuality,
            dwellOnMs,
            dwellOffMs,
            gapMs,
            deepWorkActive,
            deepWorkDwellOnMs,
            deepWorkDwellOffMs,
            deepWorkGapMs,
            deepWorkStartedAtUtcMs,
            deepWorkCandidate,
            deepWorkExitCandidate,
            deepWorkLowQualityPaused,
            notificationSuppressionActive,
            notificationSuppressionActionState,
            notificationSuppressionRestorePending,
            notificationSuppressionLastErrorCode,
            notificationSuppressionEnableCooldownUntilUtcMs,
            notificationSuppressionMinOnUntilUtcMs,
            notificationSuppressionBackoffUntilUtcMs,
            brightnessAdjusted,
            brightnessActionState,
            brightnessSupported,
            brightnessLastErrorCode,
            brightnessCooldownUntilUtcMs,
            brightnessBackoffUntilUtcMs,
            overloadStateDebug,
            deepWorkStateDebug);
    }

    internal static string BuildStateBucketStateFlagsJson(
        bool coldStart,
        bool partial,
        string saResetReason,
        double taskContinuityScore,
        double taskContinuitySeconds,
        double focusEpisodeCandidateSeconds,
        bool focusEpisodeActive,
        string continuityState,
        double continuityConfidence,
        string continuityAnchorAppHash,
        double continuityInstabilitySeconds,
        string attentionSegmentKind,
        double attentionSegmentSeconds,
        string attentionSegmentReasonCode,
        string attentionSegmentReasonText,
        bool distractionEpisodeActive,
        double distractionEpisodeSeconds,
        string focusEpisodeReasonCode,
        string focusEpisodeReasonText,
        double signalQualityScore,
        double modelConfidence,
        double pressureLevel,
        string overloadInferenceSource,
        string calibrationStage,
        string trustTier,
        bool guidanceEnabled,
        bool suggestionsEnabled,
        bool systemActionsEnabled,
        bool overloadActive,
        bool lowQuality,
        long dwellOnMs,
        long dwellOffMs,
        long gapMs,
        bool deepWorkActive,
        long deepWorkDwellOnMs,
        long deepWorkDwellOffMs,
        long deepWorkGapMs,
        long deepWorkStartedAtUtcMs,
        bool deepWorkCandidate,
        bool deepWorkExitCandidate,
        bool deepWorkLowQualityPaused,
        bool notificationSuppressionActive,
        string notificationSuppressionActionState,
        bool notificationSuppressionRestorePending,
        string notificationSuppressionLastErrorCode,
        long notificationSuppressionEnableCooldownUntilUtcMs,
        long notificationSuppressionMinOnUntilUtcMs,
        long notificationSuppressionBackoffUntilUtcMs,
        bool brightnessAdjusted,
        string brightnessActionState,
        bool brightnessSupported,
        string brightnessLastErrorCode,
        long brightnessCooldownUntilUtcMs,
        long brightnessBackoffUntilUtcMs,
        object? overloadStateDebug = null,
        object? deepWorkStateDebug = null)
    {
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            cold_start = coldStart ? 1 : 0,
            partial = partial ? 1 : 0,
            sa_reset_reason = saResetReason ?? string.Empty,
            task_continuity_score = Math.Clamp(taskContinuityScore, 0d, 1d),
            task_continuity_seconds = Math.Max(0d, taskContinuitySeconds),
            focus_episode_candidate_seconds = Math.Max(0d, focusEpisodeCandidateSeconds),
            focus_episode_active = focusEpisodeActive ? 1 : 0,
            continuity_state = string.IsNullOrWhiteSpace(continuityState) ? "reset" : continuityState.Trim().ToLowerInvariant(),
            continuity_confidence = Math.Clamp(continuityConfidence, 0d, 1d),
            continuity_anchor_app_hash = continuityAnchorAppHash ?? string.Empty,
            continuity_instability_seconds = Math.Max(0d, continuityInstabilitySeconds),
            attention_segment_kind = string.IsNullOrWhiteSpace(attentionSegmentKind) ? "focus" : attentionSegmentKind.Trim().ToLowerInvariant(),
            attention_segment_seconds = Math.Max(0d, attentionSegmentSeconds),
            attention_segment_reason_code = attentionSegmentReasonCode ?? string.Empty,
            attention_segment_reason_text = attentionSegmentReasonText ?? string.Empty,
            distraction_episode_active = distractionEpisodeActive ? 1 : 0,
            distraction_episode_seconds = Math.Max(0d, distractionEpisodeSeconds),
            focus_episode_reason_code = focusEpisodeReasonCode ?? string.Empty,
            focus_episode_reason_text = focusEpisodeReasonText ?? string.Empty,
            behavior_continuity_score = Math.Clamp(taskContinuityScore, 0d, 1d),
            signal_quality_score = Math.Clamp(signalQualityScore, 0d, 1d),
            model_confidence = Math.Clamp(modelConfidence, 0d, 1d),
            pressure_level = Math.Clamp(pressureLevel, 0d, 1d),
            overload_inference_source = overloadInferenceSource ?? string.Empty,
            calibration_stage = CalibrationTruth.NormalizeStage(calibrationStage),
            trust_tier = string.IsNullOrWhiteSpace(trustTier) ? CalibrationTruth.TrustLow : trustTier.Trim().ToLowerInvariant(),
            guidance_enabled = guidanceEnabled ? 1 : 0,
            suggestions_enabled = suggestionsEnabled ? 1 : 0,
            policies_enabled = suggestionsEnabled ? 1 : 0,
            system_actions_enabled = systemActionsEnabled ? 1 : 0,
            overload_active = overloadActive,
            low_quality = lowQuality,
            dwell_on_ms = Math.Max(0, dwellOnMs),
            dwell_off_ms = Math.Max(0, dwellOffMs),
            gap_s = Math.Max(0d, gapMs / 1000d),
            overload_state_debug = overloadStateDebug,
            deep_work_active = deepWorkActive,
            deep_work_dwell_on_ms = Math.Max(0, deepWorkDwellOnMs),
            deep_work_dwell_off_ms = Math.Max(0, deepWorkDwellOffMs),
            deep_work_gap_s = Math.Max(0d, deepWorkGapMs / 1000d),
            deep_work_started_at_utc_ms = Math.Max(0, deepWorkStartedAtUtcMs),
            dw_on_dwell_ms = Math.Max(0, deepWorkDwellOnMs),
            dw_off_dwell_ms = Math.Max(0, deepWorkDwellOffMs),
            dw_candidate = deepWorkCandidate ? 1 : 0,
            dw_exit = deepWorkExitCandidate ? 1 : 0,
            dw_low_quality = deepWorkLowQualityPaused ? 1 : 0,
            deep_work_state_debug = deepWorkStateDebug,
            notifications_suppressed = notificationSuppressionActive,
            notification_suppression_action_state = notificationSuppressionActionState ?? string.Empty,
            notification_suppression_restore_pending = notificationSuppressionRestorePending ? 1 : 0,
            notification_suppression_error = notificationSuppressionLastErrorCode ?? string.Empty,
            notification_enable_cooldown_until_utc_ms = Math.Max(0, notificationSuppressionEnableCooldownUntilUtcMs),
            notification_min_on_until_utc_ms = Math.Max(0, notificationSuppressionMinOnUntilUtcMs),
            notification_backoff_until_utc_ms = Math.Max(0, notificationSuppressionBackoffUntilUtcMs),
            brightness_adjusted = brightnessAdjusted,
            brightness_action_state = brightnessActionState ?? string.Empty,
            brightness_supported = brightnessSupported ? 1 : 0,
            brightness_error = brightnessLastErrorCode ?? string.Empty,
            brightness_cooldown_until_utc_ms = Math.Max(0, brightnessCooldownUntilUtcMs),
            brightness_backoff_until_utc_ms = Math.Max(0, brightnessBackoffUntilUtcMs)
        });
    }

    private static AgentBrainSnapshot ApplyOverloadInference(
        AgentBrainSnapshot snapshot,
        OverloadInferenceResult overloadInference)
    {
        double overload = overloadInference.OverloadLikelihood;
        double confidence = overloadInference.Confidence;
        double dominantValue = Math.Max(snapshot.FocusStrength, Math.Max(overload, snapshot.FatigueProbability));
        string dominantName = ResolveDominantStateName(snapshot.FocusStrength, overload, snapshot.FatigueProbability);

        return snapshot with
        {
            OverloadLikelihood = overload,
            Confidence = confidence,
            DominantStateName = dominantName,
            DominantStateValue = dominantValue
        };
    }

    private readonly record struct SignalDrivenStateMetrics(
        double FocusStrength,
        double FatigueProbability,
        double CognitiveStability,
        string DominantStateName,
        double DominantStateValue);

    private readonly record struct LegacySnapshotMetadata(
        string LastAction,
        bool ActivationWindowTriggered,
        bool CooldownTriggered,
        bool ActivationWindowOpen,
        IReadOnlyList<string> Contributors,
        string PolicyId,
        string Domain,
        string EscalationLevel,
        DateTimeOffset? PlannedEndTimestampUtc,
        DateTimeOffset? ActualEndTimestampUtc,
        DateTimeOffset? CooldownEndTimestampUtc)
    {
        public static LegacySnapshotMetadata Default { get; } = new(
            LastAction: "monitor_only",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: Array.Empty<string>(),
            PolicyId: "n/a",
            Domain: "control",
            EscalationLevel: "Inform",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            CooldownEndTimestampUtc: null);
    }

    private static SignalDrivenStateMetrics ComputeSignalDrivenStateMetrics(
        double typingSmoothed,
        double focusSwitchRateSmoothed,
        double overloadLikelihood)
    {
        double safeTyping = ClampUnitInterval(typingSmoothed);
        double safeFocusSwitchRate = ClampUnitInterval(focusSwitchRateSmoothed);
        double safeOverloadLikelihood = ClampUnitInterval(overloadLikelihood);
        double focusStrength = ClampUnitInterval(
            (safeTyping * 0.5d)
            + ((1d - safeFocusSwitchRate) * 0.3d)
            + ((1d - safeOverloadLikelihood) * 0.2d));
        double fatigueProbability = ClampUnitInterval(
            (safeOverloadLikelihood * 0.6d)
            + ((1d - focusStrength) * 0.4d));
        double cognitiveStability = ClampUnitInterval(1d - Math.Abs(safeOverloadLikelihood - focusStrength));
        double dominantStateValue = Math.Max(focusStrength, Math.Max(safeOverloadLikelihood, fatigueProbability));
        string dominantStateName = ResolveDominantStateName(focusStrength, safeOverloadLikelihood, fatigueProbability);

        return new SignalDrivenStateMetrics(
            FocusStrength: focusStrength,
            FatigueProbability: fatigueProbability,
            CognitiveStability: cognitiveStability,
            DominantStateName: dominantStateName,
            DominantStateValue: dominantStateValue);
    }

    private static double ClampUnitInterval(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private void InsertInputSignalWindow(
        SqliteConnection connection,
        SqliteTransaction tx,
        AgentInputSignalWindow window)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO input_signal_windows (
                accepted_at_utc,
                schema_ver,
                agent_instance_id,
                session_id,
                window_start_utc,
                window_end_utc,
                keyboard_keydown_count,
                keyboard_backspace_count,
                mouse_wheel_delta,
                mouse_move_dx,
                mouse_move_dy,
                focus_app_hash,
                idle_seconds,
                last_input_event_utc,
                keyboard_iki_mean_ms,
                keyboard_iki_std_ms,
                keyboard_iki_cv,
                keyboard_burst_count,
                keyboard_burst_rate,
                keyboard_correction_spike_count,
                keyboard_correction_spike_rate,
                mouse_move_sample_count,
                mouse_path_length_px,
                mouse_speed_mean_px_per_sec,
                mouse_jerk_p95,
                mouse_micro_correction_count,
                mouse_micro_correction_rate,
                mouse_wheel_event_count,
                mouse_scroll_burst_count,
                mouse_scroll_burst_rate,
                mouse_scroll_speed_mean,
                focus_observation_count,
                focus_switch_count,
                focus_active_app_count,
                focus_app_entropy,
                idle_observation_count,
                idle_seconds_within_window,
                micro_idle_count,
                micro_idle_seconds,
                break_seconds_within_window,
                short_idle_boundary_observed,
                notification_toasts_enabled
            ) VALUES (
                $accepted_at_utc,
                $schema_ver,
                $agent_instance_id,
                $session_id,
                $window_start_utc,
                $window_end_utc,
                $keyboard_keydown_count,
                $keyboard_backspace_count,
                $mouse_wheel_delta,
                $mouse_move_dx,
                $mouse_move_dy,
                $focus_app_hash,
                $idle_seconds,
                $last_input_event_utc,
                $keyboard_iki_mean_ms,
                $keyboard_iki_std_ms,
                $keyboard_iki_cv,
                $keyboard_burst_count,
                $keyboard_burst_rate,
                $keyboard_correction_spike_count,
                $keyboard_correction_spike_rate,
                $mouse_move_sample_count,
                $mouse_path_length_px,
                $mouse_speed_mean_px_per_sec,
                $mouse_jerk_p95,
                $mouse_micro_correction_count,
                $mouse_micro_correction_rate,
                $mouse_wheel_event_count,
                $mouse_scroll_burst_count,
                $mouse_scroll_burst_rate,
                $mouse_scroll_speed_mean,
                $focus_observation_count,
                $focus_switch_count,
                $focus_active_app_count,
                $focus_app_entropy,
                $idle_observation_count,
                $idle_seconds_within_window,
                $micro_idle_count,
                $micro_idle_seconds,
                $break_seconds_within_window,
                $short_idle_boundary_observed,
                $notification_toasts_enabled
            );
            """;
        cmd.Parameters.AddWithValue("$accepted_at_utc", window.AcceptedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$schema_ver", window.SchemaVer);
        cmd.Parameters.AddWithValue("$agent_instance_id", window.AgentInstanceId);
        cmd.Parameters.AddWithValue("$session_id", window.SessionId);
        cmd.Parameters.AddWithValue("$window_start_utc", window.WindowStartUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$window_end_utc", window.WindowEndUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$keyboard_keydown_count", window.KeyboardKeydownCount);
        cmd.Parameters.AddWithValue("$keyboard_backspace_count", window.KeyboardBackspaceCount);
        cmd.Parameters.AddWithValue("$mouse_wheel_delta", window.MouseWheelDelta);
        cmd.Parameters.AddWithValue("$mouse_move_dx", window.MouseMoveDx);
        cmd.Parameters.AddWithValue("$mouse_move_dy", window.MouseMoveDy);
        cmd.Parameters.AddWithValue("$focus_app_hash", window.FocusAppHash ?? string.Empty);
        cmd.Parameters.AddWithValue("$idle_seconds", window.IdleSeconds);
        cmd.Parameters.AddWithValue("$last_input_event_utc", window.LastInputEventUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$keyboard_iki_mean_ms", (object?)window.KeyboardIkiMeanMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$keyboard_iki_std_ms", (object?)window.KeyboardIkiStdMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$keyboard_iki_cv", (object?)window.KeyboardIkiCv ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$keyboard_burst_count", window.KeyboardBurstCount);
        cmd.Parameters.AddWithValue("$keyboard_burst_rate", (object?)window.KeyboardBurstRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$keyboard_correction_spike_count", window.KeyboardCorrectionSpikeCount);
        cmd.Parameters.AddWithValue("$keyboard_correction_spike_rate", (object?)window.KeyboardCorrectionSpikeRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mouse_move_sample_count", window.MouseMoveSampleCount);
        cmd.Parameters.AddWithValue("$mouse_path_length_px", (object?)window.MousePathLengthPx ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mouse_speed_mean_px_per_sec", (object?)window.MouseSpeedMeanPxPerSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mouse_jerk_p95", (object?)window.MouseJerkP95 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mouse_micro_correction_count", window.MouseMicroCorrectionCount);
        cmd.Parameters.AddWithValue("$mouse_micro_correction_rate", (object?)window.MouseMicroCorrectionRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mouse_wheel_event_count", window.MouseWheelEventCount);
        cmd.Parameters.AddWithValue("$mouse_scroll_burst_count", window.MouseScrollBurstCount);
        cmd.Parameters.AddWithValue("$mouse_scroll_burst_rate", (object?)window.MouseScrollBurstRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mouse_scroll_speed_mean", (object?)window.MouseScrollSpeedMean ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$focus_observation_count", window.FocusObservationCount);
        cmd.Parameters.AddWithValue("$focus_switch_count", window.FocusSwitchCount);
        cmd.Parameters.AddWithValue("$focus_active_app_count", window.FocusActiveAppCount);
        cmd.Parameters.AddWithValue("$focus_app_entropy", (object?)window.FocusAppEntropy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$idle_observation_count", window.IdleObservationCount);
        cmd.Parameters.AddWithValue("$idle_seconds_within_window", (object?)window.IdleSecondsWithinWindow ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$micro_idle_count", window.MicroIdleCount);
        cmd.Parameters.AddWithValue("$micro_idle_seconds", (object?)window.MicroIdleSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$break_seconds_within_window", (object?)window.BreakSecondsWithinWindow ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$short_idle_boundary_observed", window.ShortIdleBoundaryObserved ? 1 : 0);
        cmd.Parameters.AddWithValue("$notification_toasts_enabled", window.NotificationToastsEnabled.HasValue
            ? (window.NotificationToastsEnabled.Value ? 1 : 0)
            : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void UpsertInputBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow mappedRow)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO input_buckets (
                timestamp_utc,
                start_utc,
                bucket_seq,
                typing_burst_intensity,
                scroll_velocity_index,
                focus_switch_rate,
                keyboard_iki_mean,
                keyboard_iki_cv,
                keyboard_burst_rate,
                keyboard_backspace_rate,
                mouse_speed_mean,
                mouse_jerk_p95,
                mouse_scroll_speed,
                mouse_micro_correction_rate,
                focus_app_entropy,
                focus_switch_count,
                dominant_app_hash,
                focus_active_app_count,
                idle_seconds,
                idle_micro_idle_count,
                idle_micro_idle_seconds,
                true_break_seconds,
                true_break_flag,
                quality_score,
                partial,
                contributors_json
            ) VALUES (
                $ts, $start_utc, $bucket_seq, $typing, $scroll, $focus_switch,
                $keyboard_iki_mean, $keyboard_iki_cv, $keyboard_burst_rate, $keyboard_backspace_rate,
                $mouse_speed_mean, $mouse_jerk_p95, $mouse_scroll_speed, $mouse_micro_correction_rate,
                $focus_app_entropy, $focus_switch_count, $dominant_app_hash, $focus_active_app_count,
                $idle_seconds, $idle_micro_idle_count, $idle_micro_idle_seconds, $true_break_seconds, $true_break_flag,
                $quality_score, $partial, $contributors_json
            )
            ON CONFLICT(start_utc) DO UPDATE SET
                timestamp_utc = excluded.timestamp_utc,
                bucket_seq = excluded.bucket_seq,
                typing_burst_intensity = excluded.typing_burst_intensity,
                scroll_velocity_index = excluded.scroll_velocity_index,
                focus_switch_rate = excluded.focus_switch_rate,
                keyboard_iki_mean = excluded.keyboard_iki_mean,
                keyboard_iki_cv = excluded.keyboard_iki_cv,
                keyboard_burst_rate = excluded.keyboard_burst_rate,
                keyboard_backspace_rate = excluded.keyboard_backspace_rate,
                mouse_speed_mean = excluded.mouse_speed_mean,
                mouse_jerk_p95 = excluded.mouse_jerk_p95,
                mouse_scroll_speed = excluded.mouse_scroll_speed,
                mouse_micro_correction_rate = excluded.mouse_micro_correction_rate,
                focus_app_entropy = excluded.focus_app_entropy,
                focus_switch_count = excluded.focus_switch_count,
                dominant_app_hash = excluded.dominant_app_hash,
                focus_active_app_count = excluded.focus_active_app_count,
                idle_seconds = excluded.idle_seconds,
                idle_micro_idle_count = excluded.idle_micro_idle_count,
                idle_micro_idle_seconds = excluded.idle_micro_idle_seconds,
                true_break_seconds = excluded.true_break_seconds,
                true_break_flag = excluded.true_break_flag,
                quality_score = excluded.quality_score,
                partial = excluded.partial,
                contributors_json = excluded.contributors_json;
            """;
        cmd.Parameters.AddWithValue("$ts", mappedRow.TimestampUtc);
        cmd.Parameters.AddWithValue("$start_utc", mappedRow.StartUtc);
        cmd.Parameters.AddWithValue("$bucket_seq", mappedRow.BucketSeq);
        cmd.Parameters.AddWithValue("$typing", mappedRow.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll", mappedRow.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch", mappedRow.FocusSwitchRate);
        cmd.Parameters.AddWithValue("$keyboard_iki_mean", mappedRow.KeyboardIkiMean);
        cmd.Parameters.AddWithValue("$keyboard_iki_cv", mappedRow.KeyboardIkiCv);
        cmd.Parameters.AddWithValue("$keyboard_burst_rate", mappedRow.KeyboardBurstRate);
        cmd.Parameters.AddWithValue("$keyboard_backspace_rate", mappedRow.KeyboardBackspaceRate);
        cmd.Parameters.AddWithValue("$mouse_speed_mean", mappedRow.MouseSpeedMean);
        cmd.Parameters.AddWithValue("$mouse_jerk_p95", mappedRow.MouseJerkP95);
        cmd.Parameters.AddWithValue("$mouse_scroll_speed", mappedRow.MouseScrollSpeed);
        cmd.Parameters.AddWithValue("$mouse_micro_correction_rate", mappedRow.MouseMicroCorrectionRate);
        cmd.Parameters.AddWithValue("$focus_app_entropy", mappedRow.FocusAppEntropy);
        cmd.Parameters.AddWithValue("$focus_switch_count", mappedRow.FocusSwitchCount);
        cmd.Parameters.AddWithValue("$dominant_app_hash", mappedRow.DominantAppHash);
        cmd.Parameters.AddWithValue("$focus_active_app_count", mappedRow.FocusActiveAppCount);
        cmd.Parameters.AddWithValue("$idle_seconds", mappedRow.IdleSeconds);
        cmd.Parameters.AddWithValue("$idle_micro_idle_count", mappedRow.IdleMicroIdleCount);
        cmd.Parameters.AddWithValue("$idle_micro_idle_seconds", mappedRow.IdleMicroIdleSeconds);
        cmd.Parameters.AddWithValue("$true_break_seconds", mappedRow.TrueBreakSeconds);
        cmd.Parameters.AddWithValue("$true_break_flag", mappedRow.TrueBreakFlag);
        cmd.Parameters.AddWithValue("$quality_score", mappedRow.QualityScore);
        cmd.Parameters.AddWithValue("$partial", mappedRow.Partial);
        cmd.Parameters.AddWithValue("$contributors_json", mappedRow.ContributorsJson);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertPrimitiveBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        PrimitiveBucketWriteModel row)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO primitive_buckets (
                bucket_seq,
                start_utc,
                end_utc,
                primitives_json,
                contributors_json,
                why_json,
                state_flags_json,
                quality,
                partial
            ) VALUES (
                $bucket_seq, $start_utc, $end_utc, $primitives_json, $contributors_json, $why_json, $state_flags_json, $quality, $partial
            )
            ON CONFLICT(bucket_seq) DO UPDATE SET
                start_utc = excluded.start_utc,
                end_utc = excluded.end_utc,
                primitives_json = excluded.primitives_json,
                contributors_json = excluded.contributors_json,
                why_json = excluded.why_json,
                state_flags_json = excluded.state_flags_json,
                quality = excluded.quality,
                partial = excluded.partial;
            """;
        cmd.Parameters.AddWithValue("$bucket_seq", row.BucketSeq);
        cmd.Parameters.AddWithValue("$start_utc", row.StartUtc);
        cmd.Parameters.AddWithValue("$end_utc", row.EndUtc);
        cmd.Parameters.AddWithValue("$primitives_json", row.PrimitivesJson);
        cmd.Parameters.AddWithValue("$contributors_json", row.ContributorsJson);
        cmd.Parameters.AddWithValue("$why_json", row.WhyJson);
        cmd.Parameters.AddWithValue("$state_flags_json", row.StateFlagsJson);
        cmd.Parameters.AddWithValue("$quality", row.Quality);
        cmd.Parameters.AddWithValue("$partial", row.Partial);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertStateBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        StateBucketWriteModel row)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO state_buckets (
                timestamp_utc,
                typing_burst_intensity_raw,
                scroll_velocity_index_raw,
                focus_switch_rate_raw,
                typing_burst_intensity_smoothed,
                scroll_velocity_index_smoothed,
                focus_switch_rate_smoothed,
                focus_strength,
                overload_likelihood,
                fatigue_probability,
                cognitive_stability,
                dominant_state_name,
                dominant_state_value,
                confidence,
                sustained_attention_duration_sec,
                idle_fragmentation_index,
                activation_window_triggered,
                cooldown_triggered,
                activation_window_open,
                contributors_json,
                state_flags_json
            ) VALUES ($ts, $typing_raw, $scroll_raw, $focus_switch_raw, $typing_smooth, $scroll_smooth, $focus_switch_smooth, $focus, $overload, $fatigue, $stability, $dominant_name, $dominant_value, $confidence, $sustained_attention_duration_sec, $idle_fragmentation_index, $activation_triggered, $cooldown_triggered, $activation_open, $contributors, $state_flags_json)
            ON CONFLICT(timestamp_utc) DO UPDATE SET
                typing_burst_intensity_raw = excluded.typing_burst_intensity_raw,
                scroll_velocity_index_raw = excluded.scroll_velocity_index_raw,
                focus_switch_rate_raw = excluded.focus_switch_rate_raw,
                typing_burst_intensity_smoothed = excluded.typing_burst_intensity_smoothed,
                scroll_velocity_index_smoothed = excluded.scroll_velocity_index_smoothed,
                focus_switch_rate_smoothed = excluded.focus_switch_rate_smoothed,
                focus_strength = excluded.focus_strength,
                overload_likelihood = excluded.overload_likelihood,
                fatigue_probability = excluded.fatigue_probability,
                cognitive_stability = excluded.cognitive_stability,
                dominant_state_name = excluded.dominant_state_name,
                dominant_state_value = excluded.dominant_state_value,
                confidence = excluded.confidence,
                sustained_attention_duration_sec = excluded.sustained_attention_duration_sec,
                idle_fragmentation_index = excluded.idle_fragmentation_index,
                activation_window_triggered = excluded.activation_window_triggered,
                cooldown_triggered = excluded.cooldown_triggered,
                activation_window_open = excluded.activation_window_open,
                contributors_json = excluded.contributors_json,
                state_flags_json = excluded.state_flags_json;
            """;
        cmd.Parameters.AddWithValue("$ts", row.TimestampUtc);
        cmd.Parameters.AddWithValue("$typing_raw", row.TypingRaw);
        cmd.Parameters.AddWithValue("$scroll_raw", row.ScrollRaw);
        cmd.Parameters.AddWithValue("$focus_switch_raw", row.FocusRaw);
        cmd.Parameters.AddWithValue("$typing_smooth", row.TypingSmoothed);
        cmd.Parameters.AddWithValue("$scroll_smooth", row.ScrollSmoothed);
        cmd.Parameters.AddWithValue("$focus_switch_smooth", row.FocusSmoothed);
        cmd.Parameters.AddWithValue("$focus", row.FocusStrength);
        cmd.Parameters.AddWithValue("$overload", row.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$fatigue", row.FatigueProbability);
        cmd.Parameters.AddWithValue("$stability", row.CognitiveStability);
        cmd.Parameters.AddWithValue("$dominant_name", row.DominantStateName);
        cmd.Parameters.AddWithValue("$dominant_value", row.DominantStateValue);
        cmd.Parameters.AddWithValue("$confidence", row.Confidence);
        cmd.Parameters.AddWithValue("$sustained_attention_duration_sec", row.SustainedAttentionDurationSec);
        cmd.Parameters.AddWithValue("$idle_fragmentation_index", row.IdleFragmentationIndex);
        cmd.Parameters.AddWithValue("$activation_triggered", row.ActivationWindowTriggered);
        cmd.Parameters.AddWithValue("$cooldown_triggered", row.CooldownTriggered);
        cmd.Parameters.AddWithValue("$activation_open", row.ActivationWindowOpen);
        cmd.Parameters.AddWithValue("$contributors", row.ContributorsJson);
        cmd.Parameters.AddWithValue("$state_flags_json", row.StateFlagsJson);
        cmd.ExecuteNonQuery();
    }

    private readonly record struct PrimitiveBucketWriteModel(
        long BucketSeq,
        string StartUtc,
        string EndUtc,
        string PrimitivesJson,
        string ContributorsJson,
        string WhyJson,
        string StateFlagsJson,
        double Quality,
        int Partial);

    private readonly record struct StateBucketWriteModel(
        string TimestampUtc,
        double TypingRaw,
        double ScrollRaw,
        double FocusRaw,
        double TypingSmoothed,
        double ScrollSmoothed,
        double FocusSmoothed,
        double FocusStrength,
        double OverloadLikelihood,
        double FatigueProbability,
        double CognitiveStability,
        string DominantStateName,
        double DominantStateValue,
        double Confidence,
        double SustainedAttentionDurationSec,
        double IdleFragmentationIndex,
        int ActivationWindowTriggered,
        int CooldownTriggered,
        int ActivationWindowOpen,
        string ContributorsJson,
        string StateFlagsJson);

    private void InsertLegacySnapshot(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            DELETE FROM brain_snapshots WHERE timestamp_utc = $ts;
            INSERT INTO brain_snapshots (
                timestamp_utc,
                typing_burst_intensity,
                scroll_velocity_index,
                focus_switch_rate,
                smoothed_typing_burst_intensity,
                smoothed_scroll_velocity_index,
                smoothed_focus_switch_rate,
                focus_strength,
                overload_likelihood,
                fatigue_probability,
                cognitive_stability,
                dominant_state_name,
                dominant_state_value,
                confidence,
                last_action,
                activation_window_triggered,
                cooldown_triggered,
                activation_window_open,
                contributors_json,
                policy_id,
                domain,
                escalation_level,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                cooldown_end_timestamp_utc
            ) VALUES (
                $ts, $typing, $scroll, $focus_switch, $typing_smooth, $scroll_smooth, $focus_switch_smooth,
                $focus_strength, $overload, $fatigue, $stability, $dominant_name, $dominant_value,
                $confidence, $last_action, $activation_triggered, $cooldown_triggered, $activation_open, $contributors,
                $policy_id, $domain, $escalation, $planned_end, $actual_end, $cooldown_end
            );
            """;
        cmd.Parameters.AddWithValue("$ts", snapshot.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$typing", snapshot.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll", snapshot.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch", snapshot.FocusSwitchRate);
        cmd.Parameters.AddWithValue("$typing_smooth", snapshot.SmoothedTypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll_smooth", snapshot.SmoothedScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus_switch_smooth", snapshot.SmoothedFocusSwitchRate);
        cmd.Parameters.AddWithValue("$focus_strength", snapshot.FocusStrength);
        cmd.Parameters.AddWithValue("$overload", snapshot.OverloadLikelihood);
        cmd.Parameters.AddWithValue("$fatigue", snapshot.FatigueProbability);
        cmd.Parameters.AddWithValue("$stability", snapshot.CognitiveStability);
        cmd.Parameters.AddWithValue("$dominant_name", snapshot.DominantStateName);
        cmd.Parameters.AddWithValue("$dominant_value", snapshot.DominantStateValue);
        cmd.Parameters.AddWithValue("$confidence", snapshot.Confidence);
        cmd.Parameters.AddWithValue("$last_action", snapshot.LastAction);
        cmd.Parameters.AddWithValue("$activation_triggered", snapshot.ActivationWindowTriggered ? 1 : 0);
        cmd.Parameters.AddWithValue("$cooldown_triggered", snapshot.CooldownTriggered ? 1 : 0);
        cmd.Parameters.AddWithValue("$activation_open", snapshot.ActivationWindowOpen ? 1 : 0);
        cmd.Parameters.AddWithValue("$contributors", JsonSerializer.Serialize(snapshot.Contributors));
        cmd.Parameters.AddWithValue("$policy_id", snapshot.PolicyId);
        cmd.Parameters.AddWithValue("$domain", snapshot.Domain);
        cmd.Parameters.AddWithValue("$escalation", snapshot.EscalationLevel);
        cmd.Parameters.AddWithValue("$planned_end", snapshot.PlannedEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$actual_end", snapshot.ActualEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$cooldown_end", snapshot.CooldownEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    private void TryInsertAutomaticInterventionEvent(SqliteConnection connection, SqliteTransaction tx, AgentBrainSnapshot snapshot)
    {
        string currentAction = snapshot.LastAction?.Trim() ?? string.Empty;
        bool currentIsIntervention = !string.IsNullOrWhiteSpace(currentAction) && !IsNonInterventionAction(currentAction);

        if (_activeIntervention != null)
        {
            bool actionChanged = !string.Equals(_activeIntervention.Action, currentAction, StringComparison.Ordinal);
            bool plannedExpired = _activeIntervention.PlannedEndTimestampUtc.HasValue
                && snapshot.TimestampUtc >= _activeIntervention.PlannedEndTimestampUtc.Value;
            if (!currentIsIntervention || actionChanged || plannedExpired)
            {
                string reason = !currentIsIntervention
                    ? $"deactivated_due_to:{currentAction}"
                    : actionChanged
                        ? $"deactivated_due_to_action_change:{currentAction}"
                        : "deactivated_due_to_planned_end";
                InsertAutomaticDeactivationEvent(connection, tx, snapshot, reason);
            }
        }

        if (!currentIsIntervention)
        {
            return;
        }

        if (string.Equals(_lastAutoEventAction, currentAction, StringComparison.Ordinal)
            && (snapshot.TimestampUtc - _lastAutoEventAtUtc) < TimeSpan.FromSeconds(DefaultEventCooldownSeconds))
        {
            return;
        }

        (string triggerState, double triggerValue) = ResolveTrigger(snapshot);
        string escalationLevel = string.IsNullOrWhiteSpace(snapshot.EscalationLevel)
            ? ResolveEscalationLevel(currentAction)
            : snapshot.EscalationLevel;
        bool reversible = IsReversibleAction(currentAction);
        int? durationSeconds = ResolveDurationSeconds(currentAction);
        DateTimeOffset? plannedEndTimestampUtc = snapshot.PlannedEndTimestampUtc
            ?? (durationSeconds.HasValue ? snapshot.TimestampUtc.AddSeconds(Math.Max(durationSeconds.Value, 0)) : null);
        string interventionId = Guid.NewGuid().ToString("N");
        string whatChanged = ResolveWhatChanged(currentAction, lifecycleState: "activated");
        string howToUndo = ResolveHowToUndo(currentAction, reversible, lifecycleState: "activated");

        var ev = new AgentControlEvent(
            TimestampUtc: snapshot.TimestampUtc,
            Action: currentAction,
            TriggerState: triggerState,
            TriggerValue: triggerValue,
            Confidence: snapshot.Confidence,
            DurationSeconds: durationSeconds,
            EscalationLevel: escalationLevel,
            Reversible: reversible,
            Reason: ResolveAutomaticReason(currentAction, triggerState, triggerValue, snapshot.Confidence, reversible),
            Contributors: snapshot.Contributors,
            InterventionId: interventionId,
            PolicyId: string.IsNullOrWhiteSpace(snapshot.PolicyId) ? "n/a" : snapshot.PolicyId,
            Domain: string.IsNullOrWhiteSpace(snapshot.Domain) ? "control" : snapshot.Domain,
            PlannedEndTimestampUtc: plannedEndTimestampUtc,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: JsonSerializer.Serialize(new
            {
                previous_action = _activeIntervention?.Action ?? "none",
                previous_escalation = _activeIntervention?.EscalationLevel ?? "Inform"
            }),
            WhatChanged: whatChanged,
            HowToUndo: howToUndo,
            ReversalApplied: false,
            CooldownEndTimestampUtc: snapshot.CooldownEndTimestampUtc,
            LifecycleState: "activated");

        try
        {
            InsertInterventionEvent(connection, tx, ev);
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
            return;
        }

        InsertAutomaticSystemChange(connection, tx, ev);
        _activeIntervention = new ActiveInterventionState(
            InterventionId: interventionId,
            Action: currentAction,
            EscalationLevel: escalationLevel,
            PolicyId: ev.PolicyId,
            Domain: ev.Domain,
            StartTimestampUtc: snapshot.TimestampUtc,
            PlannedEndTimestampUtc: plannedEndTimestampUtc,
            CooldownEndTimestampUtc: snapshot.CooldownEndTimestampUtc);
        _lastAutoEventAction = currentAction;
        _lastAutoEventAtUtc = snapshot.TimestampUtc;
    }

    internal static bool TryInsertBreakSuggestionPolicyEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        AgentBrainSnapshot snapshot,
        InputBucketRow inputBucket,
        BaselineGatingResult gating,
        string trustTier,
        DerivedSessionMetricsResult derivedSession,
        OverloadInferenceResult overloadInference,
        OverloadStabilizationResult overloadStabilization,
        BreakSuggestionDecision decision,
        RuntimePolicyEnvelope? policyEnvelope = null,
        BreakPersonalizationSnapshot? personalization = null,
        int dismissStreak = 0)
    {
        if (!decision.ShouldEmit)
        {
            return false;
        }

        RuntimeBreakPolicySettings breakPolicy = policyEnvelope?.Settings.BreakPolicy
            ?? DefaultBreakPolicySettings();
        string policyVersion = policyEnvelope?.Version ?? "local-default";
        string action = string.IsNullOrWhiteSpace(decision.Action)
            ? PortaBreakSemantics.LongBreakKind
            : decision.Action.Trim().ToLowerInvariant();
        string whyJson = BuildBreakSuggestionWhyJson(
            inputBucket,
            derivedSession,
            gating,
            trustTier,
            overloadInference,
            overloadStabilization,
            decision,
            breakPolicy,
            policyVersion,
            personalization,
            dismissStreak);
        DateTimeOffset finalizeTimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            Time.bucketStartEnd(inputBucket.BucketSeq).endUtcMs);
        var ev = new AgentControlEvent(
            TimestampUtc: finalizeTimestampUtc,
            Action: action,
            TriggerState: decision.HighPressure ? "overload" : "focus",
            TriggerValue: snapshot.OverloadLikelihood,
            Confidence: snapshot.Confidence,
            DurationSeconds: string.Equals(decision.BreakKind, PortaBreakSemantics.ShortBreakKind, StringComparison.Ordinal) ? 180 : 300,
            EscalationLevel: "Suggest",
            Reversible: false,
            Reason: decision.ReasonCode,
            Contributors: snapshot.Contributors,
            InterventionId: Guid.NewGuid().ToString("N"),
            PolicyId: $"break-policy:{policyVersion}",
            Domain: "fatigue",
            PlannedEndTimestampUtc: finalizeTimestampUtc.AddSeconds(breakPolicy.CooldownSeconds),
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{\"previous_action\":\"none\"}",
            WhatChanged: ResolveWhatChanged(action, "activated"),
            HowToUndo: "Dismiss suggestion toast.",
            ReversalApplied: false,
            CooldownEndTimestampUtc: finalizeTimestampUtc.AddSeconds(breakPolicy.CooldownSeconds),
            LifecycleState: "activated",
            WhyJsonOverride: whyJson,
            IdempotencyKey: BuildBreakEventIdempotencyKey(
                nowUtcMs: finalizeTimestampUtc.ToUnixTimeMilliseconds(),
                installIdHash: BreakInstallIdHash.Value,
                cooldownWindowMs: breakPolicy.CooldownSeconds * 1000L));

        try
        {
            InsertInterventionEvent(connection, tx, ev);
            return true;
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Hard no-duplicate path: if idempotency key already exists, suppress silently.
            return false;
        }
    }

    internal static string BuildBreakSuggestionWhyJson(
        InputBucketRow inputBucket,
        DerivedSessionMetricsResult derivedSession,
        BaselineGatingResult gating,
        string trustTier,
        OverloadInferenceResult overloadInference,
        OverloadStabilizationResult overloadStabilization,
        BreakSuggestionDecision decision,
        RuntimeBreakPolicySettings? policy = null,
        string policyVersion = "local-default",
        BreakPersonalizationSnapshot? personalization = null,
        int dismissStreak = 0)
    {
        RuntimeBreakPolicySettings settings = policy ?? DefaultBreakPolicySettings();
        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            policy = $"break_suggestion:{policyVersion}",
            reasons = new
            {
                guidance_enabled = gating.GuidanceEnabled,
                suggestions_enabled = gating.SuggestionsEnabled,
                calibration_stage = gating.CalibrationStage,
                trust_tier = CalibrationTruth.NormalizeTrustTier(trustTier),
                overload_active = overloadStabilization.OverloadActive,
                overload_likelihood = overloadInference.OverloadLikelihood,
                pressure_level = overloadInference.PressureLevel,
                high_pressure = decision.HighPressure,
                confidence = overloadInference.Confidence,
                task_continuity_s = derivedSession.TaskContinuitySeconds,
                task_continuity_score = derivedSession.TaskContinuityScore,
                effective_focus_s = decision.EffectiveFocusSeconds,
                effective_focus_window_s = decision.EffectiveFocusWindowSeconds,
                action = decision.Action,
                break_kind = decision.BreakKind,
                delivery_boundary = string.IsNullOrWhiteSpace(decision.DeliveryBoundaryKind) ? "none" : decision.DeliveryBoundaryKind,
                cooldown_expired = decision.CooldownChecked && decision.Cooldown.CooldownExpired,
                // Workstream 1 — the personal gate, spelled out so any nudge can be audited:
                // which mode gated it, against what threshold, and the backoff in effect.
                need_gate_mode = string.IsNullOrWhiteSpace(decision.NeedGateMode)
                    ? (personalization?.NeedGateMode ?? "cold_start_absolute")
                    : decision.NeedGateMode,
                need_gate_reason = decision.NeedGateReason,
                personal_pressure_threshold = personalization?.PersonalPressureThreshold,
                pressure_declining = personalization?.PressureDeclining ?? false,
                dismiss_streak = dismissStreak,
                cooldown_effective_s = BreakBackoffPolicy.EffectiveCooldownSeconds(dismissStreak, settings.CooldownSeconds),
                overload_on_dwell_ms = overloadStabilization.RuntimeState.OnDwellMs
            },
            thresholds = new
            {
                confidence_min = settings.ConfidenceMin,
                short_break_window_s = settings.ShortBreakWindowSeconds,
                short_break_effective_focus_min_low_s = settings.ShortBreakEffectiveFocusSecondsLowPressure,
                short_break_effective_focus_min_high_s = settings.ShortBreakEffectiveFocusSecondsHighPressure,
                long_break_window_s = settings.LongBreakWindowSeconds,
                long_break_effective_focus_min_low_s = settings.LongBreakEffectiveFocusSecondsLowPressure,
                long_break_effective_focus_min_high_s = settings.LongBreakEffectiveFocusSecondsHighPressure,
                // Backward-compatible aliases for older diagnostics readers.
                microbreak_window_s = settings.ShortBreakWindowSeconds,
                microbreak_effective_focus_min_low_s = settings.ShortBreakEffectiveFocusSecondsLowPressure,
                microbreak_effective_focus_min_high_s = settings.ShortBreakEffectiveFocusSecondsHighPressure,
                legacy_short_break_window_s = settings.LongBreakWindowSeconds,
                legacy_short_break_effective_focus_min_low_s = settings.LongBreakEffectiveFocusSecondsLowPressure,
                legacy_short_break_effective_focus_min_high_s = settings.LongBreakEffectiveFocusSecondsHighPressure,
                cooldown_min_s = settings.CooldownSeconds
            },
            context = new
            {
                bucket_seq = inputBucket.BucketSeq,
                dominant_app_hash = derivedSession.DominantAppHash
            }
        });
    }

    private static void TryInsertDeepWorkNotificationEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow inputBucket,
        DeepWorkTransitionExecution transitionExecution,
        DeepWorkStabilizationResult deepWorkStabilization,
        RuntimePolicyEnvelope? policyEnvelope = null)
    {
        string policyVersion = policyEnvelope?.Version ?? "local-default";
        DeepWorkActionOutcome outcome = transitionExecution.Outcome;
        string action = ResolveDeepWorkNotificationAction(outcome);
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        DateTimeOffset eventTimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            Time.bucketStartEnd(inputBucket.BucketSeq).endUtcMs);
        string lifecycleState = action switch
        {
            "deep_work_notifications_suppressed" => "activated",
            "deep_work_notifications_restored" => "deactivated",
            _ => "failed"
        };
        string operation = ResolveDeepWorkNotificationOperation(action, outcome);
        long eventTsMs = eventTimestampUtc.ToUnixTimeMilliseconds();
        RuntimeDeepWorkDetectionSettings deepWorkPolicy = policyEnvelope?.Settings.DeepWorkDetection
            ?? new RuntimeDeepWorkDetectionSettings(
                PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds,
                PortaExecutionConstants.DeepWorkDetection.ConfidenceMin,
                PortaExecutionConstants.DeepWorkDetection.FragmentationMax,
                PortaExecutionConstants.DeepWorkDetection.VolatilityMax,
                PortaExecutionConstants.DeepWorkDetection.IntensityMin,
                PortaExecutionConstants.DeepWorkDetection.IntensityMax,
                PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds,
                PortaExecutionConstants.DeepWorkDetection.DwellOffSeconds,
                PortaExecutionConstants.DeepWorkDetection.GapAllowSeconds);
        string interventionId = $"dw_notify:{action}:{ResolveDeepWorkTransitionWindowStartMs(action, eventTsMs, deepWorkPolicy)}";
        string deterministicIdempotencyKey = BuildDeepWorkNotificationIdempotencyKey(action, eventTsMs, deepWorkPolicy);
        string whyJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            policy = $"deep_work_notifications:{policyVersion}",
            operation,
            error_code = outcome.ErrorCode,
            error_detail = outcome.ErrorDetail,
            backoff_until_utc_ms = transitionExecution.RuntimeState.BackoffUntilUtcMs,
            deep_work_active = deepWorkStabilization.DeepWorkActive,
            outcome = new
            {
                reason = outcome.OutcomeReason,
                succeeded = outcome.Succeeded,
                entering = outcome.EnteringDeepWork,
                exiting = outcome.ExitingDeepWork,
                suppression_attempted = outcome.SuppressionAttempted,
                restore_attempted = outcome.RestoreAttempted,
                state_changed = outcome.StateChanged,
                error_code = outcome.ErrorCode,
                error_detail = outcome.ErrorDetail
            },
            deep_work = new
            {
                active = deepWorkStabilization.DeepWorkActive,
                confidence = deepWorkStabilization.Confidence,
                f_component = deepWorkStabilization.FComponent,
                i_component = deepWorkStabilization.IComponent,
                v_component = deepWorkStabilization.VComponent,
                sustained_attention_duration_sec = deepWorkStabilization.SustainedAttentionDurationSec
            },
            notification_suppression = new
            {
                active = transitionExecution.RuntimeState.Active,
                action_state = transitionExecution.RuntimeState.ActionState,
                restore_pending = transitionExecution.RuntimeState.RestorePending,
                enable_cooldown_until_utc_ms = transitionExecution.RuntimeState.EnableCooldownUntilUtcMs,
                min_on_until_utc_ms = transitionExecution.RuntimeState.MinOnUntilUtcMs,
                backoff_until_utc_ms = transitionExecution.RuntimeState.BackoffUntilUtcMs,
                last_error_code = transitionExecution.RuntimeState.LastErrorCode
            },
            context = new
            {
                bucket_seq = inputBucket.BucketSeq
            }
        });

        var ev = new AgentControlEvent(
            TimestampUtc: eventTimestampUtc,
            Action: action,
            TriggerState: "deep_work",
            TriggerValue: deepWorkStabilization.Confidence,
            Confidence: deepWorkStabilization.Confidence,
            DurationSeconds: null,
            EscalationLevel: "Auto",
            Reversible: true,
            Reason: action,
            Contributors: Array.Empty<string>(),
            InterventionId: interventionId,
            PolicyId: $"deep_work_notifications:{policyVersion}",
            Domain: "output_action",
            PlannedEndTimestampUtc: eventTimestampUtc.AddMinutes(10),
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: ResolveDeepWorkNotificationWhatChanged(action),
            HowToUndo: "Automatically restored when deep work exits.",
            ReversalApplied: action == "deep_work_notifications_restored",
            CooldownEndTimestampUtc: null,
            LifecycleState: lifecycleState,
            WhyJsonOverride: whyJson,
            IdempotencyKey: deterministicIdempotencyKey);

        try
        {
            InsertInterventionEvent(connection, tx, ev);
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
            // idempotency key already inserted for this finalized bucket.
        }
    }

    private static void InsertStartupNotificationSuppressionDiagnosticEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        DateTimeOffset nowUtc,
        NotificationSuppressionStartupReconciliation reconciliation,
        RuntimePolicyEnvelope? policyEnvelope = null)
    {
        if (string.IsNullOrWhiteSpace(reconciliation.DiagnosticEventAction))
        {
            return;
        }

        string policyVersion = policyEnvelope?.Version ?? "local-default";
        string interventionId = $"startup_notify_diag:{reconciliation.DiagnosticEventAction}:{nowUtc.ToUnixTimeMilliseconds()}";
        string diagnosticIdempotencyKey = $"startup_notify_diag:v1:{reconciliation.DiagnosticEventAction}:{ResolveWindowStartMs(nowUtc.ToUnixTimeMilliseconds(), 300000)}";
        string whyJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            policy = $"deep_work_notifications_startup_reconcile:{policyVersion}",
            outcome_reason = reconciliation.OutcomeReason,
            error_code = reconciliation.ErrorCode,
            error_detail = reconciliation.ErrorDetail,
            notification_suppression = new
            {
                active = reconciliation.RuntimeState.Active,
                action_state = reconciliation.RuntimeState.ActionState,
                restore_pending = reconciliation.RuntimeState.RestorePending,
                backoff_until_utc_ms = reconciliation.RuntimeState.BackoffUntilUtcMs,
                min_on_until_utc_ms = reconciliation.RuntimeState.MinOnUntilUtcMs,
                enable_cooldown_until_utc_ms = reconciliation.RuntimeState.EnableCooldownUntilUtcMs
            }
        });

        var ev = new AgentControlEvent(
            TimestampUtc: nowUtc,
            Action: reconciliation.DiagnosticEventAction,
            TriggerState: "startup_reconcile",
            TriggerValue: 0d,
            Confidence: 0d,
            DurationSeconds: null,
            EscalationLevel: "Error",
            Reversible: false,
            Reason: string.IsNullOrWhiteSpace(reconciliation.ErrorCode)
                ? reconciliation.OutcomeReason
                : reconciliation.ErrorCode,
            Contributors: Array.Empty<string>(),
            InterventionId: interventionId,
            PolicyId: $"deep_work_notifications_startup_reconcile:{policyVersion}",
            Domain: "output_action",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: "Startup reconciliation detected notification suppression state issue.",
            HowToUndo: "No direct undo. Runtime state will self-heal after reconciliation.",
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "failed",
            WhyJsonOverride: whyJson,
            IdempotencyKey: diagnosticIdempotencyKey);

        try
        {
            InsertInterventionEvent(connection, tx, ev);
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
        }
    }

    private static void TryInsertOverloadBrightnessEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow inputBucket,
        BrightnessActionRuntimeState previousBrightnessState,
        OverloadBrightnessTransitionExecution transitionExecution,
        OverloadStabilizationResult overloadStabilization,
        OverloadInferenceResult overloadInference,
        RuntimePolicyEnvelope? policyEnvelope = null)
    {
        string policyVersion = policyEnvelope?.Version ?? "local-default";
        string action = ResolveOverloadBrightnessAction(previousBrightnessState, transitionExecution);
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        DateTimeOffset eventTimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            Time.bucketStartEnd(inputBucket.BucketSeq).endUtcMs);
        long eventTsMs = eventTimestampUtc.ToUnixTimeMilliseconds();
        string lifecycleState = action switch
        {
            "overload_brightness_reduced" => "activated",
            "overload_brightness_restored" => "deactivated",
            _ => "failed"
        };
        string interventionId = $"ovr_brightness:{action}:{ResolveOverloadBrightnessTransitionWindowStartMs(action, eventTsMs)}";
        RuntimeOverloadStabilizationSettings overloadPolicy = policyEnvelope?.Settings.OverloadStabilization
            ?? DefaultOverloadStabilizationSettings();
        string deterministicIdempotencyKey = BuildOverloadBrightnessIdempotencyKey(action, eventTsMs, overloadPolicy);
        string whyJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            policy = $"overload_brightness:{policyVersion}",
            action,
            reason = ResolveOverloadBrightnessReason(action, transitionExecution.Outcome),
            outcome = new
            {
                reason = transitionExecution.Outcome.OutcomeReason,
                succeeded = transitionExecution.Outcome.Succeeded,
                entering_overload = transitionExecution.Outcome.EnteringOverload,
                exiting_overload = transitionExecution.Outcome.ExitingOverload,
                reduce_attempted = transitionExecution.Outcome.ReduceAttempted,
                restore_attempted = transitionExecution.Outcome.RestoreAttempted,
                state_changed = transitionExecution.Outcome.StateChanged,
                error_code = transitionExecution.Outcome.ErrorCode,
                error_detail = transitionExecution.Outcome.ErrorDetail
            },
            brightness = new
            {
                previous = new
                {
                    active = previousBrightnessState.Active,
                    supported = previousBrightnessState.BrightnessSupported,
                    prev_level = previousBrightnessState.PreviousLevel,
                    target_level = previousBrightnessState.TargetLevel,
                    current_level = previousBrightnessState.CurrentLevel,
                    ramp_direction = previousBrightnessState.RampDirection,
                    last_change_utc_ms = previousBrightnessState.LastChangeUtcMs,
                    cooldown_until_utc_ms = previousBrightnessState.CooldownUntilUtcMs,
                    backoff_until_utc_ms = previousBrightnessState.BackoffUntilUtcMs,
                    last_error_code = previousBrightnessState.LastErrorCode,
                    logged_unsupported = previousBrightnessState.LoggedUnsupported
                },
                current = new
                {
                    active = transitionExecution.RuntimeState.Active,
                    supported = transitionExecution.RuntimeState.BrightnessSupported,
                    prev_level = transitionExecution.RuntimeState.PreviousLevel,
                    target_level = transitionExecution.RuntimeState.TargetLevel,
                    current_level = transitionExecution.RuntimeState.CurrentLevel,
                    ramp_direction = transitionExecution.RuntimeState.RampDirection,
                    last_change_utc_ms = transitionExecution.RuntimeState.LastChangeUtcMs,
                    cooldown_until_utc_ms = transitionExecution.RuntimeState.CooldownUntilUtcMs,
                    backoff_until_utc_ms = transitionExecution.RuntimeState.BackoffUntilUtcMs,
                    last_error_code = transitionExecution.RuntimeState.LastErrorCode,
                    logged_unsupported = transitionExecution.RuntimeState.LoggedUnsupported
                }
            },
            overload = new
            {
                active = overloadStabilization.OverloadActive,
                likelihood = overloadInference.OverloadLikelihood,
                confidence = overloadInference.Confidence,
                on_dwell_ms = overloadStabilization.RuntimeState.OnDwellMs,
                off_dwell_ms = overloadStabilization.RuntimeState.OffDwellMs
            },
            context = new
            {
                bucket_seq = inputBucket.BucketSeq
            }
        });

        var ev = new AgentControlEvent(
            TimestampUtc: eventTimestampUtc,
            Action: action,
            TriggerState: "overload",
            TriggerValue: overloadInference.OverloadLikelihood,
            Confidence: overloadInference.Confidence,
            DurationSeconds: null,
            EscalationLevel: "Auto",
            Reversible: true,
            Reason: action,
            Contributors: Array.Empty<string>(),
            InterventionId: interventionId,
            PolicyId: $"overload_brightness:{policyVersion}",
            Domain: "output_action",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: ResolveOverloadBrightnessWhatChanged(action),
            HowToUndo: "Automatically restored after overload exits and restore ramp completes.",
            ReversalApplied: action == "overload_brightness_restored",
            CooldownEndTimestampUtc: null,
            LifecycleState: lifecycleState,
            WhyJsonOverride: whyJson,
            IdempotencyKey: deterministicIdempotencyKey);

        try
        {
            InsertInterventionEvent(connection, tx, ev);
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
        }
    }

    private static void TryInsertOverloadRecoveryEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow inputBucket,
        OverloadStabilizationResult overloadStabilization,
        OverloadInferenceResult overloadInference,
        RuntimePolicyEnvelope? policyEnvelope = null)
    {
        if (!overloadStabilization.Transitioned
            || !overloadStabilization.PreviousOverloadActive
            || overloadStabilization.OverloadActive)
        {
            return;
        }

        RuntimeOverloadStabilizationSettings overloadPolicy = policyEnvelope?.Settings.OverloadStabilization
            ?? DefaultOverloadStabilizationSettings();
        string policyVersion = policyEnvelope?.Version ?? "local-default";
        DateTimeOffset eventTimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            Time.bucketStartEnd(inputBucket.BucketSeq).endUtcMs);
        long eventTsMs = eventTimestampUtc.ToUnixTimeMilliseconds();
        const string action = "overload_recovered";

        string whyJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            policy = $"overload_stabilization:{policyVersion}",
            action,
            reason = "overload_active_transition_off",
            overload = new
            {
                previous_overload_active = overloadStabilization.PreviousOverloadActive,
                overload_active = overloadStabilization.OverloadActive,
                likelihood = overloadInference.OverloadLikelihood,
                confidence = overloadInference.Confidence,
                on_dwell_ms = overloadStabilization.RuntimeState.OnDwellMs,
                off_dwell_ms = overloadStabilization.RuntimeState.OffDwellMs,
                gap_s = overloadStabilization.GapMs / 1000d
            },
            context = new
            {
                bucket_seq = inputBucket.BucketSeq
            }
        });

        string interventionId = $"overload_recovery:v1:{ResolveWindowStartMs(eventTsMs, overloadPolicy.DwellOffSeconds * 1000L)}";
        string deterministicIdempotencyKey = BuildOverloadRecoveryIdempotencyKey(eventTsMs, overloadPolicy);
        var ev = new AgentControlEvent(
            TimestampUtc: eventTimestampUtc,
            Action: action,
            TriggerState: "overload",
            TriggerValue: overloadInference.OverloadLikelihood,
            Confidence: overloadInference.Confidence,
            DurationSeconds: null,
            EscalationLevel: "Auto",
            Reversible: false,
            Reason: "Overload ended after stabilization.",
            Contributors: Array.Empty<string>(),
            InterventionId: interventionId,
            PolicyId: $"overload_stabilization:{policyVersion}",
            Domain: "state_transition",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            PreviousSystemStateJson: "{}",
            WhatChanged: "Overload state returned to normal after stabilization dwell was satisfied.",
            HowToUndo: "No action needed.",
            ReversalApplied: false,
            CooldownEndTimestampUtc: null,
            LifecycleState: "deactivated",
            WhyJsonOverride: whyJson,
            IdempotencyKey: deterministicIdempotencyKey);

        try
        {
            InsertInterventionEvent(connection, tx, ev);
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
        }
    }

    private static string ResolveOverloadBrightnessAction(
        BrightnessActionRuntimeState previousState,
        OverloadBrightnessTransitionExecution transitionExecution)
    {
        OverloadBrightnessActionOutcome outcome = transitionExecution.Outcome;
        BrightnessActionRuntimeState currentState = transitionExecution.RuntimeState;

        if (outcome.Succeeded
            && outcome.EnteringOverload
            && !previousState.Active
            && currentState.Active)
        {
            return "overload_brightness_reduced";
        }

        if (outcome.Succeeded
            && previousState.Active
            && !currentState.Active
            && string.Equals(currentState.ActionState, "restored", StringComparison.Ordinal))
        {
            return "overload_brightness_restored";
        }

        if (!outcome.Succeeded)
        {
            if (string.Equals(outcome.ErrorCode, "brightness_unsupported", StringComparison.Ordinal))
            {
                return previousState.LoggedUnsupported
                    ? string.Empty
                    : "brightness_unsupported";
            }

            return "brightness_control_failed";
        }

        return string.Empty;
    }

    private static string ResolveOverloadBrightnessWhatChanged(string action)
        => PortaActionSemantics.DescribeBrightnessAction(action);

    private static string ResolveOverloadBrightnessReason(string action, OverloadBrightnessActionOutcome outcome)
    {
        if (action == "overload_brightness_reduced")
        {
            return "session_started";
        }

        if (action == "overload_brightness_restored")
        {
            return "restore_completed";
        }

        if (action == "brightness_unsupported")
        {
            return "unsupported";
        }

        if (action == "brightness_control_failed")
        {
            if (outcome.RestoreAttempted || outcome.ExitingOverload)
            {
                return "restore_failed";
            }

            return "reduce_failed";
        }

        return "unknown";
    }

    private static string ResolveDeepWorkNotificationAction(DeepWorkActionOutcome outcome)
    {
        if (outcome.SuppressionAttempted && outcome.Succeeded)
        {
            return "deep_work_notifications_suppressed";
        }

        if (outcome.RestoreAttempted && outcome.Succeeded)
        {
            return "deep_work_notifications_restored";
        }

        if (!outcome.Succeeded && outcome.EnteringDeepWork)
        {
            if (string.Equals(outcome.ErrorCode, "snapshot_capture_failed", StringComparison.Ordinal)
                || string.Equals(outcome.ErrorCode, "snapshot_invalid", StringComparison.Ordinal))
            {
                return "notification_snapshot_failed";
            }

            return "notification_suppress_failed";
        }

        if (!outcome.Succeeded && outcome.ExitingDeepWork)
        {
            return "notification_restore_failed";
        }

        return string.Empty;
    }

    private static string ResolveDeepWorkNotificationWhatChanged(string action)
    {
        return action switch
        {
            "deep_work_notifications_suppressed" => "Notification suppression requested for deep work protection.",
            "deep_work_notifications_restored" => "Notification restore requested after deep work ended.",
            "notification_snapshot_failed" => "Failed to capture current notification state snapshot.",
            "notification_suppress_failed" => "Failed to suppress system notifications for deep work.",
            "notification_restore_failed" => "Failed to restore system notifications from persisted snapshot.",
            _ => "No notification suppression change."
        };
    }

    private static string ResolveDeepWorkNotificationOperation(string action, DeepWorkActionOutcome outcome)
    {
        if (action == "deep_work_notifications_suppressed")
        {
            return "apply";
        }

        if (action == "deep_work_notifications_restored")
        {
            return "restore";
        }

        if (action == "notification_snapshot_failed")
        {
            return "snapshot";
        }

        if (action == "notification_suppress_failed")
        {
            return outcome.SuppressionAttempted ? "apply" : "snapshot";
        }

        if (action == "notification_restore_failed")
        {
            return "restore";
        }

        return "unknown";
    }

    private static void PersistBreakPolicyRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx,
        long nowUtcMs,
        BreakSuggestionDecision decision,
        bool breakInserted,
        BreakFocusWindowSnapshot focusWindow,
        RuntimePolicyEnvelope? policyEnvelope = null,
        int dismissStreak = 0)
    {
        RuntimeBreakPolicySettings breakPolicy = policyEnvelope?.Settings.BreakPolicy
            ?? DefaultBreakPolicySettings();
        string? existingJson = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.BreakPolicy.RuntimeStateKey);
        BreakPolicyCooldownSnapshot existingCooldown = ParseBreakPolicyCooldownSnapshotJson(existingJson, nowUtcMs);
        long cooldownEndTsMs = existingCooldown.CooldownEndTsMs;
        long lastFiredTsMs = 0;
        string lastAction = string.Empty;
        long breakDueSinceUtcMs = 0;
        if (decision.ShouldEmit && breakInserted)
        {
            // Workstream 1.3: the cooldown written at fire time already honors the
            // dismissal streak (30m → 1h → 2h → 4h). A dismissal afterwards pushes it
            // out again from the dismissal moment (ApplyBreakBackoffAckSideEffects).
            cooldownEndTsMs = checked(nowUtcMs
                + (BreakBackoffPolicy.EffectiveCooldownSeconds(dismissStreak, breakPolicy.CooldownSeconds) * 1000L));
            lastFiredTsMs = nowUtcMs;
            lastAction = decision.Action;
        }

        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using JsonDocument existingDoc = JsonDocument.Parse(existingJson);
                JsonElement root = existingDoc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (lastFiredTsMs <= 0)
                    {
                        lastFiredTsMs = ReadInt64Property(root, "break_last_fired_utc_ms", "last_trigger_ts_ms");
                    }

                    if (string.IsNullOrWhiteSpace(lastAction))
                    {
                        lastAction = ReadStringProperty(root, "break_last_action");
                        if (string.IsNullOrWhiteSpace(lastAction))
                        {
                            lastAction = ReadStringProperty(root, "last_action");
                        }
                    }

                    breakDueSinceUtcMs = ReadInt64Property(root, "break_due_since_utc_ms");
                }
            }
            catch
            {
            }
        }

        // Track when the break first became due (focus threshold met, awaiting delivery boundary).
        // Reset whenever the break fires or is no longer pending.
        if (string.Equals(decision.ReasonCode, "awaiting_delivery_boundary", StringComparison.Ordinal))
        {
            if (breakDueSinceUtcMs <= 0)
            {
                breakDueSinceUtcMs = nowUtcMs;
            }
        }
        else
        {
            breakDueSinceUtcMs = 0;
        }

        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.BreakPolicy.ContractVersion,
            break_cooldown_until_utc_ms = cooldownEndTsMs,
            break_last_fired_utc_ms = lastFiredTsMs,
            break_last_action = lastAction,
            break_due_since_utc_ms = breakDueSinceUtcMs,
            short_break_effective_focus_s = focusWindow.ShortBreakWindowEffectiveFocusSeconds,
            long_break_effective_focus_s = focusWindow.LongBreakWindowEffectiveFocusSeconds,
            current_bucket_effective_focus_s = focusWindow.CurrentBucketEffectiveFocusSeconds,
            delivery_boundary_reached = focusWindow.DeliveryBoundaryReached,
            delivery_boundary_kind = focusWindow.DeliveryBoundaryKind ?? string.Empty,
            flow_state_active = focusWindow.FlowStateActive,
            high_pressure = focusWindow.HighPressure,
            pending_action = decision.Action,
            pending_break_kind = decision.BreakKind,
            pending_reason_code = decision.ReasonCode,
            pending_effective_focus_s = decision.EffectiveFocusSeconds,
            pending_effective_focus_window_s = decision.EffectiveFocusWindowSeconds,
            // Backward-compat fields for older readers during rollout.
            microbreak_effective_focus_s = focusWindow.ShortBreakWindowEffectiveFocusSeconds,
            short_break_effective_focus_s_legacy = focusWindow.LongBreakWindowEffectiveFocusSeconds,
            high_overload_pressure = focusWindow.HighPressure,
            cooldown_end_ts_ms = cooldownEndTsMs,
            last_trigger_ts_ms = lastFiredTsMs,
            last_reason_code = decision.ReasonCode
        });

        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.BreakPolicy.RuntimeStateKey,
            valueJson,
            Time.toIsoUtcMsString(nowUtcMs));
    }

    // ── Workstream 1: personal pressure baseline + rising-trend + acceptance backoff ──

    /// <summary>
    /// Builds the personalization inputs for one break decision: the user's own p80
    /// pressure threshold (or cold start when under 3 days of data) and whether pressure
    /// is currently declining. Cached hourly; the percentile never runs per bucket.
    /// </summary>
    private static BreakPersonalizationSnapshot ResolveBreakPersonalization(
        SqliteConnection connection,
        SqliteTransaction tx,
        long nowUtcMs)
    {
        PersonalPressureBaselineResult baseline = ResolvePersonalPressureBaseline(connection, tx, nowUtcMs);
        bool declining = ResolvePressureDeclining(connection, tx, nowUtcMs);
        return baseline.Ready
            ? new BreakPersonalizationSnapshot(baseline.P80, declining, "personal_p80")
            : new BreakPersonalizationSnapshot(null, declining, "cold_start_absolute");
    }

    private static PersonalPressureBaselineResult ResolvePersonalPressureBaseline(
        SqliteConnection connection,
        SqliteTransaction tx,
        long nowUtcMs)
    {
        // Hourly cache in runtime_state — the 7-day percentile scan must not run per bucket.
        string? cachedJson = ReadRuntimeStateValue(
            connection, tx, PortaExecutionConstants.BreakPolicy.PersonalBaselineRuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(cachedJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(cachedJson);
                JsonElement root = doc.RootElement;
                long computedAtMs = ReadInt64Property(root, "computed_at_utc_ms");
                if (computedAtMs > 0
                    && nowUtcMs - computedAtMs < PortaExecutionConstants.BreakPolicy.PersonalBaselineCacheSeconds * 1000L
                    && root.TryGetProperty("p80", out JsonElement p80El)
                    && p80El.TryGetDouble(out double cachedP80))
                {
                    return new PersonalPressureBaselineResult(
                        cachedP80,
                        (int)ReadInt64Property(root, "sample_count"),
                        (int)ReadInt64Property(root, "days_of_data"));
                }
            }
            catch
            {
            }
        }

        long cutoffMs = nowUtcMs - (PortaExecutionConstants.BreakPolicy.PersonalBaselineDays * 86_400_000L);
        var readings = new List<double>(4096);
        var days = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            // Active buckets only: reuse the pipeline's own idle/away classification
            // (attention_segment_kind == 'away' is the idle marker at segment level).
            // pressure_level has always been written into contributors_json, so this
            // works on pre-update history from day one.
            cmd.CommandText =
                """
                SELECT
                    substr(timestamp_utc, 1, 10),
                    CAST(json_extract(contributors_json, '$.pressure_level') AS REAL)
                FROM state_buckets
                WHERE timestamp_utc >= $cutoff
                  AND LOWER(COALESCE(json_extract(state_flags_json, '$.attention_segment_kind'), 'focus')) <> 'away'
                  AND json_extract(contributors_json, '$.pressure_level') IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$cutoff", Time.toIsoUtcMsString(cutoffMs));
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    days.Add(reader.GetString(0));
                    readings.Add(reader.GetDouble(1));
                }
            }
        }

        PersonalPressureBaselineResult result = PersonalPressureBaseline.Compute(readings, days.Count);
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.BreakPolicy.PersonalBaselineRuntimeStateKey,
            JsonSerializer.Serialize(new
            {
                schema_ver = 1,
                p80 = result.P80,
                sample_count = result.SampleCount,
                days_of_data = result.DaysOfData,
                computed_at_utc_ms = nowUtcMs
            }),
            Time.toIsoUtcMsString(nowUtcMs));
        return result;
    }

    /// <summary>
    /// Rising-trend requirement (1.2): mean pressure over the last 10 min vs the 10 min
    /// before it. Declining (recent &lt; 95% of prior) means the user is settling in — a
    /// short break would interrupt, not relieve. Sparse windows never suppress.
    /// </summary>
    private static bool ResolvePressureDeclining(
        SqliteConnection connection,
        SqliteTransaction tx,
        long nowUtcMs)
    {
        long windowMs = PortaExecutionConstants.BreakPolicy.TrendWindowSeconds * 1000L;
        (double recentMean, int recentCount) = ReadMeanPressure(connection, tx, nowUtcMs - windowMs, nowUtcMs);
        (double priorMean, int priorCount) = ReadMeanPressure(connection, tx, nowUtcMs - (2 * windowMs), nowUtcMs - windowMs);

        // Under half a minute of samples on either side there is no trend to speak of.
        const int minSamples = 6;
        if (recentCount < minSamples || priorCount < minSamples || priorMean <= 0d)
        {
            return false;
        }

        return recentMean < priorMean * PortaExecutionConstants.BreakPolicy.TrendToleranceRatio;
    }

    private static (double Mean, int Count) ReadMeanPressure(
        SqliteConnection connection,
        SqliteTransaction tx,
        long fromUtcMs,
        long toUtcMs)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT
                AVG(CAST(json_extract(contributors_json, '$.pressure_level') AS REAL)),
                COUNT(*)
            FROM state_buckets
            WHERE timestamp_utc >= $from
              AND timestamp_utc < $to
              AND json_extract(contributors_json, '$.pressure_level') IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("$from", Time.toIsoUtcMsString(fromUtcMs));
        cmd.Parameters.AddWithValue("$to", Time.toIsoUtcMsString(toUtcMs));
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return (0d, 0);
        }

        return (reader.GetDouble(0), reader.GetInt32(1));
    }

    private static (int DismissStreak, string BackoffDay) ReadBreakBackoffState(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        string? json = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.BreakPolicy.BackoffRuntimeStateKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return (0, string.Empty);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int streak = (int)Math.Clamp(ReadInt64Property(root, "dismiss_streak"), 0, 16);
            string day = ReadStringProperty(root, "backoff_day");
            return (streak, day);
        }
        catch
        {
            return (0, string.Empty);
        }
    }

    private static void WriteBreakBackoffState(
        SqliteConnection connection,
        SqliteTransaction tx,
        int dismissStreak,
        string backoffDay,
        string changeReason,
        long nowUtcMs)
    {
        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.BreakPolicy.BackoffRuntimeStateKey,
            JsonSerializer.Serialize(new
            {
                schema_ver = 1,
                dismiss_streak = Math.Clamp(dismissStreak, 0, 16),
                backoff_day = backoffDay ?? string.Empty,
                last_change_reason = changeReason,
                updated_utc_ms = nowUtcMs
            }),
            Time.toIsoUtcMsString(nowUtcMs));
    }

    /// <summary>
    /// Per-bucket backoff maintenance: morning reset (first finalized bucket after 5:00
    /// local starts a new day — yesterday's refusals never carry over) and natural-rest
    /// reset (the user took a real pause on their own; the debt is paid). Returns the
    /// streak in effect for this bucket's break decision.
    /// </summary>
    internal static int UpdateBreakBackoffForBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        long nowUtcMs,
        DerivedSessionMetricsResult derivedSession)
    {
        (int streak, string day) = ReadBreakBackoffState(connection, tx);
        string currentDay = BreakBackoffPolicy.ResolveBackoffDay(
            DateTimeOffset.FromUnixTimeMilliseconds(nowUtcMs).ToLocalTime());
        if (!string.Equals(day, currentDay, StringComparison.Ordinal))
        {
            WriteBreakBackoffState(connection, tx, 0, currentDay, "morning_reset", nowUtcMs);
            return 0;
        }

        bool naturalRest = derivedSession.SaIdlePauseSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds
            || string.Equals(derivedSession.SaFreezeReason, "long_break", StringComparison.Ordinal);
        if (naturalRest && streak > 0)
        {
            WriteBreakBackoffState(connection, tx, 0, currentDay, "natural_rest", nowUtcMs);
            return 0;
        }

        return streak;
    }

    /// <summary>
    /// Outcome wiring (1.3): a dismissed/skipped break increments the streak AND pushes
    /// the cooldown out from the moment of dismissal; an accepted or completed break
    /// resets it. Snooze is neutral — "later" is engagement, not refusal. An unanswered
    /// nudge produces no ack today, so "ignored" has no hook yet; dismiss/skip covers
    /// the practical case.
    /// </summary>
    internal static void ApplyBreakBackoffAckSideEffects(
        SqliteConnection connection,
        SqliteTransaction tx,
        string action,
        string normalizedResult,
        DateTimeOffset appliedAtUtc,
        int baseCooldownSeconds)
    {
        if (!PortaBreakSemantics.IsBreakSuggestionAction(action))
        {
            return;
        }

        long ackUtcMs = appliedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        string day = BreakBackoffPolicy.ResolveBackoffDay(appliedAtUtc.ToLocalTime());
        (int streak, string _) = ReadBreakBackoffState(connection, tx);
        switch (normalizedResult)
        {
            case "accepted":
            case "overlay_completed":
                if (streak != 0)
                {
                    WriteBreakBackoffState(connection, tx, 0, day, "break_accepted", ackUtcMs);
                }

                break;
            case "dismissed":
            case "skipped":
                int nextStreak = Math.Min(streak + 1, 16);
                WriteBreakBackoffState(connection, tx, nextStreak, day, "break_dismissed", ackUtcMs);
                ExtendBreakCooldownAfterDismissal(connection, tx, ackUtcMs, nextStreak, baseCooldownSeconds);
                break;
        }
    }

    // After a dismissal the next ask must be effectiveCooldown(streak) away from the
    // DISMISSAL, not from when the nudge fired — otherwise the fire-time 30m window
    // would let the next nudge arrive almost immediately after an annoyed "skip".
    private static void ExtendBreakCooldownAfterDismissal(
        SqliteConnection connection,
        SqliteTransaction tx,
        long ackUtcMs,
        int dismissStreak,
        int baseCooldownSeconds)
    {
        long newEndMs = ackUtcMs
            + (BreakBackoffPolicy.EffectiveCooldownSeconds(dismissStreak, baseCooldownSeconds) * 1000L);
        string? json = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.BreakPolicy.RuntimeStateKey);
        string updatedJson;
        try
        {
            System.Text.Json.Nodes.JsonNode? node = string.IsNullOrWhiteSpace(json)
                ? null
                : System.Text.Json.Nodes.JsonNode.Parse(json);
            System.Text.Json.Nodes.JsonObject obj = node as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject
                {
                    ["schema_ver"] = 1,
                    ["contract_ver"] = PortaExecutionConstants.BreakPolicy.ContractVersion
                };
            long existingEndMs = 0;
            if (obj.TryGetPropertyValue("break_cooldown_until_utc_ms", out System.Text.Json.Nodes.JsonNode? endNode)
                && endNode is not null)
            {
                _ = long.TryParse(endNode.ToString(), out existingEndMs);
            }

            obj["break_cooldown_until_utc_ms"] = Math.Max(existingEndMs, newEndMs);
            obj["cooldown_end_ts_ms"] = Math.Max(existingEndMs, newEndMs);
            updatedJson = obj.ToJsonString();
        }
        catch
        {
            updatedJson = JsonSerializer.Serialize(new
            {
                schema_ver = 1,
                contract_ver = PortaExecutionConstants.BreakPolicy.ContractVersion,
                break_cooldown_until_utc_ms = newEndMs,
                cooldown_end_ts_ms = newEndMs
            });
        }

        UpsertRuntimeStateValue(
            connection,
            tx,
            PortaExecutionConstants.BreakPolicy.RuntimeStateKey,
            updatedJson,
            Time.toIsoUtcMsString(ackUtcMs));
    }

    internal static BreakFocusWindowSnapshot ReadBreakFocusWindowSnapshot(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow inputBucket,
        DerivedSessionMetricsResult derivedSession,
        OverloadStabilizationResult overloadStabilization,
        OverloadInferenceResult overloadInference,
        RuntimeBreakPolicySettings? policy = null)
    {
        RuntimeBreakPolicySettings settings = policy ?? DefaultBreakPolicySettings();
        int shortBreakBuckets = Math.Max(1, settings.ShortBreakWindowSeconds / Time.bucketSeconds);
        int longBreakBuckets = Math.Max(1, settings.LongBreakWindowSeconds / Time.bucketSeconds);
        long earliestBucketSeq = Math.Max(0, inputBucket.BucketSeq - longBreakBuckets + 1);
        long earliestShortBreakBucketSeq = Math.Max(0, inputBucket.BucketSeq - shortBreakBuckets + 1);
        long earliestTimestampUtcMs = Time.bucketStartEnd(earliestBucketSeq).startUtcMs;
        long earliestShortBreakTimestampUtcMs = Time.bucketStartEnd(earliestShortBreakBucketSeq).startUtcMs;
        long currentTimestampUtcMs = Time.bucketStartEnd(inputBucket.BucketSeq).startUtcMs;
        string earliestTimestampUtc = Time.toIsoUtcMsString(earliestTimestampUtcMs);
        string currentTimestampUtc = Time.toIsoUtcMsString(currentTimestampUtcMs);
        double currentBucketEffectiveFocusSeconds = ResolveCurrentBucketEffectiveFocusSeconds(derivedSession);
        double shortBreakWindowEffectiveFocusSeconds = currentBucketEffectiveFocusSeconds;
        double longBreakWindowEffectiveFocusSeconds = currentBucketEffectiveFocusSeconds;
        long segmentCursorUtcMs = Time.bucketStartEnd(inputBucket.BucketSeq).endUtcMs;

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT
                timestamp_utc,
                state_flags_json
            FROM state_buckets
            WHERE timestamp_utc >= $min_timestamp_utc
              AND timestamp_utc < $current_timestamp_utc
            ORDER BY timestamp_utc DESC
            """;
        cmd.Parameters.AddWithValue("$min_timestamp_utc", earliestTimestampUtc);
        cmd.Parameters.AddWithValue("$current_timestamp_utc", currentTimestampUtc);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string timestampUtc = ReadStringOrFallback(reader, 0, string.Empty);
            if (string.IsNullOrWhiteSpace(timestampUtc))
            {
                break;
            }

            long timestampUtcMs = Time.parseIsoUtcMs(timestampUtc);
            if ((segmentCursorUtcMs - timestampUtcMs) > (long)(PortaExecutionConstants.DerivedSessionMetrics.GapResetSeconds * 1000d))
            {
                break;
            }

            string stateFlagsJson = ReadStringOrFallback(reader, 1, string.Empty);
            if (!TryResolveStoredBreakActiveFocusSeconds(stateFlagsJson, out double rowEffectiveFocusSeconds))
            {
                // Non-active bucket (pause/decay/reset or missing state): contributes 0 focus seconds
                // but must not abort the scan — genuine data gaps are already caught by the gap check above.
                segmentCursorUtcMs = timestampUtcMs;
                continue;
            }

            longBreakWindowEffectiveFocusSeconds += rowEffectiveFocusSeconds;
            if (timestampUtcMs >= earliestShortBreakTimestampUtcMs)
            {
                shortBreakWindowEffectiveFocusSeconds += rowEffectiveFocusSeconds;
            }

            segmentCursorUtcMs = timestampUtcMs;
        }

        // Flow state: user has been in uninterrupted task continuity for FlowStateWindowSeconds
        // with no app switch in the current bucket. Suppresses typing_quiet delivery boundary.
        bool flowStateActive =
            !string.Equals(derivedSession.ContinuityState, "reset", StringComparison.Ordinal)
            && derivedSession.TaskContinuitySeconds >= settings.FlowStateWindowSeconds
            && inputBucket.FocusSwitchCount == 0;

        // Read persisted break_due_since_utc_ms so the hard-timeout can be evaluated.
        long breakDueSinceUtcMs = 0;
        string? existingBreakRuntimeJson = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.BreakPolicy.RuntimeStateKey);
        if (!string.IsNullOrWhiteSpace(existingBreakRuntimeJson))
        {
            try
            {
                using JsonDocument bDoc = JsonDocument.Parse(existingBreakRuntimeJson);
                if (bDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    breakDueSinceUtcMs = ReadInt64Property(bDoc.RootElement, "break_due_since_utc_ms");
                }
            }
            catch
            {
            }
        }

        return new BreakFocusWindowSnapshot(
            CurrentBucketEffectiveFocusSeconds: currentBucketEffectiveFocusSeconds,
            ShortBreakWindowEffectiveFocusSeconds: shortBreakWindowEffectiveFocusSeconds,
            LongBreakWindowEffectiveFocusSeconds: longBreakWindowEffectiveFocusSeconds,
            DeliveryBoundaryReached: IsBreakDeliveryBoundary(inputBucket, derivedSession, flowStateActive, settings),
            DeliveryBoundaryKind: ResolveBreakDeliveryBoundaryKind(inputBucket, derivedSession, flowStateActive, settings),
            HighPressure: overloadStabilization.OverloadActive
                || overloadInference.PressureLevel >= settings.HighPressureLevel,
            FlowStateActive: flowStateActive,
            BreakDueSinceUtcMs: breakDueSinceUtcMs);
    }

    private static double ResolveCurrentBucketEffectiveFocusSeconds(DerivedSessionMetricsResult derivedSession)
    {
        return IsBreakActiveContinuity(
            derivedSession.ContinuityState,
            derivedSession.SaFreezeReason,
            derivedSession.TaskContinuityScore)
            ? ResolveEffectiveFocusSeconds(derivedSession.TaskContinuityScore)
            : 0d;
    }

    private static double ResolveEffectiveFocusSeconds(double continuityScore)
    {
        return Math.Max(0d, Time.bucketSeconds * Math.Clamp(
            double.IsFinite(continuityScore) ? continuityScore : 0d,
            0d,
            1d));
    }

    private static bool TryResolveStoredBreakActiveFocusSeconds(
        string stateFlagsJson,
        out double effectiveFocusSeconds)
    {
        effectiveFocusSeconds = 0d;
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            double continuityScore = TryReadDoubleProperty(root, "behavior_continuity_score", out double behaviorScore)
                ? behaviorScore
                : TryReadDoubleProperty(root, "task_continuity_score", out double taskScore)
                    ? taskScore
                    : 0d;
            string continuityState = TryReadStringProperty(root, "continuity_state", out string? continuityStateParsed)
                ? continuityStateParsed ?? string.Empty
                : string.Empty;
            string reasonCode = TryReadStringProperty(root, "sa_reset_reason", out string? reasonParsed)
                ? reasonParsed ?? string.Empty
                : string.Empty;

            if (!IsBreakActiveContinuity(continuityState, reasonCode, continuityScore))
            {
                return false;
            }

            effectiveFocusSeconds = ResolveEffectiveFocusSeconds(continuityScore);
            return effectiveFocusSeconds > 0d;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBreakActiveContinuity(
        string continuityState,
        string reasonCode,
        double continuityScore)
    {
        if (string.Equals(reasonCode, "long_break", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(continuityState?.Trim(), "keep", StringComparison.OrdinalIgnoreCase)
            && double.IsFinite(continuityScore)
            && continuityScore > 0d;
    }

    private static bool IsBreakDeliveryBoundary(
        InputBucketRow inputBucket,
        DerivedSessionMetricsResult derivedSession,
        bool flowStateActive,
        RuntimeBreakPolicySettings? policy = null)
    {
        return !string.IsNullOrWhiteSpace(ResolveBreakDeliveryBoundaryKind(inputBucket, derivedSession, flowStateActive, policy));
    }

    private static string ResolveBreakDeliveryBoundaryKind(
        InputBucketRow inputBucket,
        DerivedSessionMetricsResult derivedSession,
        bool flowStateActive,
        RuntimeBreakPolicySettings? policy = null)
    {
        RuntimeBreakPolicySettings settings = policy ?? DefaultBreakPolicySettings();
        if (HasShortIdleBoundaryObserved(inputBucket.ContributorsJson))
        {
            return "short_idle";
        }

        if (inputBucket.IdleSeconds >= settings.DeliveryBoundaryIdleMinSeconds
            && inputBucket.IdleSeconds <= settings.DeliveryBoundaryIdleMaxSeconds)
        {
            return "short_idle";
        }

        if (inputBucket.IdleMicroIdleSeconds >= settings.DeliveryBoundaryIdleMinSeconds)
        {
            return "micro_idle";
        }

        if (inputBucket.FocusSwitchCount > 0
            && !string.Equals(derivedSession.ContinuityState, "reset", StringComparison.Ordinal))
        {
            return "post_switch_settle";
        }

        // typing_quiet suppressed during flow state — a brief keyboard lull mid-deep-work
        // is not a natural pause and would interrupt focus.
        if (!flowStateActive
            && inputBucket.TypingBurstIntensity <= settings.DeliveryBoundaryTypingQuietMax
            && inputBucket.KeyboardBurstRate <= settings.DeliveryBoundaryKeyboardBurstQuietMax)
        {
            return "typing_quiet";
        }

        return string.Empty;
    }

    private static bool HasShortIdleBoundaryObserved(string contributorsJson)
    {
        if (string.IsNullOrWhiteSpace(contributorsJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(contributorsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("idle", out JsonElement idle)
                || idle.ValueKind != JsonValueKind.Object
                || !idle.TryGetProperty("short_idle_boundary_observed", out JsonElement boundary))
            {
                return false;
            }

            return boundary.ValueKind == JsonValueKind.True
                || (boundary.ValueKind == JsonValueKind.String
                    && bool.TryParse(boundary.GetString(), out bool parsed)
                    && parsed);
        }
        catch
        {
            return false;
        }
    }

    private static RuntimeBreakPolicySettings DefaultBreakPolicySettings()
    {
        return new RuntimeBreakPolicySettings(
            PortaExecutionConstants.BreakPolicy.ShortBreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowPressure,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsHighPressure,
            PortaExecutionConstants.BreakPolicy.LongBreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.LongBreakEffectiveFocusSecondsLowPressure,
            PortaExecutionConstants.BreakPolicy.LongBreakEffectiveFocusSecondsHighPressure,
            PortaExecutionConstants.BreakPolicy.CooldownSeconds,
            PortaExecutionConstants.BreakPolicy.ConfidenceMin,
            PortaExecutionConstants.BreakPolicy.HighPressureLevel,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMinSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMaxSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryTypingQuietMax,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryKeyboardBurstQuietMax,
            PortaExecutionConstants.BreakPolicy.FlowStateWindowSeconds,
            PortaExecutionConstants.BreakPolicy.HardTimeoutSeconds);
    }

    private static RuntimeOverloadStabilizationSettings DefaultOverloadStabilizationSettings()
    {
        return new RuntimeOverloadStabilizationSettings(
            PortaExecutionConstants.OverloadStabilization.ThresholdOn,
            PortaExecutionConstants.OverloadStabilization.ThresholdOff,
            PortaExecutionConstants.OverloadStabilization.DwellOnSeconds,
            PortaExecutionConstants.OverloadStabilization.DwellOffSeconds,
            PortaExecutionConstants.OverloadStabilization.ConfidenceMin,
            PortaExecutionConstants.OverloadStabilization.GapAllowSeconds,
            PortaExecutionConstants.OverloadStabilization.LowQualityPauseThreshold,
            PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);
    }

    private static BreakPolicyCooldownSnapshot ReadBreakPolicyCooldownSnapshot(
        SqliteConnection connection,
        SqliteTransaction tx,
        long nowUtcMs)
    {
        string? json = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.BreakPolicy.RuntimeStateKey);
        return ParseBreakPolicyCooldownSnapshotJson(json, nowUtcMs);
    }

    internal static BreakPolicyCooldownSnapshot ParseBreakPolicyCooldownSnapshotJson(
        string? json,
        long nowUtcMs)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BreakPolicyCooldownSnapshot(
                CooldownExpired: true,
                CooldownEndTsMs: 0,
                CooldownRemainingMs: 0);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new BreakPolicyCooldownSnapshot(true, 0, 0);
            }

            // Step 13C contract fields (with backward-compatible fallbacks):
            long cooldownEndTsMs = ReadInt64Property(root, "break_cooldown_until_utc_ms", "cooldown_end_ts_ms");
            if (cooldownEndTsMs <= 0
                && root.TryGetProperty("break_cooldown_until_utc", out JsonElement cooldownEndUtc)
                && cooldownEndUtc.ValueKind == JsonValueKind.String)
            {
                string? raw = cooldownEndUtc.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        cooldownEndTsMs = Time.parseIsoUtcMs(raw);
                    }
                    catch
                    {
                        cooldownEndTsMs = 0;
                    }
                }
            }
            if (cooldownEndTsMs <= 0
                && root.TryGetProperty("cooldown_end_utc", out JsonElement legacyCooldownEndUtc)
                && legacyCooldownEndUtc.ValueKind == JsonValueKind.String)
            {
                string? raw = legacyCooldownEndUtc.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        cooldownEndTsMs = Time.parseIsoUtcMs(raw);
                    }
                    catch
                    {
                        cooldownEndTsMs = 0;
                    }
                }
            }

            bool expired = cooldownEndTsMs <= 0 || nowUtcMs >= cooldownEndTsMs;
            long remainingMs = expired ? 0 : Math.Max(0, cooldownEndTsMs - nowUtcMs);
            return new BreakPolicyCooldownSnapshot(
                CooldownExpired: expired,
                CooldownEndTsMs: Math.Max(0, cooldownEndTsMs),
                CooldownRemainingMs: remainingMs);
        }
        catch
        {
            return new BreakPolicyCooldownSnapshot(true, 0, 0);
        }
    }

    private void InsertAutomaticDeactivationEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        AgentBrainSnapshot snapshot,
        string reason)
    {
        if (_activeIntervention == null)
        {
            return;
        }

        ActiveInterventionState active = _activeIntervention;
        _activeIntervention = null;

        (string triggerState, double triggerValue) = ResolveTrigger(snapshot);
        int? durationSeconds = null;
        if (snapshot.TimestampUtc >= active.StartTimestampUtc)
        {
            durationSeconds = (int)Math.Max(0, (snapshot.TimestampUtc - active.StartTimestampUtc).TotalSeconds);
        }
        string whatChanged = ResolveWhatChanged(active.Action, lifecycleState: "deactivated");
        string howToUndo = ResolveHowToUndo(active.Action, reversible: true, lifecycleState: "deactivated");
        string deactivationReason = $"{reason}; trigger={triggerState}={triggerValue:F2}; confidence={snapshot.Confidence:F2}";

        var ev = new AgentControlEvent(
            TimestampUtc: snapshot.TimestampUtc,
            Action: active.Action,
            TriggerState: triggerState,
            TriggerValue: triggerValue,
            Confidence: snapshot.Confidence,
            DurationSeconds: durationSeconds,
            EscalationLevel: "Inform",
            Reversible: true,
            Reason: deactivationReason,
            Contributors: ["recovery", "auto_reversal"],
            InterventionId: active.InterventionId,
            PolicyId: active.PolicyId,
            Domain: active.Domain,
            PlannedEndTimestampUtc: active.PlannedEndTimestampUtc,
            ActualEndTimestampUtc: snapshot.TimestampUtc,
            PreviousSystemStateJson: JsonSerializer.Serialize(new
            {
                active_action = active.Action,
                active_escalation = active.EscalationLevel
            }),
            WhatChanged: whatChanged,
            HowToUndo: howToUndo,
            ReversalApplied: true,
            CooldownEndTimestampUtc: snapshot.CooldownEndTimestampUtc ?? active.CooldownEndTimestampUtc,
            LifecycleState: "deactivated");

        InsertInterventionEvent(connection, tx, ev);
        InsertAutomaticSystemChange(connection, tx, ev);
    }

    private static bool IsNonInterventionAction(string action)
    {
        return string.IsNullOrWhiteSpace(action)
            || string.Equals(action, "none", StringComparison.Ordinal)
            || string.Equals(action, "unknown", StringComparison.Ordinal)
            || string.Equals(action, "monitor_only", StringComparison.Ordinal)
            || string.Equals(action, "paused_by_user", StringComparison.Ordinal)
            || string.Equals(action, "consent_required", StringComparison.Ordinal)
            || string.Equals(action, "maintain_focus", StringComparison.Ordinal)
            || string.Equals(action, "starting", StringComparison.Ordinal);
    }

    private static bool IsPolicyDrivenAction(string action)
    {
        return !IsNonInterventionAction(action)
            && !string.IsNullOrWhiteSpace(action);
    }

    private static bool IsInformAction(string action)
    {
        return string.Equals(action, "inform_overload_risk", StringComparison.Ordinal)
            || string.Equals(action, "inform_focus_protection", StringComparison.Ordinal)
            || string.Equals(action, "inform_fatigue_prevention", StringComparison.Ordinal);
    }

    private static bool IsSuggestionAction(string action)
    {
        return PortaBreakSemantics.IsBreakSuggestionAction(action)
            || string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal);
    }

    private static bool IsSystemAction(string action)
    {
        return string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal)
            || string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal)
            || string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal)
            || string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal);
    }

    private static string MapDomainToSuggestion(string domain, string fallbackAction)
    {
        return NormalizeDomain(domain) switch
        {
            "focus" => "focus_lock_in_suggestion",
            "fatigue" => PortaBreakSemantics.ShortBreakKind,
            "overload" => PortaBreakSemantics.LongBreakKind,
            _ when string.Equals(fallbackAction, "focus_assist_enabled", StringComparison.Ordinal) => "focus_lock_in_suggestion",
            _ when PortaBreakSemantics.IsBreakSuggestionAction(fallbackAction)
                => PortaBreakSemantics.ResolveBreakKind(fallbackAction) ?? PortaBreakSemantics.ShortBreakKind,
            _ => PortaBreakSemantics.LongBreakKind
        };
    }

    private static bool AllowsCalibrationSuggestionFallback(string domain, string fallbackAction)
    {
        string normalizedDomain = NormalizeDomain(domain);
        return string.Equals(normalizedDomain, "focus", StringComparison.Ordinal)
            || string.Equals(fallbackAction, "focus_assist_enabled", StringComparison.Ordinal);
    }

    private static string MapDomainToInform(string domain, string fallbackAction)
    {
        return NormalizeDomain(domain) switch
        {
            "focus" => "inform_focus_protection",
            "fatigue" => "inform_fatigue_prevention",
            "overload" => "inform_overload_risk",
            _ when string.Equals(fallbackAction, "focus_lock_in_suggestion", StringComparison.Ordinal)
                || string.Equals(fallbackAction, "focus_assist_enabled", StringComparison.Ordinal) => "inform_focus_protection",
            _ when (PortaBreakSemantics.IsBreakSuggestionAction(fallbackAction)
                    && string.Equals(PortaBreakSemantics.ResolveBreakKind(fallbackAction), PortaBreakSemantics.ShortBreakKind, StringComparison.Ordinal))
                || string.Equals(fallbackAction, "fatigue_relief_enabled", StringComparison.Ordinal) => "inform_fatigue_prevention",
            _ => "inform_overload_risk"
        };
    }

    private static string ResolveDomainForCalibrationAction(string action, string fallbackDomain)
    {
        if (!string.IsNullOrWhiteSpace(fallbackDomain))
        {
            return NormalizeDomain(fallbackDomain);
        }

        if (string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal)
            || string.Equals(action, "inform_focus_protection", StringComparison.Ordinal))
        {
            return "focus";
        }

        if (PortaBreakSemantics.IsBreakSuggestionAction(action)
            && string.Equals(PortaBreakSemantics.ResolveBreakKind(action), PortaBreakSemantics.ShortBreakKind, StringComparison.Ordinal))
        {
            return "fatigue";
        }

        if (string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal)
            || string.Equals(action, "inform_fatigue_prevention", StringComparison.Ordinal))
        {
            return "fatigue";
        }

        if (PortaBreakSemantics.IsBreakSuggestionAction(action)
            || string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal)
            || string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal)
            || string.Equals(action, "inform_overload_risk", StringComparison.Ordinal))
        {
            return "overload";
        }

        return "control";
    }

    private static string NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return string.Empty;
        }

        string normalized = domain.Trim().ToLowerInvariant();
        return normalized is "focus" or "fatigue" or "overload" ? normalized : string.Empty;
    }

    private static (string triggerState, double triggerValue) ResolveTrigger(AgentBrainSnapshot snapshot)
    {
        if (string.Equals(snapshot.LastAction, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return ("overload", snapshot.OverloadLikelihood);
        }

        if (string.Equals(snapshot.LastAction, "focus_assist_enabled", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        if (string.Equals(snapshot.LastAction, "fatigue_relief_enabled", StringComparison.Ordinal))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

        if (PortaBreakSemantics.IsBreakSuggestionAction(snapshot.LastAction))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

        if (string.Equals(snapshot.LastAction, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        if (string.Equals(snapshot.LastAction, "temporary_app_restriction_enabled", StringComparison.Ordinal))
        {
            if (string.Equals(snapshot.Domain, "focus", StringComparison.OrdinalIgnoreCase))
            {
                return ("focus", snapshot.FocusStrength);
            }

            if (string.Equals(snapshot.Domain, "fatigue", StringComparison.OrdinalIgnoreCase))
            {
                return ("fatigue", snapshot.FatigueProbability);
            }

            return ("overload", snapshot.OverloadLikelihood);
        }

        if (string.Equals(snapshot.LastAction, "inform_overload_risk", StringComparison.Ordinal))
        {
            return ("overload", snapshot.OverloadLikelihood);
        }

        if (string.Equals(snapshot.LastAction, "inform_focus_protection", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        // stretch_break_suggestion kept for historical events before Step 12 migration.
        // Canonical short_break is already handled by the IsBreakSuggestionAction check above.

        if (string.Equals(snapshot.LastAction, "inform_fatigue_prevention", StringComparison.Ordinal))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

        if (string.Equals(snapshot.LastAction, "maintain_focus", StringComparison.Ordinal))
        {
            return ("focus", snapshot.FocusStrength);
        }

        double dominant = Math.Max(snapshot.FocusStrength, Math.Max(snapshot.OverloadLikelihood, snapshot.FatigueProbability));
        string state = dominant == snapshot.FocusStrength
            ? "focus"
            : dominant == snapshot.OverloadLikelihood
                ? "overload"
                : "fatigue";

        return (state, dominant);
    }

    private static string ResolveEscalationLevel(string action)
    {
        if (string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal))
        {
            return "Restrict";
        }

        if (string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return "Nudge";
        }

        if (string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal))
        {
            return "Nudge";
        }

        if (string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal))
        {
            return "Nudge";
        }

        if (PortaBreakSemantics.IsBreakSuggestionAction(action)
            || string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return "Suggest";
        }

        return "Inform";
    }

    private static bool IsReversibleAction(string action)
    {
        return string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal)
            || string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal)
            || string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal)
            || string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal);
    }

    private static int? ResolveDurationSeconds(string action)
    {
        if (string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal))
        {
            return 180;
        }

        if (string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal))
        {
            return 600;
        }

        if (string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal))
        {
            return 600;
        }

        if (string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal))
        {
            return 600;
        }

        if (PortaBreakSemantics.IsBreakSuggestionAction(action)
            || string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return 300;
        }

        if (string.Equals(action, "inform_overload_risk", StringComparison.Ordinal))
        {
            return 60;
        }

        if (string.Equals(action, "inform_focus_protection", StringComparison.Ordinal))
        {
            return 60;
        }

        if (string.Equals(action, "inform_fatigue_prevention", StringComparison.Ordinal))
        {
            return 60;
        }

        return null;
    }

    private static string ResolveAutomaticReason(
        string action,
        string triggerState,
        double triggerValue,
        double confidence,
        bool reversible)
    {
        string reversibility = reversible ? "reversible" : "not_reversible";
        return $"{action} triggered by {triggerState}={triggerValue:F2}, confidence={confidence:F2}, {reversibility}.";
    }

    private static string ResolveWhatChanged(string action, string lifecycleState)
    {
        if (string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase))
        {
            if (PortaActionSemantics.IsPolicyIntentAction(action))
            {
                return PortaActionSemantics.DescribePolicyIntent(action, lifecycleState);
            }

            return "Intervention ended; prior system state restoration attempted.";
        }

        if (PortaActionSemantics.IsPolicyIntentAction(action))
        {
            return PortaActionSemantics.DescribePolicyIntent(action, lifecycleState);
        }

        string? breakKind = PortaBreakSemantics.ResolveBreakKind(action);
        if (breakKind == PortaBreakSemantics.LongBreakKind) return "Long break suggestion displayed to user.";
        if (breakKind == PortaBreakSemantics.ShortBreakKind) return "Short break suggestion displayed to user.";
        return action switch
        {
            "focus_lock_in_suggestion" => "Focus lock-in suggestion displayed to user.",
            "inform_overload_risk" => "Overload risk logged for transparency; no direct system setting changed.",
            "inform_focus_protection" => "Focus protection signal logged for transparency; no direct system setting changed.",
            "inform_fatigue_prevention" => "Fatigue prevention signal logged for transparency; no direct system setting changed.",
            _ => $"Action {action} recorded."
        };
    }

    private static string ResolveHowToUndo(string action, bool reversible, string lifecycleState)
    {
        if (string.Equals(lifecycleState, "deactivated", StringComparison.OrdinalIgnoreCase))
        {
            return "No action required. PORTA already attempted automatic reversal.";
        }

        if (PortaBreakSemantics.IsBreakSuggestionAction(action)
            || string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return "Dismiss the suggestion overlay or wait for auto-expiry.";
        }

        return action switch
        {
            "inform_overload_risk" or "inform_focus_protection" or "inform_fatigue_prevention"
                => "No undo required; this action is informational only.",
            _ when reversible
                => "Use Undo Action in Interventions, or disable the category in Settings > Interventions.",
            _ => "No direct undo path is available for this action."
        };
    }

    private static void InsertAutomaticSystemChange(SqliteConnection connection, SqliteTransaction tx, AgentControlEvent controlEvent)
    {
        if (!IsReversibleAction(controlEvent.Action)
            && !string.Equals(controlEvent.Action, "break_suggestion", StringComparison.Ordinal)
            && !string.Equals(controlEvent.Action, "focus_lock_in_suggestion", StringComparison.Ordinal)
            && !string.Equals(controlEvent.Action, "stretch_break_suggestion", StringComparison.Ordinal))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO system_changes (
                timestamp_utc,
                change_type,
                previous_value_json,
                new_value_json,
                undo_info,
                reason
            ) VALUES (
                $ts, $type, $previous, $new, $undo, $reason
            );
            """;
        string eventTsUtc = Time.toIsoUtcMsString(controlEvent.TimestampUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$ts", eventTsUtc);
        cmd.Parameters.AddWithValue("$type", "regulation");
        cmd.Parameters.AddWithValue("$previous", JsonSerializer.Serialize(new
        {
            action = "none"
        }));
        cmd.Parameters.AddWithValue("$new", JsonSerializer.Serialize(new
        {
            intervention_id = controlEvent.InterventionId,
            action = controlEvent.Action,
            policy_id = controlEvent.PolicyId,
            domain = controlEvent.Domain,
            escalation = controlEvent.EscalationLevel,
            reversible = controlEvent.Reversible,
            lifecycle_state = controlEvent.LifecycleState,
            what_changed = controlEvent.WhatChanged
        }));
        cmd.Parameters.AddWithValue("$undo", string.IsNullOrWhiteSpace(controlEvent.HowToUndo)
            ? (controlEvent.Reversible ? "Disable category in settings/interventions." : "No-op")
            : controlEvent.HowToUndo);
        cmd.Parameters.AddWithValue("$reason", controlEvent.Reason);
        cmd.ExecuteNonQuery();
    }

    private static void InsertInterventionEvent(SqliteConnection connection, SqliteTransaction tx, AgentControlEvent controlEvent)
    {
        string commandType = ResolveInterventionCommandType(controlEvent.Action);
        string payloadJson = BuildInterventionPayloadJson(controlEvent, commandType);
        string executionState = string.IsNullOrWhiteSpace(commandType) ? "decision_only" : "decided";
        string decidedAtUtc = Time.toIsoUtcMsString(controlEvent.TimestampUtc.ToUnixTimeMilliseconds());
        bool queueDelivery = string.Equals(controlEvent.LifecycleState, "activated", StringComparison.Ordinal);

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO events (
                timestamp_utc,
                ts_utc,
                action,
                duration_seconds,
                why,
                why_json,
                escalation,
                reversible,
                trigger_state,
                trigger_value,
                confidence,
                contributors_json,
                state_flags_json,
                idempotency_key,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state
            ) VALUES (
                $ts, $ts, $action, $duration, $reason, $why_json, $escalation, $reversible, $trigger_state, $trigger_value, $confidence, $contributors, $state_flags_json, $idempotency_key,
                $intervention_id, $policy_id, $domain, $planned_end, $actual_end, $previous_state, $what_changed, $how_to_undo, $reversal_applied, $cooldown_end, $lifecycle_state
            );

            INSERT INTO interventions (
                timestamp_utc,
                action,
                trigger_state,
                trigger_value,
                confidence,
                duration_seconds,
                escalation_level,
                reversible,
                reason,
                contributors_json,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state,
                session_id,
                command_type,
                payload_json,
                execution_state,
                decided_at_utc,
                applied_at_utc,
                acked_at_utc,
                error_code,
                retry_count,
                executor_agent_instance_id
            )
            SELECT
                $ts, $action, $trigger_state, $trigger_value, $confidence, $duration,
                $escalation, $reversible, $reason, $contributors,
                $intervention_id, $policy_id, $domain, $planned_end, $actual_end, $previous_state, $what_changed, $how_to_undo, $reversal_applied, $cooldown_end, $lifecycle_state,
                '', $command_type, $payload_json, $execution_state, $decided_at_utc, '', '', '', 0, ''
            WHERE $queue_delivery = 1;
            """;
        string eventTsUtc = Time.toIsoUtcMsString(controlEvent.TimestampUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$ts", eventTsUtc);
        cmd.Parameters.AddWithValue("$action", controlEvent.Action);
        cmd.Parameters.AddWithValue("$trigger_state", controlEvent.TriggerState);
        cmd.Parameters.AddWithValue("$trigger_value", controlEvent.TriggerValue);
        cmd.Parameters.AddWithValue("$confidence", controlEvent.Confidence);
        if (controlEvent.DurationSeconds.HasValue)
        {
            cmd.Parameters.AddWithValue("$duration", controlEvent.DurationSeconds.Value);
        }
        else
        {
            cmd.Parameters.AddWithValue("$duration", DBNull.Value);
        }
        cmd.Parameters.AddWithValue("$escalation", controlEvent.EscalationLevel);
        cmd.Parameters.AddWithValue("$reversible", controlEvent.Reversible ? 1 : 0);
        cmd.Parameters.AddWithValue("$reason", controlEvent.Reason);
        cmd.Parameters.AddWithValue("$contributors", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contributors = controlEvent.Contributors
        }));
        string whyJson = string.IsNullOrWhiteSpace(controlEvent.WhyJsonOverride)
            ? JsonSerializer.Serialize(new
            {
                schema_ver = 1,
                reason = controlEvent.Reason
            })
            : controlEvent.WhyJsonOverride;
        cmd.Parameters.AddWithValue("$why_json", whyJson);
        cmd.Parameters.AddWithValue("$state_flags_json", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            lifecycle_state = controlEvent.LifecycleState,
            reversible = controlEvent.Reversible,
            reversal_applied = controlEvent.ReversalApplied,
            event_command_type = commandType,
            event_execution_state = executionState,
            event_applied_at_utc = decidedAtUtc,
            event_acked_at_utc = decidedAtUtc,
            event_error_code = string.Empty,
            event_executor_agent_instance_id = string.Empty,
            delivery_surface = ResolveInterventionDeliverySurface(controlEvent.Action),
            interaction_kind = ResolveInterventionInteractionKind(controlEvent.Action),
            break_kind = ResolveInterventionBreakKind(controlEvent.Action),
            prompt_outcome = string.Empty,
            overlay_state = string.Empty
        }));
        cmd.Parameters.AddWithValue("$idempotency_key", BuildEventIdempotencyKey(controlEvent));
        cmd.Parameters.AddWithValue("$intervention_id", controlEvent.InterventionId);
        cmd.Parameters.AddWithValue("$policy_id", controlEvent.PolicyId);
        cmd.Parameters.AddWithValue("$domain", controlEvent.Domain);
        cmd.Parameters.AddWithValue("$planned_end", controlEvent.PlannedEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$actual_end", controlEvent.ActualEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$previous_state", controlEvent.PreviousSystemStateJson);
        cmd.Parameters.AddWithValue("$what_changed", controlEvent.WhatChanged ?? string.Empty);
        cmd.Parameters.AddWithValue("$how_to_undo", controlEvent.HowToUndo ?? string.Empty);
        cmd.Parameters.AddWithValue("$reversal_applied", controlEvent.ReversalApplied ? 1 : 0);
        cmd.Parameters.AddWithValue("$cooldown_end", controlEvent.CooldownEndTimestampUtc?.ToString("O") ?? string.Empty);
        cmd.Parameters.AddWithValue("$lifecycle_state", controlEvent.LifecycleState);
        cmd.Parameters.AddWithValue("$command_type", commandType);
        cmd.Parameters.AddWithValue("$payload_json", payloadJson);
        cmd.Parameters.AddWithValue("$execution_state", executionState);
        cmd.Parameters.AddWithValue("$decided_at_utc", decidedAtUtc);
        cmd.Parameters.AddWithValue("$queue_delivery", queueDelivery ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static string ResolveInterventionCommandType(string action)
    {
        string normalized = string.IsNullOrWhiteSpace(action)
            ? string.Empty
            : action.Trim().ToLowerInvariant();
        // Canonical break names emit show_break_overlay.
        // Legacy transport IDs kept for historical event compat.
        return normalized switch
        {
            PortaBreakSemantics.LongBreakKind => "show_break_overlay",
            PortaBreakSemantics.ShortBreakKind => "show_break_overlay",
            PortaBreakSemantics.LegacyLongBreakAction => "show_break_overlay",
            PortaBreakSemantics.LegacyShortBreakAction => "show_break_overlay",
            "focus_lock_in_suggestion" => "show_focus_lock_in_toast",
            "inform_overload_risk" => "show_status_toast",
            "inform_focus_protection" => "show_status_toast",
            "inform_fatigue_prevention" => "show_status_toast",
            "focus_mode_enabled" => "show_status_toast",
            "focus_assist_enabled" => "show_status_toast",
            "fatigue_relief_enabled" => "show_status_toast",
            "temporary_app_restriction_enabled" => "show_status_toast",
            "overload_brightness_reduced" => string.Empty,
            "overload_brightness_restored" => "show_status_toast",
            "brightness_unsupported" => "show_status_toast",
            "brightness_control_failed" => "show_status_toast",
            "notification_snapshot_failed" => "show_status_toast",
            "notification_suppress_failed" => "show_status_toast",
            "notification_restore_failed" => "show_status_toast",
            "overload_recovered" => "show_status_toast",
            "deep_work_notifications_suppressed" => "suppress_notifications",
            "deep_work_notifications_restored" => "restore_notifications",
            _ => string.Empty
        };
    }

    private static string BuildInterventionPayloadJson(AgentControlEvent controlEvent, string commandType)
    {
        if (string.IsNullOrWhiteSpace(commandType))
        {
            return "{}";
        }

        string action = controlEvent.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        string title = ResolveInterventionToastTitle(action);
        string message = ResolveInterventionToastMessage(controlEvent, action);

        return JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            action,
            command_type = commandType,
            delivery_surface = ResolveInterventionDeliverySurface(action),
            break_kind = ResolveInterventionBreakKind(action),
            title,
            message,
            tip_key = ResolveInterventionTipKey(action),
            interaction_kind = ResolveInterventionInteractionKind(action),
            accept_label = ResolveInterventionAcceptLabel(action),
            dismiss_label = ResolveInterventionDismissLabel(action),
            snooze_label = ResolveInterventionSnoozeLabel(action),
            suppress_label = ResolveInterventionSuppressLabel(action),
            what_changed = controlEvent.WhatChanged ?? string.Empty,
            how_to_undo = controlEvent.HowToUndo ?? string.Empty,
            duration_seconds = ResolveInterventionPromptDurationSeconds(controlEvent, action),
            countdown_seconds = ResolveInterventionCountdownSeconds(action),
            confidence = controlEvent.Confidence,
            policy_id = controlEvent.PolicyId,
            domain = controlEvent.Domain,
            intervention_id = controlEvent.InterventionId
        });
    }

    private static string ResolveInterventionToastTitle(string action)
    {
        return action switch
        {
            PortaBreakSemantics.LongBreakKind or PortaBreakSemantics.LegacyLongBreakAction => "Long break",
            PortaBreakSemantics.ShortBreakKind or PortaBreakSemantics.LegacyShortBreakAction => "Short break",
            "focus_lock_in_suggestion" => "Focus suggestion",
            "inform_overload_risk" => "Overload watch",
            "inform_focus_protection" => "Focus watch",
            "inform_fatigue_prevention" => "Fatigue watch",
            "focus_mode_enabled" => "Focus mode on",
            "focus_assist_enabled" => "Focus guidance",
            "fatigue_relief_enabled" => "Fatigue guidance",
            "temporary_app_restriction_enabled" => "App limits on",
            "deep_work_notifications_suppressed" => "Deep focus on",
            "deep_work_notifications_restored" => "Notifications restored",
            "overload_brightness_reduced" => "Brightness reduced",
            "overload_brightness_restored" => "Brightness restored",
            "overload_recovered" => "Pressure normal",
            "brightness_unsupported" or "brightness_control_failed" or "notification_snapshot_failed"
                or "notification_suppress_failed" or "notification_restore_failed"
                => "Ari limited",
            _ => "Ari"
        };
    }

    private static string ResolveInterventionToastMessage(AgentControlEvent controlEvent, string action)
    {
        return action switch
        {
            // Workstream 1.4 — canonical break kinds explain their ACTUAL trigger, built
            // from the decision's recorded inputs (never generic copy).
            PortaBreakSemantics.ShortBreakKind
                => ComposeBreakPromptMessage(controlEvent, PortaBreakSemantics.ShortBreakKind),
            PortaBreakSemantics.LongBreakKind
                => ComposeBreakPromptMessage(controlEvent, PortaBreakSemantics.LongBreakKind),
            "break_suggestion"
                => "Ari sees a long work run and rising strain. Ready for a long break now? Choose Skip or Snooze if now is not a good time, or turn off this tip.",
            "focus_lock_in_suggestion"
                => "Ari sees steady focus building and thinks staying on one task will help. Keep this task in front of you for a bit? Choose Later if not now, or turn off this tip.",
            "stretch_break_suggestion"
                => "Ari sees a long steady run and thinks a short recovery break could reduce strain. Take a short break now? Choose Skip or Snooze if not now, or turn off this tip.",
            "inform_overload_risk"
                => "Pressure is building. Not high enough to act yet — keep an eye on it.",
            "inform_focus_protection"
                => "Focus is building. Ari is watching for interruptions.",
            "inform_fatigue_prevention"
                => "Fatigue is rising. Consider a short break.",
            "focus_mode_enabled"
                => "Pressure stayed high. Focus mode is on for this block.",
            "focus_assist_enabled"
                => "Focus block detected. Focus guidance is on.",
            "fatigue_relief_enabled"
                => "Fatigue stayed high. Relief guidance is on.",
            "temporary_app_restriction_enabled"
                => "Pressure stayed high. Distracting apps are limited for a short block.",
            "deep_work_notifications_suppressed"
                => "Strong focus detected. Windows notifications are muted.",
            "deep_work_notifications_restored"
                => "Focus block ended. Notifications are back on.",
            "overload_brightness_reduced"
                => "Pressure is high. Screen brightness was lowered.",
            "overload_brightness_restored"
                => "Ari saw overload ease and restored your previous brightness. No action is needed, or open Settings to change this feature. You can turn this feature off in Settings.",
            "brightness_unsupported"
                => "Ari tried to lower brightness because overload stayed high, but this device does not expose that control. Lower brightness manually or open Health for details. You can turn this feature off in Settings.",
            "brightness_control_failed"
                => "Ari tried to lower brightness because overload stayed high, but Windows did not apply the change. Lower brightness manually or open Health for details. You can turn this feature off in Settings.",
            "notification_snapshot_failed"
                => "Ari entered deep work but could not read your current Windows notification state, so it cannot guarantee a clean restore. Mute banners manually or open Health for details. You can turn this feature off in Settings.",
            "notification_suppress_failed"
                => "Ari entered deep work but could not mute Windows notifications. Mute them manually or open Health for details. You can turn this feature off in Settings.",
            "notification_restore_failed"
                => "Ari ended a focus block but could not restore Windows notification settings cleanly. Check banners or open Health for details. You can turn this feature off in Settings.",
            "overload_recovered"
                => "Pressure is back to normal. All good.",
            _ when !string.IsNullOrWhiteSpace(controlEvent.WhatChanged)
                => controlEvent.WhatChanged!,
            _ => "Ari recorded a new state change."
        };
    }

    // Workstream 1.4 — reason line from the decision's own why-json, then the ask. The
    // reason text itself is pure (BreakReasonText) so the exact copy is unit-testable.
    internal static string ComposeBreakPromptMessage(AgentControlEvent controlEvent, string breakKind)
    {
        string needGateReason = string.Empty;
        double overloadOnDwellMs = 0d;
        double effectiveFocusSeconds = 0d;
        try
        {
            if (!string.IsNullOrWhiteSpace(controlEvent.WhyJsonOverride))
            {
                using JsonDocument doc = JsonDocument.Parse(controlEvent.WhyJsonOverride);
                if (doc.RootElement.TryGetProperty("reasons", out JsonElement reasons)
                    && reasons.ValueKind == JsonValueKind.Object)
                {
                    if (reasons.TryGetProperty("need_gate_reason", out JsonElement reasonEl)
                        && reasonEl.ValueKind == JsonValueKind.String)
                    {
                        needGateReason = reasonEl.GetString() ?? string.Empty;
                    }

                    if (reasons.TryGetProperty("overload_on_dwell_ms", out JsonElement dwellEl)
                        && dwellEl.TryGetDouble(out double dwell))
                    {
                        overloadOnDwellMs = dwell;
                    }

                    if (reasons.TryGetProperty("effective_focus_s", out JsonElement focusEl)
                        && focusEl.TryGetDouble(out double focus))
                    {
                        effectiveFocusSeconds = focus;
                    }
                }
            }
        }
        catch
        {
        }

        string reason = Porta.Agent.Common.BreakReasonText.For(
            needGateReason,
            breakKind,
            overloadForMinutes: overloadOnDwellMs / 60_000d,
            effectiveFocusHours: effectiveFocusSeconds / 3600d);
        string ask = string.Equals(breakKind, PortaBreakSemantics.LongBreakKind, StringComparison.Ordinal)
            ? "Ready for a longer break now?"
            : "Take a short break now?";
        return $"{reason} {ask} Choose Skip or Snooze if now is not a good time.";
    }

    private static string NormalizeExecutionResult(string result)
    {
        string normalized = string.IsNullOrWhiteSpace(result)
            ? string.Empty
            : result.Trim().ToLowerInvariant();
        return normalized switch
        {
            "applied" => "applied",
            "verified" => "verified",
            "failed" => "failed",
            "overlay_started" => "overlay_started",
            "accepted" => "accepted",
            "skipped" => "skipped",
            "dismissed" => "dismissed",
            "snoozed" => "snoozed",
            "suppressed_by_preference" => "suppressed_by_preference",
            "overlay_recovered" => "overlay_recovered",
            "overlay_completed" => "overlay_completed",
            "overlay_aborted" => "overlay_aborted",
            _ => string.Empty
        };
    }

    private static string ResolveInterventionTipKey(string action)
    {
        // Canonical break kinds are their own tip keys.
        // Legacy IDs preserved so historical events resolve correctly.
        if (PortaBreakSemantics.IsBreakSuggestionAction(action))
        {
            return PortaBreakSemantics.ResolveBreakKind(action) ?? action;
        }

        return action switch
        {
            "focus_lock_in_suggestion" => "focus_lock_in_suggestion",
            _ => string.Empty
        };
    }

    private static string ResolveInterventionInteractionKind(string action)
    {
        return PortaBreakSemantics.IsBreakSuggestionAction(action) || action == "focus_lock_in_suggestion"
            ? "choice_prompt"
            : "status";
    }

    private static string ResolveInterventionDeliverySurface(string action)
    {
        if (PortaBreakSemantics.IsBreakSuggestionAction(action)) return "break_overlay";
        return action switch
        {
            "focus_lock_in_suggestion" => "widget",
            _ => "toast"
        };
    }

    private static string? ResolveInterventionBreakKind(string action) =>
        PortaBreakSemantics.ResolveBreakKind(action);

    private static string ResolveInterventionAcceptLabel(string action)
    {
        if (PortaBreakSemantics.IsBreakSuggestionAction(action)) return "Take break";
        return action == "focus_lock_in_suggestion" ? "Yes" : string.Empty;
    }

    private static string ResolveInterventionDismissLabel(string action)
    {
        if (PortaBreakSemantics.IsBreakSuggestionAction(action)) return "Skip";
        return action == "focus_lock_in_suggestion" ? "No" : string.Empty;
    }

    private static string ResolveInterventionSnoozeLabel(string action)
    {
        if (PortaBreakSemantics.IsBreakSuggestionAction(action)) return "Snooze";
        return action == "focus_lock_in_suggestion" ? "Later" : string.Empty;
    }

    private static string ResolveInterventionSuppressLabel(string action)
    {
        return PortaBreakSemantics.IsBreakSuggestionAction(action) || action == "focus_lock_in_suggestion"
            ? "Turn off this tip"
            : string.Empty;
    }

    private static bool IsPendingInterventionState(string executionState) =>
        executionState is "decided" or "overlay_started" or "accepted" or "overlay_recovered";

    private static bool CanTransitionInterventionState(string currentState, string nextState)
    {
        return currentState switch
        {
            "decided" => nextState is "applied"
                or "verified"
                or "failed"
                or "overlay_started"
                or "accepted"
                or "dismissed"
                or "skipped"
                or "snoozed"
                or "suppressed_by_preference",
            "overlay_started" => nextState is "accepted"
                or "failed"
                or "dismissed"
                or "skipped"
                or "snoozed"
                or "suppressed_by_preference",
            "accepted" => nextState is "overlay_recovered"
                or "overlay_completed"
                or "overlay_aborted"
                or "failed",
            "overlay_recovered" => nextState is "overlay_completed"
                or "overlay_aborted"
                or "failed",
            _ => false
        };
    }

    private static bool ShouldSetAppliedAtUtcForResult(string result) =>
        result is "applied"
            or "verified"
            or "overlay_started"
            or "accepted"
            or "skipped"
            or "dismissed"
            or "overlay_recovered"
            or "overlay_completed"
            or "overlay_aborted";

    private static int ResolveInterventionPromptDurationSeconds(AgentControlEvent controlEvent, string action)
    {
        return PortaBreakSemantics.IsBreakSuggestionAction(action) || action == "focus_lock_in_suggestion"
            ? Math.Clamp(controlEvent.DurationSeconds ?? 120, 30, 300)
            : controlEvent.DurationSeconds ?? 8;
    }

    private static int? ResolveInterventionCountdownSeconds(string action)
    {
        string? breakKind = ResolveInterventionBreakKind(action);
        return breakKind == null
            ? null
            : (int)PortaBreakSemantics.ResolveBreakCountdownDuration(breakKind).TotalSeconds;
    }

    private static void InsertInterventionLifecycleEvent(
        SqliteConnection connection,
        SqliteTransaction tx,
        (long Id, string SessionId, string ExecutionState, string Action, string CommandType, string PolicyId, string Domain, string WhatChanged, string HowToUndo) targetRow,
        AgentInterventionAck ack)
    {
        string normalizedResult = NormalizeExecutionResult(ack.Result);
        if (string.IsNullOrWhiteSpace(normalizedResult))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO events (
                timestamp_utc,
                ts_utc,
                action,
                duration_seconds,
                why,
                why_json,
                escalation,
                reversible,
                trigger_state,
                trigger_value,
                confidence,
                contributors_json,
                state_flags_json,
                idempotency_key,
                intervention_id,
                policy_id,
                domain,
                planned_end_timestamp_utc,
                actual_end_timestamp_utc,
                previous_system_state_json,
                what_changed,
                how_to_undo,
                reversal_applied,
                cooldown_end_timestamp_utc,
                lifecycle_state
            ) VALUES (
                $timestamp_utc,
                $ts_utc,
                $action,
                NULL,
                $why,
                $why_json,
                $escalation,
                0,
                'interaction',
                0,
                1,
                $contributors_json,
                $state_flags_json,
                $idempotency_key,
                $intervention_id,
                $policy_id,
                $domain,
                '',
                '',
                '{}',
                $what_changed,
                $how_to_undo,
                0,
                '',
                'activated'
            );
            """;
        string appliedAtUtc = ack.AppliedAtUtc.ToUniversalTime().ToString("O");
        string deliverySurface = !string.IsNullOrWhiteSpace(ack.DeliverySurface)
            ? ack.DeliverySurface.Trim()
            : ResolveInterventionDeliverySurface(targetRow.Action);
        string interactionKind = !string.IsNullOrWhiteSpace(ack.InteractionKind)
            ? ack.InteractionKind.Trim()
            : ResolveInterventionInteractionKind(targetRow.Action);
        string? breakKind = !string.IsNullOrWhiteSpace(ack.BreakKind)
            ? ack.BreakKind.Trim()
            : ResolveInterventionBreakKind(targetRow.Action);
        cmd.Parameters.AddWithValue("$ts_utc", appliedAtUtc);
        cmd.Parameters.AddWithValue("$timestamp_utc", appliedAtUtc);
        cmd.Parameters.AddWithValue("$action", targetRow.Action);
        cmd.Parameters.AddWithValue("$why", ResolveLifecycleEventReason(normalizedResult));
        cmd.Parameters.AddWithValue("$why_json", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            reason = ResolveLifecycleEventReason(normalizedResult),
            execution_state = normalizedResult
        }));
        cmd.Parameters.AddWithValue("$escalation", "Suggest");
        cmd.Parameters.AddWithValue("$contributors_json", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contributors = new[] { "user_agent_intervention_ack" }
        }));
        cmd.Parameters.AddWithValue("$state_flags_json", JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            lifecycle_state = "activated",
            reversible = false,
            reversal_applied = false,
            event_command_type = targetRow.CommandType,
            event_execution_state = normalizedResult,
            event_applied_at_utc = appliedAtUtc,
            event_acked_at_utc = appliedAtUtc,
            event_error_code = ack.ErrorCode?.Trim() ?? string.Empty,
            event_executor_agent_instance_id = ack.AgentInstanceId.Trim(),
            delivery_surface = deliverySurface,
            interaction_kind = interactionKind,
            break_kind = breakKind,
            prompt_outcome = ResolvePromptOutcome(normalizedResult),
            overlay_state = ResolveOverlayState(normalizedResult)
        }));
        cmd.Parameters.AddWithValue("$idempotency_key", $"intervention_ack_v1:{targetRow.Action}:{ack.InterventionId}:{normalizedResult}:{appliedAtUtc}:{ack.AgentInstanceId.Trim().ToLowerInvariant()}");
        cmd.Parameters.AddWithValue("$intervention_id", ack.InterventionId.Trim());
        cmd.Parameters.AddWithValue("$policy_id", string.IsNullOrWhiteSpace(targetRow.PolicyId) ? "n/a" : targetRow.PolicyId);
        cmd.Parameters.AddWithValue("$domain", string.IsNullOrWhiteSpace(targetRow.Domain) ? "control" : targetRow.Domain);
        cmd.Parameters.AddWithValue("$what_changed", ResolveLifecycleWhatChanged(targetRow.WhatChanged, targetRow.Action, normalizedResult));
        cmd.Parameters.AddWithValue("$how_to_undo", string.IsNullOrWhiteSpace(targetRow.HowToUndo) ? "No manual undo required." : targetRow.HowToUndo);
        cmd.ExecuteNonQuery();
    }

    private static string ResolveLifecycleEventReason(string executionState) =>
        executionState switch
        {
            "overlay_started" => "overlay_started",
            "accepted" => "break_accepted",
            "skipped" => "break_skipped",
            "dismissed" => "prompt_dismissed",
            "snoozed" => "prompt_snoozed",
            "suppressed_by_preference" => "prompt_suppressed_by_preference",
            "overlay_recovered" => "overlay_recovered",
            "overlay_completed" => "break_completed",
            "overlay_aborted" => "break_aborted",
            "failed" => "intervention_failed",
            "verified" => "intervention_verified",
            "applied" => "intervention_applied",
            _ => "intervention_acknowledged"
        };

    private static string ResolveLifecycleWhatChanged(string fallbackWhatChanged, string action, string executionState)
    {
        string label = PortaBreakSemantics.IsBreakSuggestionAction(action)
            ? PortaBreakSemantics.ResolveBreakLabel(ResolveInterventionBreakKind(action) ?? "short_break")
            : "Suggestion";
        return executionState switch
        {
            "overlay_started" => $"{label} overlay shown.",
            "accepted" => $"{label} accepted and countdown started.",
            "skipped" => $"{label} skipped by the user.",
            "dismissed" => "Prompt dismissed by the user.",
            "snoozed" => "Prompt snoozed by the user.",
            "suppressed_by_preference" => "Prompt suppressed by user preference.",
            "overlay_recovered" => $"{label} overlay recovered after restart or unlock.",
            "overlay_completed" => $"{label} completed in the Ari overlay.",
            "overlay_aborted" => $"{label} aborted from the Ari overlay.",
            "failed" => string.IsNullOrWhiteSpace(fallbackWhatChanged) ? "Intervention failed." : fallbackWhatChanged,
            _ => string.IsNullOrWhiteSpace(fallbackWhatChanged) ? "Intervention acknowledged." : fallbackWhatChanged
        };
    }

    private static string? ResolvePromptOutcome(string executionState) =>
        executionState switch
        {
            "accepted" => "accepted",
            "dismissed" => "dismissed",
            "skipped" => "skipped",
            "snoozed" => "snoozed",
            "suppressed_by_preference" => "suppressed_by_preference",
            _ => null
        };

    private static string? ResolveOverlayState(string executionState) =>
        executionState switch
        {
            "overlay_started" => "prompt_visible",
            "accepted" => "countdown_active",
            "overlay_recovered" => "countdown_recovered",
            "overlay_completed" => "countdown_completed",
            "overlay_aborted" => "countdown_aborted",
            "skipped" => "prompt_skipped",
            "dismissed" => "prompt_dismissed",
            "snoozed" => "prompt_snoozed",
            "suppressed_by_preference" => "prompt_suppressed",
            _ => null
        };

    private static bool IsUniqueConstraintViolation(SqliteException ex)
    {
        // SQLite: 19 = SQLITE_CONSTRAINT, 2067 = SQLITE_CONSTRAINT_UNIQUE.
        return ex.SqliteErrorCode == 19 || ex.SqliteExtendedErrorCode == 2067;
    }

    internal static string BuildBreakEventIdempotencyKey(long nowUtcMs, string installIdHash, long? cooldownWindowMs = null)
    {
        string safeHash = string.IsNullOrWhiteSpace(installIdHash)
            ? "unknown_install"
            : installIdHash.Trim().ToLowerInvariant();
        long windowStartMs = ResolveBreakCooldownWindowStartMs(nowUtcMs, cooldownWindowMs);
        return $"break_v1:{windowStartMs}:{safeHash}";
    }

    private static long ResolveBreakCooldownWindowStartMs(long nowUtcMs, long? cooldownWindowMs = null)
    {
        long windowMs = cooldownWindowMs.GetValueOrDefault(PortaExecutionConstants.BreakPolicy.CooldownWindowMs);
        if (windowMs <= 0)
        {
            return nowUtcMs;
        }

        long quotient = Math.DivRem(nowUtcMs, windowMs, out long remainder);
        if (remainder < 0)
        {
            quotient--;
        }

        return checked(quotient * windowMs);
    }

    internal static string BuildDeepWorkNotificationIdempotencyKey(
        string action,
        long eventTsMs,
        RuntimeDeepWorkDetectionSettings? policy = null)
    {
        string normalizedAction = string.IsNullOrWhiteSpace(action)
            ? "unknown"
            : action.Trim().ToLowerInvariant();
        long windowStartMs = ResolveDeepWorkTransitionWindowStartMs(normalizedAction, eventTsMs, policy);
        string keyPrefix = normalizedAction switch
        {
            "deep_work_notifications_suppressed" => "dw_notify_on",
            "deep_work_notifications_restored" => "dw_notify_off",
            "notification_suppress_failed" => "dw_notify_on_fail",
            "notification_snapshot_failed" => "dw_notify_snapshot_fail",
            "notification_restore_failed" => "dw_notify_off_fail",
            _ => "dw_notify"
        };
        return $"{keyPrefix}:v1:{windowStartMs}";
    }

    internal static string BuildOverloadBrightnessIdempotencyKey(
        string action,
        long eventTsMs,
        RuntimeOverloadStabilizationSettings? policy = null)
    {
        string normalizedAction = string.IsNullOrWhiteSpace(action)
            ? "unknown"
            : action.Trim().ToLowerInvariant();
        long windowStartMs = ResolveOverloadBrightnessTransitionWindowStartMs(normalizedAction, eventTsMs, policy);
        string keyPrefix = normalizedAction switch
        {
            "overload_brightness_reduced" => "brightness_on",
            "overload_brightness_restored" => "brightness_off",
            "brightness_unsupported" => "brightness_unsupported",
            "brightness_control_failed" => "brightness_fail",
            _ => "brightness"
        };
        return $"{keyPrefix}:v1:{windowStartMs}";
    }

    internal static string BuildOverloadRecoveryIdempotencyKey(long eventTsMs, RuntimeOverloadStabilizationSettings? policy = null)
    {
        RuntimeOverloadStabilizationSettings settings = policy ?? DefaultOverloadStabilizationSettings();
        long windowMs = settings.DwellOffSeconds * 1000L;
        long windowStartMs = ResolveWindowStartMs(eventTsMs, windowMs);
        return $"overload_recovery:v1:{windowStartMs}";
    }

    private static long ResolveDeepWorkTransitionWindowStartMs(
        string action,
        long eventTsMs,
        RuntimeDeepWorkDetectionSettings? policy = null)
    {
        RuntimeDeepWorkDetectionSettings settings = policy ?? new RuntimeDeepWorkDetectionSettings(
            PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds,
            PortaExecutionConstants.DeepWorkDetection.ConfidenceMin,
            PortaExecutionConstants.DeepWorkDetection.FragmentationMax,
            PortaExecutionConstants.DeepWorkDetection.VolatilityMax,
            PortaExecutionConstants.DeepWorkDetection.IntensityMin,
            PortaExecutionConstants.DeepWorkDetection.IntensityMax,
            PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds,
            PortaExecutionConstants.DeepWorkDetection.DwellOffSeconds,
            PortaExecutionConstants.DeepWorkDetection.GapAllowSeconds);
        long windowMs = action switch
        {
            "deep_work_notifications_suppressed" => settings.DwellOnSeconds * 1000L,
            "notification_suppress_failed" => settings.DwellOnSeconds * 1000L,
            "notification_snapshot_failed" => settings.DwellOnSeconds * 1000L,
            "deep_work_notifications_restored" => settings.DwellOffSeconds * 1000L,
            "notification_restore_failed" => settings.DwellOffSeconds * 1000L,
            _ => Time.bucketSeconds * 1000L
        };
        return ResolveWindowStartMs(eventTsMs, windowMs);
    }

    private static long ResolveOverloadBrightnessTransitionWindowStartMs(
        string action,
        long eventTsMs,
        RuntimeOverloadStabilizationSettings? policy = null)
    {
        RuntimeOverloadStabilizationSettings settings = policy ?? DefaultOverloadStabilizationSettings();
        long windowMs = action switch
        {
            "overload_brightness_reduced" => settings.DwellOnSeconds * 1000L,
            "brightness_control_failed" => settings.DwellOnSeconds * 1000L,
            "brightness_unsupported" => settings.DwellOnSeconds * 1000L,
            "overload_brightness_restored" => settings.DwellOffSeconds * 1000L,
            _ => Time.bucketSeconds * 1000L
        };
        return ResolveWindowStartMs(eventTsMs, windowMs);
    }

    private static long ResolveWindowStartMs(long nowUtcMs, long windowMs)
    {
        if (windowMs <= 0)
        {
            return nowUtcMs;
        }

        long quotient = Math.DivRem(nowUtcMs, windowMs, out long remainder);
        if (remainder < 0)
        {
            quotient--;
        }

        return checked(quotient * windowMs);
    }

    private static string ResolveBreakInstallIdHash()
    {
#pragma warning disable CA1416
        byte[] installSecret = Secrets.GetInstallSecret();
#pragma warning restore CA1416
        using var hmac = new HMACSHA256(installSecret);
        byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes("install"));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string BuildEventIdempotencyKey(AgentControlEvent controlEvent)
    {
        if (!string.IsNullOrWhiteSpace(controlEvent.IdempotencyKey))
        {
            return controlEvent.IdempotencyKey.Trim();
        }

        string interventionId = string.IsNullOrWhiteSpace(controlEvent.InterventionId)
            ? "none"
            : controlEvent.InterventionId.Trim();
        string lifecycle = string.IsNullOrWhiteSpace(controlEvent.LifecycleState)
            ? "activated"
            : controlEvent.LifecycleState.Trim();
        string action = string.IsNullOrWhiteSpace(controlEvent.Action)
            ? "unknown"
            : controlEvent.Action.Trim();
        if ((string.Equals(action, "break_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal))
            && string.Equals(lifecycle, "activated", StringComparison.Ordinal))
        {
            return BuildBreakEventIdempotencyKey(
                nowUtcMs: controlEvent.TimestampUtc.ToUnixTimeMilliseconds(),
                installIdHash: BreakInstallIdHash.Value);
        }

        string ts = controlEvent.TimestampUtc.ToString("O");
        return $"{action}|{interventionId}|{lifecycle}|{ts}";
    }

    private int ReadSingleInt(SqliteConnection connection, string sql, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        object? scalar = cmd.ExecuteScalar();
        return scalar is long l ? (int)l : Convert.ToInt32(scalar ?? 0);
    }

    private static double ReadSingleDouble(SqliteConnection connection, string sql, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        object? scalar = cmd.ExecuteScalar();
        return scalar is double d ? d : Convert.ToDouble(scalar ?? 0d);
    }

    private static int? ReadMostVolatileHour(SqliteConnection connection, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT CAST(strftime('%H', timestamp_utc) AS INTEGER) AS hour_utc
            FROM state_buckets
            WHERE timestamp_utc >= $start AND timestamp_utc < $end
            GROUP BY hour_utc
            ORDER BY AVG(ABS(overload_likelihood - focus_strength)) DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt32(scalar);
    }

    private void PruneIfNeeded(SqliteConnection connection, SqliteTransaction tx, DateTimeOffset nowUtc)
    {
        if ((nowUtc - _lastPruneUtc) < TimeSpan.FromMinutes(30))
        {
            return;
        }

        DateTimeOffset cutoff = nowUtc.Subtract(_retention);
        string cutoffRaw = cutoff.ToString("O");

        // IMPORTANT: ground_truth_labels is intentionally NOT pruned here. Labels must survive
        // for months to feed the future learned model (Phase 1b). They are rare and self-contained,
        // so keeping them forever is cheap. Do NOT add ground_truth_labels to this DELETE list.
        //
        // primitive_buckets was MISSING from this list until 2026-08-12, and the omission was an
        // accident with a provable date: this list was written in b3beced at 13:23:52 on
        // 2026-03-02, when primitive_buckets did not yet exist; the table was created 38 minutes
        // later in a7d5370. Nobody came back. `DELETE FROM primitive_buckets` had never appeared
        // in any commit on any branch.
        //
        // Measured cost of that on the founder's machine, 2026-08-12: 542 MB, 137,217 rows,
        // 4,141 bytes per row, 34 days deep while every table in this list held 15 — about
        // 16.7 MB a day, unbounded. Multiplied by eight, because DatabaseBackupWorker keeps seven
        // nightly VACUUM copies of the whole database beside the live one.
        //
        // It uses start_utc, not timestamp_utc (ix_primitive_buckets_start_utc covers it).
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            DELETE FROM input_signal_windows WHERE window_end_utc < $cutoff;
            DELETE FROM input_buckets WHERE timestamp_utc < $cutoff;
            DELETE FROM state_buckets WHERE timestamp_utc < $cutoff;
            DELETE FROM primitive_buckets WHERE start_utc < $cutoff;
            DELETE FROM events WHERE timestamp_utc < $cutoff;
            DELETE FROM system_changes WHERE timestamp_utc < $cutoff;
            DELETE FROM consent WHERE timestamp_utc < $cutoff;
            DELETE FROM settings WHERE timestamp_utc < $cutoff;
            DELETE FROM interventions WHERE timestamp_utc < $cutoff;
            DELETE FROM diagnostics_snapshots WHERE timestamp_utc < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoffRaw);
        cmd.ExecuteNonQuery();

        // The app rollup is the point of the exercise and keeps its own, far longer window: it
        // exists precisely so a project's picture outlives the 14-day capture tables. Matched to
        // task retention, because a rollup row whose task has aged out can no longer name its
        // project and is just an app total with a dangling id.
        using var rollupCmd = connection.CreateCommand();
        rollupCmd.Transaction = tx;
        rollupCmd.CommandText = "DELETE FROM app_usage_rollup WHERE hour_start_utc < $rollupCutoff;";
        rollupCmd.Parameters.AddWithValue(
            "$rollupCutoff", nowUtc.AddDays(-TaskDoneRetentionDays).ToUniversalTime().ToString("O"));
        rollupCmd.ExecuteNonQuery();

        _lastPruneUtc = nowUtc;
    }

    private void EnsureSchema()
    {
        CoreStateDbStartup.InitializeAndMigrate(logPragmasAtBoot: true, dbPathOverride: _dbPath);
    }

    private void ScheduleBackgroundVacuumIfNeeded()
    {
        // PASSIVE WAL checkpoint only — no VACUUM, no TRUNCATE.
        // VACUUM and wal_checkpoint(TRUNCATE) both require exclusive DB access and block
        // all service reads/writes until they complete. On a large WAL (>1 GB), that
        // exhausts the .NET thread pool and hangs the HTTP server.
        // A PASSIVE checkpoint is safe: it copies WAL frames without waiting for any lock.
        // SQLite's default wal_autocheckpoint=1000 handles routine WAL maintenance.
        if (_backgroundVacuumScheduled)
        {
            return;
        }

        _backgroundVacuumScheduled = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            try
            {
                using var connection = DbOpen.OpenConnection(_connectionString);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SqliteBrainStateStore] WAL checkpoint failed: {ex.Message}");
            }
        });
    }

    private static List<string> DeserializeContributors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        string? value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            items.Add(value);
                        }
                    }
                }

                return items;
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("contributors", out JsonElement contributorsElement)
                && contributorsElement.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                foreach (JsonElement element in contributorsElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        string? value = element.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            items.Add(value);
                        }
                    }
                }

                return items;
            }

            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static List<string> DeserializeStringList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static double ReadDoubleOrFallback(SqliteDataReader reader, int ordinal, double fallback)
    {
        try
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetDouble(ordinal);
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal)
    {
        try
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetDouble(ordinal);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ReadStringOrFallback(SqliteDataReader reader, int ordinal, string fallback)
    {
        try
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetString(ordinal);
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static bool ReadBoolOrFallback(SqliteDataReader reader, int ordinal, bool fallback)
    {
        try
        {
            if (!reader.IsDBNull(ordinal))
            {
                return reader.GetInt32(ordinal) == 1;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static DateTimeOffset ParseEventTsUtc(SqliteDataReader reader)
    {
        string tsUtcRaw = ReadStringOrFallback(reader, 1, string.Empty);
        if (!string.IsNullOrWhiteSpace(tsUtcRaw))
        {
            return DateTimeOffset.Parse(tsUtcRaw);
        }

        string timestampRaw = ReadStringOrFallback(reader, 2, string.Empty);
        if (!string.IsNullOrWhiteSpace(timestampRaw))
        {
            return DateTimeOffset.Parse(timestampRaw);
        }

        return DateTimeOffset.UnixEpoch;
    }

    private static AgentControlEvent ReadControlEvent(SqliteDataReader reader)
    {
        string stateFlagsJson = ReadStringOrFallback(reader, 25, "{\"schema_ver\":1}");
        EventStateOverride overrides = EventStateOverride.Parse(stateFlagsJson);
        return new AgentControlEvent(
            TimestampUtc: ParseEventTsUtc(reader),
            Action: reader.GetString(3),
            TriggerState: reader.GetString(4),
            TriggerValue: reader.GetDouble(5),
            Confidence: reader.GetDouble(6),
            DurationSeconds: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            EscalationLevel: reader.GetString(8),
            Reversible: reader.GetInt32(9) == 1,
            Reason: reader.GetString(10),
            Contributors: DeserializeContributors(reader.GetString(11)),
            InterventionId: ReadStringOrFallback(reader, 12, string.Empty),
            PolicyId: ReadStringOrFallback(reader, 13, "n/a"),
            Domain: ReadStringOrFallback(reader, 14, "control"),
            PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 15, string.Empty)),
            ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 16, string.Empty)),
            PreviousSystemStateJson: ReadStringOrFallback(reader, 17, "{}"),
            WhatChanged: ReadStringOrFallback(reader, 18, string.Empty),
            HowToUndo: ReadStringOrFallback(reader, 19, string.Empty),
            ReversalApplied: ReadBoolOrFallback(reader, 20, false),
            CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 21, string.Empty)),
            LifecycleState: ReadStringOrFallback(reader, 22, "activated"),
            EventId: reader.GetInt64(0),
            IdempotencyKey: ReadStringOrFallback(reader, 23, string.Empty),
            WhyJson: ReadStringOrFallback(reader, 24, "{\"schema_ver\":1}"),
            StateFlagsJson: stateFlagsJson,
            CommandType: overrides.CommandType ?? ReadStringOrFallback(reader, 26, string.Empty),
            ExecutionState: overrides.ExecutionState ?? ReadStringOrFallback(reader, 27, "decision_only"),
            DecidedAtUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 28, string.Empty)),
            AppliedAtUtc: overrides.AppliedAtUtc ?? ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 29, string.Empty)),
            AckedAtUtc: overrides.AckedAtUtc ?? ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 30, string.Empty)),
            ExecutionErrorCode: overrides.ErrorCode ?? ReadStringOrFallback(reader, 31, string.Empty),
            ExecutorAgentInstanceId: overrides.ExecutorAgentInstanceId ?? ReadStringOrFallback(reader, 32, string.Empty));
    }

    private sealed record EventStateOverride(
        string? CommandType,
        string? ExecutionState,
        DateTimeOffset? AppliedAtUtc,
        DateTimeOffset? AckedAtUtc,
        string? ErrorCode,
        string? ExecutorAgentInstanceId)
    {
        public static EventStateOverride Parse(string? stateFlagsJson)
        {
            if (string.IsNullOrWhiteSpace(stateFlagsJson))
            {
                return new EventStateOverride(null, null, null, null, null, null);
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new EventStateOverride(null, null, null, null, null, null);
                }

                return new EventStateOverride(
                    ReadString(root, "event_command_type"),
                    ReadString(root, "event_execution_state"),
                    ReadDateTime(root, "event_applied_at_utc"),
                    ReadDateTime(root, "event_acked_at_utc"),
                    ReadString(root, "event_error_code"),
                    ReadString(root, "event_executor_agent_instance_id"));
            }
            catch
            {
                return new EventStateOverride(null, null, null, null, null, null);
            }
        }

        private static string? ReadString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement node) || node.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = node.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static DateTimeOffset? ReadDateTime(JsonElement root, string propertyName)
        {
            string? raw = ReadString(root, propertyName);
            return ParseOptionalDateTimeOffset(raw);
        }
    }

    private static string ResolveDominantStateName(double focusStrength, double overloadLikelihood, double fatigueProbability)
    {
        double dominant = Math.Max(focusStrength, Math.Max(overloadLikelihood, fatigueProbability));
        if (dominant == focusStrength)
        {
            return "focus";
        }

        if (dominant == overloadLikelihood)
        {
            return "overload";
        }

        return "fatigue";
    }

    private static DateTimeOffset? ParseOptionalDateTimeOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string BuildExportRoot()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "exports");
    }

    private void TryDeleteDbFiles()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
        }

        string walPath = $"{_dbPath}-wal";
        string shmPath = $"{_dbPath}-shm";

        try
        {
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }
        }
        catch
        {
        }

        try
        {
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }
        }
        catch
        {
        }
    }

    private static ReadGateLease AcquireReadGate()
    {
        if (!_readGate.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("read_gate_timeout");
        return new ReadGateLease();
    }

    private readonly struct ReadGateLease : IDisposable
    {
        public void Dispose() => _readGate.Release();
    }
}

