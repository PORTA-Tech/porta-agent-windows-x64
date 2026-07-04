using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

public sealed class CurrentSnapshotSourceOfTruthTests
{
    [Fact]
    public void ReadLatestSnapshot_DerivesStateFromFinalizedSignalBucketInsteadOfLegacySnapshotRow()
    {
        using var env = new SnapshotSourceOfTruthTestEnvironment();
        SeedFinalizedBucket(env, action: "maintain_focus");

        // Reproduce a database upgraded from v1: a legacy brain_snapshots row exists alongside the
        // freshly finalized v2 signal bucket. The live finalizer runtime no longer writes
        // brain_snapshots and direct writes are blocked, so seed the legacy row through the finalizer
        // write guard — the only sanctioned path — to recreate the upgrade scenario this test guards.
        SeedLegacySnapshotRow(env, action: "pre_v2_action");

        StateBucketObservation stateBucket = ReadLatestStateBucket(env.DbPath);
        TamperLegacySnapshotRow(env.DbPath, stateBucket.TimestampUtc);

        AgentBrainSnapshot? snapshotCandidate = env.InnerStore.ReadLatestSnapshot();
        Assert.NotNull(snapshotCandidate);
        AgentBrainSnapshot snapshot = snapshotCandidate!;
        SignalDrivenStateExpectation expected = ComputeExpectedSignalState(
            stateBucket.TypingSmoothed,
            stateBucket.FocusSmoothed,
            stateBucket.OverloadLikelihood);

        Assert.Equal(stateBucket.TimestampUtc, snapshot.TimestampUtc);
        AssertClose(stateBucket.OverloadLikelihood, snapshot.OverloadLikelihood);
        AssertClose(stateBucket.Confidence, snapshot.Confidence);
        AssertClose(expected.FocusStrength, snapshot.FocusStrength);
        AssertClose(expected.FatigueProbability, snapshot.FatigueProbability);
        AssertClose(expected.CognitiveStability, snapshot.CognitiveStability);
        Assert.Equal(expected.DominantStateName, snapshot.DominantStateName);
        AssertClose(expected.DominantStateValue, snapshot.DominantStateValue);
        AssertClose(stateBucket.SustainedAttentionDurationSec, snapshot.SustainedAttentionDurationSec);
        AssertClose(stateBucket.IdleFragmentationIndex, snapshot.IdleFragmentationIndex);
        Assert.Equal("tampered_action", snapshot.LastAction);
        Assert.Equal(stateBucket.StateFlagsJson, snapshot.StateFlagsJson);
        Assert.Equal(stateBucket.ContributorsJson, snapshot.ContributorsJson);
        Assert.NotEqual(0.99d, snapshot.FocusStrength);
        Assert.NotEqual(0.01d, snapshot.Confidence);
    }

    [Fact]
    public void RefreshRuntimeStateCache_UpdatesControllerCurrentSnapshotFromStore()
    {
        using var env = new SnapshotSourceOfTruthTestEnvironment();
        AgentBrainSnapshot initial = env.Controller.GetCurrentSnapshot();
        Assert.Equal("starting", initial.LastAction);

        SeedFinalizedBucket(env, action: "maintain_focus");
        AgentBrainSnapshot? persistedCandidate = env.InnerStore.ReadLatestSnapshot();
        Assert.NotNull(persistedCandidate);
        AgentBrainSnapshot persisted = persistedCandidate!;

        env.Controller.RefreshRuntimeStateCache();

        AgentBrainSnapshot current = env.Controller.GetCurrentSnapshot();
        Assert.Equal(persisted.TimestampUtc, current.TimestampUtc);
        AssertClose(persisted.FocusStrength, current.FocusStrength);
        AssertClose(persisted.OverloadLikelihood, current.OverloadLikelihood);
        AssertClose(persisted.FatigueProbability, current.FatigueProbability);
        AssertClose(persisted.CognitiveStability, current.CognitiveStability);
        AssertClose(persisted.Confidence, current.Confidence);
        Assert.Equal(persisted.DominantStateName, current.DominantStateName);
        Assert.Equal(persisted.LastAction, current.LastAction);
    }

    private static void SeedFinalizedBucket(SnapshotSourceOfTruthTestEnvironment env, string action)
    {
        env.StateStore.WriteSnapshot(CreateSyntheticSnapshot(env.SignalTimestampUtc, action));
        env.StateStore.IngestInputSignals(CreateInputSignals(env.WindowStartUtc, env.WindowEndUtc));
        env.StateStore.RunFinalizerTick(env.FinalizeAtUtc);
    }

