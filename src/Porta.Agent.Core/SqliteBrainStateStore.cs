using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Common;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Db;
using Porta.Agent.Core.Outputs;
using Porta.Agent.Core.Security;

namespace Porta.Agent.Core;

public sealed class SqliteBrainStateStore : IBrainStateStore, IBucketFinalizerDbWriter
{
    private const int DefaultRetentionDays = 90;
    private const int DefaultEventCooldownSeconds = 20;
    private const int ResponsiveReadBusyTimeoutMs = 250;
    private static readonly Lazy<string> BreakInstallIdHash = new(ResolveBreakInstallIdHash, isThreadSafe: true);

    private readonly object _gate = new();
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly IDeepWorkOutputActionCoordinator _deepWorkOutputActionCoordinator;
    private readonly IOverloadBrightnessActionCoordinator _overloadBrightnessActionCoordinator;
    private readonly IRuntimePolicyProvider _runtimePolicyProvider;
    private readonly TimeSpan _retention = TimeSpan.FromDays(DefaultRetentionDays);
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
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
                DerivedSessionMetricsResult derivedSession = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
                    connection,
                    tx,
                    mapped,
                    primitive,
                    gating);
                OverloadInferenceResult overloadInference = OverloadInferenceComputer.ComputeForFinalizedBucket(
                    gatedSnapshot,
                    mapped,
                    primitive,
                    gating,
                    robust);
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
                BreakSuggestionDecision breakSuggestionDecision = BreakSuggestionPolicy.ShouldTriggerBreak(
                    gating,
                    overloadStabilization,
                    overloadInference,
                    derivedSession,
                    breakFocusWindow,
                    resolveCooldown: () => ReadBreakPolicyCooldownSnapshot(connection, tx, finalizeTsMs),
                    policy.BreakPolicy);
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
                InsertLegacySnapshot(connection, tx, inferredSnapshot);
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
                    policyEnvelope);
                PersistBreakPolicyRuntimeState(
                    connection,
                    tx,
                    finalizeTsMs,
                    breakSuggestionDecision,
                    breakInserted,
                    breakFocusWindow,
                    policyEnvelope);
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
        lock (_gate)
        {
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
        lock (_gate)
        {
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
                    DELETE FROM events;
                    DELETE FROM interventions;
                    DELETE FROM system_changes;
                    DELETE FROM diagnostics;
                    DELETE FROM diagnostics_snapshots;
                    DELETE FROM consent;
                    DELETE FROM settings;
                    DELETE FROM brain_snapshots;
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
        lock (_gate)
        {
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
        lock (_gate)
        {
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
    }

    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds)
    {
        lock (_gate)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int bucket = Math.Clamp(bucketSeconds, 1, 3600);
            var rawRows = new List<AgentBrainSnapshot>();

            using var connection = DbOpen.OpenReadConnection(_connectionString);
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
                WHERE b.timestamp_utc >= $from AND b.timestamp_utc <= $to
                ORDER BY b.timestamp_utc ASC;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rawRows.Add(new AgentBrainSnapshot(
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
                    ContributorsJson: ReadStringOrFallback(reader, 28, "{\"schema_ver\":1}")));
            }

            if (bucket == 1 || rawRows.Count <= 1)
            {
                return rawRows;
            }

            var byBucket = new Dictionary<long, AgentBrainSnapshot>();
            foreach (AgentBrainSnapshot row in rawRows)
            {
                long key = row.TimestampUtc.ToUnixTimeSeconds() / bucket;
                byBucket[key] = row;
            }

            return byBucket
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .ToList();
        }
    }

    public IReadOnlyList<AgentInputSignalWindow> ReadInputSignalWindows(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit = 50000)
    {
        lock (_gate)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int boundedLimit = Math.Clamp(limit, 1, 200000);
            var rows = new List<AgentInputSignalWindow>(Math.Min(boundedLimit, 4096));

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
    }

    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null)
    {
        lock (_gate)
        {
            int boundedLimit = Math.Clamp(limit, 1, 500);
            var rows = new List<AgentControlEvent>(boundedLimit);

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
    }

    public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200)
    {
        lock (_gate)
        {
            int boundedLimit = Math.Clamp(limit, 1, 1000);
            long safeSinceId = Math.Max(0, sinceIdExclusive);
            var rows = new List<AgentControlEvent>(boundedLimit);

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
    }

    public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000)
    {
        lock (_gate)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int boundedLimit = Math.Clamp(limit, 1, 10000);
            var rows = new List<AgentControlEvent>();

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
                      AND i.execution_state = 'decided'
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
                var rows = new List<(long Id, string SessionId, string ExecutionState, string Action, string CommandType)>();

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
                            command_type
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
                            CommandType: ReadStringOrFallback(reader, 4, string.Empty)));
                    }
                }

                if (rows.Count == 0)
                {
                    TryRollback(tx);
                    return false;
                }

                List<long> decidedRowIds = rows
                    .Where(static row => string.Equals(row.ExecutionState, "decided", StringComparison.Ordinal))
                    .Select(static row => row.Id)
                    .ToList();

                if (decidedRowIds.Count == 0)
                {
                    (long Id, string SessionId, string ExecutionState, string Action, string CommandType) latestRow = rows[0];
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

                (long Id, string SessionId, string ExecutionState, string Action, string CommandType) targetRow = rows
                    .First(static row => string.Equals(row.ExecutionState, "decided", StringComparison.Ordinal));

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
                            WHEN $execution_state IN ('applied', 'verified', 'accepted') THEN $applied_at_utc
                            ELSE applied_at_utc
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
                      AND execution_state = 'decided'
                      AND (session_id = '' OR session_id = $session_id);
                    """;
                update.Parameters.AddWithValue("$session_id", sessionId);
                update.Parameters.AddWithValue("$execution_state", normalizedResult);
                update.Parameters.AddWithValue("$applied_at_utc", appliedAtUtc);
                update.Parameters.AddWithValue("$error_code", normalizedResult == "failed"
                    ? (ack.ErrorCode?.Trim() ?? string.Empty)
                    : string.Empty);
                update.Parameters.AddWithValue("$agent_instance_id", agentInstanceId);
                update.Parameters.AddWithValue("$intervention_id", interventionId);
                bool updated = update.ExecuteNonQuery() == decidedRowIds.Count;
                if (updated)
                {
                    ApplyInterventionAckSideEffects(
                        connection,
                        tx,
                        targetRow.Action,
                        targetRow.CommandType,
                        ack with { Result = normalizedResult },
                        _runtimePolicyProvider.GetEnvelope());
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
        lock (_gate)
        {
            DateOnly day = dateUtc ?? DateOnly.FromDateTime(DateTime.UtcNow);
            DateTimeOffset startUtc = new(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset endUtc = startUtc.AddDays(1);

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
            dominant_app_hash = derivedSession.DominantAppHash
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
        lock (_gate)
        {
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
    }

    public AgentBrightnessActionStatus ReadBrightnessActionStatus()
    {
        lock (_gate)
        {
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
    }

    public bool ReadDbMigrationFailed()
    {
        lock (_gate)
        {
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
        RuntimePolicyEnvelope? policyEnvelope = null)
    {
        if (!decision.ShouldEmit)
        {
            return false;
        }

        RuntimeBreakPolicySettings breakPolicy = policyEnvelope?.Settings.BreakPolicy
            ?? DefaultBreakPolicySettings();
        string policyVersion = policyEnvelope?.Version ?? "local-default";
        string action = string.IsNullOrWhiteSpace(decision.Action)
            ? "break_suggestion"
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
            policyVersion);
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
            PlannedEndTimestampUtc: finalizeTimestampUtc.AddMinutes(5),
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
        string policyVersion = "local-default")
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
                cooldown_expired = decision.CooldownChecked && decision.Cooldown.CooldownExpired
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
            PlannedEndTimestampUtc: null,
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
        RuntimePolicyEnvelope? policyEnvelope = null)
    {
        RuntimeBreakPolicySettings breakPolicy = policyEnvelope?.Settings.BreakPolicy
            ?? DefaultBreakPolicySettings();
        string? existingJson = ReadRuntimeStateValue(connection, tx, PortaExecutionConstants.BreakPolicy.RuntimeStateKey);
        BreakPolicyCooldownSnapshot existingCooldown = ParseBreakPolicyCooldownSnapshotJson(existingJson, nowUtcMs);
        long cooldownEndTsMs = existingCooldown.CooldownEndTsMs;
        long lastFiredTsMs = 0;
        string lastAction = string.Empty;
        if (decision.ShouldEmit && breakInserted)
        {
            cooldownEndTsMs = checked(nowUtcMs + (breakPolicy.CooldownSeconds * 1000L));
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
                }
            }
            catch
            {
            }
        }

        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.BreakPolicy.ContractVersion,
            break_cooldown_until_utc_ms = cooldownEndTsMs,
            break_last_fired_utc_ms = lastFiredTsMs,
            break_last_action = lastAction,
            short_break_effective_focus_s = focusWindow.ShortBreakWindowEffectiveFocusSeconds,
            long_break_effective_focus_s = focusWindow.LongBreakWindowEffectiveFocusSeconds,
            current_bucket_effective_focus_s = focusWindow.CurrentBucketEffectiveFocusSeconds,
            delivery_boundary_reached = focusWindow.DeliveryBoundaryReached,
            delivery_boundary_kind = focusWindow.DeliveryBoundaryKind ?? string.Empty,
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
                break;
            }

            longBreakWindowEffectiveFocusSeconds += rowEffectiveFocusSeconds;
            if (timestampUtcMs >= earliestShortBreakTimestampUtcMs)
            {
                shortBreakWindowEffectiveFocusSeconds += rowEffectiveFocusSeconds;
            }

            segmentCursorUtcMs = timestampUtcMs;
        }

        return new BreakFocusWindowSnapshot(
            CurrentBucketEffectiveFocusSeconds: currentBucketEffectiveFocusSeconds,
            ShortBreakWindowEffectiveFocusSeconds: shortBreakWindowEffectiveFocusSeconds,
            LongBreakWindowEffectiveFocusSeconds: longBreakWindowEffectiveFocusSeconds,
            DeliveryBoundaryReached: IsBreakDeliveryBoundary(inputBucket, derivedSession, settings),
            DeliveryBoundaryKind: ResolveBreakDeliveryBoundaryKind(inputBucket, derivedSession, settings),
            HighPressure: overloadStabilization.OverloadActive
                || overloadInference.PressureLevel >= settings.HighPressureLevel);
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
        RuntimeBreakPolicySettings? policy = null)
    {
        return !string.IsNullOrWhiteSpace(ResolveBreakDeliveryBoundaryKind(inputBucket, derivedSession, policy));
    }

    private static string ResolveBreakDeliveryBoundaryKind(
        InputBucketRow inputBucket,
        DerivedSessionMetricsResult derivedSession,
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

        if (inputBucket.TypingBurstIntensity <= settings.DeliveryBoundaryTypingQuietMax
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
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryKeyboardBurstQuietMax);
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
        return string.Equals(action, "break_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal);
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
            "fatigue" => "stretch_break_suggestion",
            "overload" => "break_suggestion",
            _ when string.Equals(fallbackAction, "focus_assist_enabled", StringComparison.Ordinal) => "focus_lock_in_suggestion",
            _ when string.Equals(fallbackAction, "fatigue_relief_enabled", StringComparison.Ordinal) => "stretch_break_suggestion",
            _ => "break_suggestion"
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
            _ when string.Equals(fallbackAction, "stretch_break_suggestion", StringComparison.Ordinal)
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

        if (string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal)
            || string.Equals(action, "inform_fatigue_prevention", StringComparison.Ordinal))
        {
            return "fatigue";
        }

        if (string.Equals(action, "break_suggestion", StringComparison.Ordinal)
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

        if (string.Equals(snapshot.LastAction, "break_suggestion", StringComparison.Ordinal))
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

        if (string.Equals(snapshot.LastAction, "stretch_break_suggestion", StringComparison.Ordinal))
        {
            return ("fatigue", snapshot.FatigueProbability);
        }

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

        if (string.Equals(action, "break_suggestion", StringComparison.Ordinal))
        {
            return "Suggest";
        }

        if (string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return "Suggest";
        }

        if (string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal))
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

        if (string.Equals(action, "break_suggestion", StringComparison.Ordinal))
        {
            return 300;
        }

        if (string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal))
        {
            return 300;
        }

        if (string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal))
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

        return action switch
        {
            "break_suggestion" => "Long-break suggestion displayed to user (legacy break_suggestion transport action; no forced system restriction).",
            "focus_lock_in_suggestion" => "Focus lock-in suggestion displayed to user.",
            "stretch_break_suggestion" => "Short-break suggestion displayed to user (legacy stretch_break_suggestion transport action).",
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

        return action switch
        {
            "break_suggestion" or "focus_lock_in_suggestion" or "stretch_break_suggestion"
                => "Dismiss the suggestion overlay or wait for auto-expiry.",
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
            reversal_applied = controlEvent.ReversalApplied
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
        return normalized switch
        {
            "break_suggestion" => "show_break_toast",
            "focus_lock_in_suggestion" => "show_focus_lock_in_toast",
            "stretch_break_suggestion" => "show_stretch_break_toast",
            "inform_overload_risk" => "show_status_toast",
            "inform_focus_protection" => "show_status_toast",
            "inform_fatigue_prevention" => "show_status_toast",
            "focus_mode_enabled" => "show_status_toast",
            "focus_assist_enabled" => "show_status_toast",
            "fatigue_relief_enabled" => "show_status_toast",
            "temporary_app_restriction_enabled" => "show_status_toast",
            "overload_brightness_reduced" => "show_status_toast",
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
            confidence = controlEvent.Confidence,
            policy_id = controlEvent.PolicyId,
            domain = controlEvent.Domain,
            intervention_id = controlEvent.InterventionId
        });
    }

    private static string ResolveInterventionToastTitle(string action)
    {
        // Step 2 migration map:
        // Keep legacy action IDs in transport, but surface canonical short-break and
        // long-break naming in user-facing titles during migration.
        return action switch
        {
            "break_suggestion" => "Long break",
            "focus_lock_in_suggestion" => "Focus suggestion",
            "stretch_break_suggestion" => "Short break",
            "inform_overload_risk" => "Overload watch",
            "inform_focus_protection" => "Focus watch",
            "inform_fatigue_prevention" => "Fatigue watch",
            "focus_mode_enabled" or "focus_assist_enabled" => "Focus guidance",
            "fatigue_relief_enabled" => "Fatigue guidance",
            "temporary_app_restriction_enabled" => "Temporary app limit",
            "deep_work_notifications_suppressed" => "Deep work notifications",
            "deep_work_notifications_restored" => "Notifications restored",
            "overload_brightness_reduced" => "Brightness reduced",
            "overload_brightness_restored" => "Brightness restored",
            "overload_recovered" => "Overload recovered",
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
            "break_suggestion"
                => "Ari sees a long work run and rising strain. Ready for a long break now? Choose Skip or Snooze if now is not a good time, or turn off this tip.",
            "focus_lock_in_suggestion"
                => "Ari sees steady focus building and thinks staying on one task will help. Keep this task in front of you for a bit? Choose Later if not now, or turn off this tip.",
            "stretch_break_suggestion"
                => "Ari sees a long steady run and thinks a short recovery break could reduce strain. Take a short break now? Choose Skip or Snooze if not now, or turn off this tip.",
            "inform_overload_risk"
                => "Ari sees overload rising, but the signal has not stayed high long enough to change anything yet. Slow down or open Now for details. You can mute this guidance in Settings.",
            "inform_focus_protection"
                => "Ari sees focus building, but it is only watching for interruptions right now. Stay on one task or open Now for details. You can mute this guidance in Settings.",
            "inform_fatigue_prevention"
                => "Ari sees fatigue building, but it has not stepped in yet. Take a quick reset or open Now for details. You can mute this guidance in Settings.",
            "focus_mode_enabled"
                => "Ari saw overload stay elevated long enough to turn on focus guidance for this block. Keep one task in front of you or open Settings to change this feature. You can turn it off in Settings.",
            "focus_assist_enabled"
                => "Ari saw steady focus long enough to turn on focus guidance for this block. Keep distractions low or open Settings to change this feature. You can turn it off in Settings.",
            "fatigue_relief_enabled"
                => "Ari saw fatigue stay elevated long enough to turn on fatigue relief guidance. Slow down or take a quick reset, or open Settings to change this feature. You can turn it off in Settings.",
            "temporary_app_restriction_enabled"
                => "Ari saw the risk stay high long enough to limit distracting apps for a short block. Stay on task or open Settings to change this feature. You can turn it off in Settings.",
            "deep_work_notifications_suppressed"
                => "Ari detected a sustained focus block and muted Windows notification banners to reduce interruptions. Keep this block going or restore notifications when you are ready. You can turn this feature off in Settings.",
            "deep_work_notifications_restored"
                => "Ari saw the focus block end and restored your Windows notification settings. No action is needed, or open Settings to change this feature. You can turn this feature off in Settings.",
            "overload_brightness_reduced"
                => "Ari saw overload stay high and lowered screen brightness to reduce strain. Keep it if helpful or restore brightness if not. You can turn this feature off in Settings.",
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
                => "Ari saw overload drop back to normal and returned to monitoring. No action is needed, or open Now for details. You can mute recovery updates in Settings.",
            _ when !string.IsNullOrWhiteSpace(controlEvent.WhatChanged)
                => controlEvent.WhatChanged!,
            _ => "Ari recorded a new state change."
        };
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
            "accepted" => "accepted",
            "dismissed" => "dismissed",
            "snoozed" => "snoozed",
            "suppressed_by_preference" => "suppressed_by_preference",
            _ => string.Empty
        };
    }

    private static string ResolveInterventionTipKey(string action)
    {
        return action switch
        {
            "break_suggestion" => "break_suggestion",
            "focus_lock_in_suggestion" => "focus_lock_in_suggestion",
            "stretch_break_suggestion" => "stretch_break_suggestion",
            _ => string.Empty
        };
    }

    private static string ResolveInterventionInteractionKind(string action)
    {
        return action switch
        {
            "break_suggestion" or "focus_lock_in_suggestion" or "stretch_break_suggestion" => "choice_prompt",
            _ => "status"
        };
    }

    private static string ResolveInterventionAcceptLabel(string action)
    {
        return action switch
        {
            "break_suggestion" => "Take break",
            "focus_lock_in_suggestion" => "Yes",
            "stretch_break_suggestion" => "Take break",
            _ => string.Empty
        };
    }

    private static string ResolveInterventionDismissLabel(string action)
    {
        return action switch
        {
            "break_suggestion" or "stretch_break_suggestion" => "Skip",
            "focus_lock_in_suggestion" => "No",
            _ => string.Empty
        };
    }

    private static string ResolveInterventionSnoozeLabel(string action)
    {
        return action switch
        {
            "break_suggestion" or "stretch_break_suggestion" => "Snooze",
            "focus_lock_in_suggestion" => "Later",
            _ => string.Empty
        };
    }

    private static string ResolveInterventionSuppressLabel(string action)
    {
        return action switch
        {
            "break_suggestion" or "focus_lock_in_suggestion" or "stretch_break_suggestion" => "Turn off this tip",
            _ => string.Empty
        };
    }

    private static int ResolveInterventionPromptDurationSeconds(AgentControlEvent controlEvent, string action)
    {
        return action switch
        {
            "break_suggestion" or "focus_lock_in_suggestion" or "stretch_break_suggestion"
                => Math.Clamp(controlEvent.DurationSeconds ?? 120, 30, 300),
            _ => controlEvent.DurationSeconds ?? 8
        };
    }

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

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            DELETE FROM input_signal_windows WHERE window_end_utc < $cutoff;
            DELETE FROM input_buckets WHERE timestamp_utc < $cutoff;
            DELETE FROM state_buckets WHERE timestamp_utc < $cutoff;
            DELETE FROM events WHERE timestamp_utc < $cutoff;
            DELETE FROM system_changes WHERE timestamp_utc < $cutoff;
            DELETE FROM consent WHERE timestamp_utc < $cutoff;
            DELETE FROM settings WHERE timestamp_utc < $cutoff;
            DELETE FROM interventions WHERE timestamp_utc < $cutoff;
            DELETE FROM diagnostics_snapshots WHERE timestamp_utc < $cutoff;
            DELETE FROM brain_snapshots WHERE timestamp_utc < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoffRaw);
        cmd.ExecuteNonQuery();

        _lastPruneUtc = nowUtc;
    }

    private void EnsureSchema()
    {
        CoreStateDbStartup.InitializeAndMigrate(logPragmasAtBoot: true, dbPathOverride: _dbPath);
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
            StateFlagsJson: ReadStringOrFallback(reader, 25, "{\"schema_ver\":1}"),
            CommandType: ReadStringOrFallback(reader, 26, string.Empty),
            ExecutionState: ReadStringOrFallback(reader, 27, "decision_only"),
            DecidedAtUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 28, string.Empty)),
            AppliedAtUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 29, string.Empty)),
            AckedAtUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 30, string.Empty)),
            ExecutionErrorCode: ReadStringOrFallback(reader, 31, string.Empty),
            ExecutorAgentInstanceId: ReadStringOrFallback(reader, 32, string.Empty));
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
}