    private static AgentBrainSnapshot CreateSyntheticSnapshot(DateTimeOffset timestampUtc, string action)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: timestampUtc,
            TypingBurstIntensity: 0.92d,
            ScrollVelocityIndex: 0.88d,
            FocusSwitchRate: 0.04d,
            SmoothedTypingBurstIntensity: 0.92d,
            SmoothedScrollVelocityIndex: 0.88d,
            SmoothedFocusSwitchRate: 0.04d,
            FocusStrength: 0.97d,
            OverloadLikelihood: 0.93d,
            FatigueProbability: 0.96d,
            CognitiveStability: 0.02d,
            DominantStateValue: 0.97d,
            DominantStateName: "focus",
            Confidence: 0.94d,
            LastAction: action,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["synthetic_worker"]);
    }

    private static InputSignalsPacket CreateInputSignals(DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc)
    {
        return new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-live-1",
            SessionId: "session-live-1",
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: 6,
            KeyboardBackspaceCount: 1,
            MouseWheelDelta: 72,
            MouseMoveDx: 80d,
            MouseMoveDy: 24d,
            FocusAppHash: "ABC12345",
            IdleSeconds: 0.35d,
            LastInputEventUtc: windowEndUtc.AddMilliseconds(-150),
            KeyboardIkiMeanMs: 118d,
            KeyboardIkiStdMs: 15d,
            KeyboardIkiCv: 0.13d,
            KeyboardBurstCount: 2,
            KeyboardBurstRate: 0.4d,
            KeyboardCorrectionSpikeCount: 1,
            KeyboardCorrectionSpikeRate: 0.2d,
            MouseMoveSampleCount: 10,
            MousePathLengthPx: 180d,
            MouseSpeedMeanPxPerSec: 36d,
            MouseJerkP95: 54d,
            MouseMicroCorrectionCount: 2,
            MouseMicroCorrectionRate: 0.4d,
            MouseWheelEventCount: 2,
            MouseScrollBurstCount: 1,
            MouseScrollBurstRate: 0.2d,
            MouseScrollSpeedMean: 120d,
            FocusObservationCount: 4,
            FocusSwitchCount: 1,
            FocusActiveAppCount: 1,
            FocusAppEntropy: 0.1d,
            IdleObservationCount: 4,
            IdleSecondsWithinWindow: 0.35d,
            MicroIdleCount: 1,
            MicroIdleSeconds: 0.15d,
            BreakSecondsWithinWindow: 0d,
            ShortIdleBoundaryObserved: false,
            NotificationToastsEnabled: true);
    }

    private static StateBucketObservation ReadLatestStateBucket(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                timestamp_utc,
                typing_burst_intensity_smoothed,
                focus_switch_rate_smoothed,
                overload_likelihood,
                confidence,
                sustained_attention_duration_sec,
                idle_fragmentation_index,
                state_flags_json,
                contributors_json
            FROM state_buckets
            ORDER BY timestamp_utc DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return new StateBucketObservation(
            TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
            TypingSmoothed: reader.GetDouble(1),
            FocusSmoothed: reader.GetDouble(2),
            OverloadLikelihood: reader.GetDouble(3),
            Confidence: reader.GetDouble(4),
            SustainedAttentionDurationSec: reader.GetDouble(5),
            IdleFragmentationIndex: reader.GetDouble(6),
            StateFlagsJson: reader.GetString(7),
            ContributorsJson: reader.GetString(8));
    }

    private static void SeedLegacySnapshotRow(SnapshotSourceOfTruthTestEnvironment env, string action)
    {
        // A v1 database left standalone brain_snapshots rows on disk. v2 forbids creating them through
        // any sanctioned API (direct writes are blocked, and persistence requires a finalized input
        // bucket row), so reproduce the v1-leftover row with a raw INSERT — the same low-level technique
        // this fixture already uses to tamper the row — placing it one second into the bucket window.
        using var connection = new SqliteConnection($"Data Source={env.DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO brain_snapshots (
                timestamp_utc, typing_burst_intensity, scroll_velocity_index, focus_switch_rate,
                focus_strength, overload_likelihood, fatigue_probability, cognitive_stability,
                confidence, last_action, contributors_json
            ) VALUES (
                $ts, 0.92, 0.88, 0.04,
                0.97, 0.93, 0.96, 0.02,
                0.94, $last_action, '["synthetic_worker"]'
            );
            """;
        cmd.Parameters.AddWithValue("$ts", env.SignalTimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$last_action", action);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static void TamperLegacySnapshotRow(string dbPath, DateTimeOffset bucketStartUtc)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        // Production associates a state bucket with the legacy brain_snapshots row whose timestamp
        // falls within [bucketStart, bucketStart + bucketSeconds) — see
        // SqliteBrainStateStore.TryReadLatestLegacySnapshotMetadataForBucket. The seeded snapshot sits
        // one second into that window, so tamper by the same window predicate rather than an exact
        // timestamp match (which targets the bucket start and updates zero rows).
        cmd.CommandText =
            """
            UPDATE brain_snapshots
            SET
                focus_strength = 0.99,
                overload_likelihood = 0.99,
                fatigue_probability = 0.99,
                cognitive_stability = 0.01,
                dominant_state_name = 'overload',
                dominant_state_value = 0.99,
                confidence = 0.01,
                last_action = 'tampered_action'
            WHERE timestamp_utc >= $bucket_start_utc AND timestamp_utc < $bucket_end_utc;
            """;
        cmd.Parameters.AddWithValue("$bucket_start_utc", bucketStartUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$bucket_end_utc", bucketStartUtc.AddSeconds(5).ToString("O"));
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static SignalDrivenStateExpectation ComputeExpectedSignalState(
        double typingSmoothed,
        double focusSmoothed,
        double overloadLikelihood)
    {
        double safeTyping = ClampUnitInterval(typingSmoothed);
        double safeFocusSmoothed = ClampUnitInterval(focusSmoothed);
        double safeOverloadLikelihood = ClampUnitInterval(overloadLikelihood);
        double focusStrength = ClampUnitInterval(
            (safeTyping * 0.5d)
            + ((1d - safeFocusSmoothed) * 0.3d)
            + ((1d - safeOverloadLikelihood) * 0.2d));
        double fatigueProbability = ClampUnitInterval(
            (safeOverloadLikelihood * 0.6d)
            + ((1d - focusStrength) * 0.4d));
        double cognitiveStability = ClampUnitInterval(1d - Math.Abs(safeOverloadLikelihood - focusStrength));
        double dominantStateValue = Math.Max(focusStrength, Math.Max(safeOverloadLikelihood, fatigueProbability));
        string dominantStateName = dominantStateValue == focusStrength
            ? "focus"
            : dominantStateValue == safeOverloadLikelihood
                ? "overload"
                : "fatigue";

        return new SignalDrivenStateExpectation(
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

    private static void AssertClose(double expected, double actual)
    {
        Assert.InRange(actual - expected, -0.000001d, 0.000001d);
    }

    private sealed class SnapshotSourceOfTruthTestEnvironment : IDisposable
    {
        private readonly string _tempDirectory;

        public SnapshotSourceOfTruthTestEnvironment()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "porta-current-snapshot-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            DbPath = Path.Combine(_tempDirectory, "core-state.db");

            InnerStore = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: DbPath);
            StateStore = new BucketFinalizerStateStore(InnerStore, () => FinalizeAtUtc);
            Controller = new AgentCoreController(StateStore);
        }

        public string DbPath { get; }

        public SqliteBrainStateStore InnerStore { get; }

        public BucketFinalizerStateStore StateStore { get; }

        public AgentCoreController Controller { get; }

        public DateTimeOffset WindowStartUtc { get; } = new(2026, 04, 04, 10, 0, 0, TimeSpan.Zero);

        public DateTimeOffset WindowEndUtc { get; } = new(2026, 04, 04, 10, 0, 5, TimeSpan.Zero);

        public DateTimeOffset SignalTimestampUtc { get; } = new(2026, 04, 04, 10, 0, 1, TimeSpan.Zero);

        public DateTimeOffset FinalizeAtUtc { get; } = new(2026, 04, 04, 10, 0, 8, TimeSpan.Zero);

        public void Dispose()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private readonly record struct StateBucketObservation(
        DateTimeOffset TimestampUtc,
        double TypingSmoothed,
        double FocusSmoothed,
        double OverloadLikelihood,
        double Confidence,
        double SustainedAttentionDurationSec,
        double IdleFragmentationIndex,
        string StateFlagsJson,
        string ContributorsJson);

    private readonly record struct SignalDrivenStateExpectation(
        double FocusStrength,
        double FatigueProbability,
        double CognitiveStability,
        string DominantStateName,
        double DominantStateValue);
}
