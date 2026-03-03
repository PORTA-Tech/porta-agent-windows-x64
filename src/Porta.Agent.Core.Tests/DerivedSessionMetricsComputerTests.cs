using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class DerivedSessionMetricsComputerTests
{
    [Fact]
    public void ComputeForFinalizedBucket_StableContext_AccumulatesByOneBucket()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 10d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow();
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(15d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal(string.Empty, result.SaFreezeReason);
        Assert.False(result.SaUntrusted);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ColdStart_QualityDecentAccumulatesAsUntrusted()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 25d,
            idleFragmentationIndex: 0.2d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow();
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Cold);

        Assert.Equal(30d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal(string.Empty, result.SaFreezeReason);
        Assert.True(result.SaUntrusted);
    }

    [Fact]
    public void ComputeForFinalizedBucket_UsesIdleFragmentationFormula()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        InputBucketRow row = BuildStableRow() with
        {
            IdleMicroIdleCount = 3d,
            IdleMicroIdleSeconds = 2.5d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        // raw = 0.5*(3/3) + 0.5*((2.5/5)/0.6) = 0.916666...
        // smoothed = 0.35*raw + 0.65*prev(0) = 0.320833...
        Assert.Equal(0.320833d, result.IdleFragmentationIndex, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_CorruptRuntimeState_UsesZeroBaseline()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertRawRuntimeState(
            connection,
            tx,
            PortaExecutionConstants.DerivedSessionMetrics.RuntimeStateKey,
            "{\"schema_ver\":1,\"sustained_attention_duration_sec\":\"bad\"}");

        InputBucketRow row = BuildStableRow();
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(5d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal(string.Empty, result.SaFreezeReason);
        Assert.False(result.SaUntrusted);
    }

    [Fact]
    public void ComputeForFinalizedBucket_MissingData_MarksPartialAndDecays()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.4d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            Partial = 1
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: false);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.True(result.Partial);
        Assert.Equal(18d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal(0.33d, result.IdleFragmentationIndex, precision: 6);
        Assert.Equal("partial_or_missing", result.SaFreezeReason);
        Assert.False(result.SaUntrusted);
    }

    [Fact]
    public void ComputeForFinalizedBucket_IdleChannelMissing_DecaysIdleFragmentation()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.4d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            Partial = 1,
            ContributorsJson = "{\"schema_ver\":1,\"missing_major_channels\":[\"idle\"]}"
        };

        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.True(result.Partial);
        Assert.Equal(0.36d, result.IdleFragmentationIndex, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ContextChanged_DoesNotAccumulate()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.4d,
            saContextAppHash: "app_hash_other",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            DominantAppHash = "app_hash_work"
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(0d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("context_changed", result.SaFreezeReason);
        Assert.Equal("app_hash_work", result.DominantAppHash);
        Assert.False(result.SaUntrusted);
    }

    [Fact]
    public void ComputeForFinalizedBucket_EntropySpike_UsesSpikeFreezeReason()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.4d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            FocusAppEntropy = 2.10d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(0d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("entropy_spike", result.SaFreezeReason);
    }

    [Fact]
    public void ComputeForFinalizedBucket_FocusSwitchSpike_ResetsSustainedAttention()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.4d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            FocusSwitchCount = 3
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(0d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("focus_switch_spike", result.SaFreezeReason);
    }

    [Fact]
    public void ComputeForFinalizedBucket_QualityLow_HoldsSustainedAttention()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.4d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            QualityScore = 0.6d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Cold);

        Assert.Equal(20d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("quality_low", result.SaFreezeReason);
        Assert.True(result.SaUntrusted);
    }

    [Fact]
    public void Acceptance_StableContextForThreeBuckets_AccumulatesToFifteenSeconds()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 0d,
            idleFragmentationIndex: 0d,
            saContextAppHash: string.Empty,
            saLastUpdateBucketSeq: 0);

        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = default;

        for (int i = 1; i <= 3; i++)
        {
            InputBucketRow row = BuildStableRow() with
            {
                BucketSeq = i,
                TimestampUtc = Time.toIsoUtcMsString(Time.epoch_anchor_ms + (i * Time.bucket_ms)),
                StartUtc = Time.toIsoUtcMsString(Time.epoch_anchor_ms + (i * Time.bucket_ms))
            };
            result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
                connection,
                tx,
                row,
                primitive,
                BaselineGatingResult.Warm);

            PersistResultAsRuntimeState(connection, tx, row.BucketSeq, result);
        }

        Assert.Equal(15d, result.SustainedAttentionDurationSec, precision: 6);
    }

    [Fact]
    public void Acceptance_TrueBreakBucket_ResetsSustainedAttention()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.4d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            TrueBreakFlag = 1,
            TrueBreakSeconds = 130d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(0d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("true_break", result.SaFreezeReason);
    }

    [Fact]
    public void Acceptance_MicroIdlePattern_IfiRisesThenFalls()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 0d,
            idleFragmentationIndex: 0d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);

        InputBucketRow highMicroIdle = BuildStableRow() with
        {
            BucketSeq = 1,
            IdleMicroIdleCount = 3d,
            IdleMicroIdleSeconds = 2.5d
        };
        DerivedSessionMetricsResult rising = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            highMicroIdle,
            primitive,
            BaselineGatingResult.Warm);
        PersistResultAsRuntimeState(connection, tx, highMicroIdle.BucketSeq, rising);

        InputBucketRow noMicroIdle = BuildStableRow() with
        {
            BucketSeq = 2,
            IdleMicroIdleCount = 0d,
            IdleMicroIdleSeconds = 0d
        };
        DerivedSessionMetricsResult falling = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            noMicroIdle,
            primitive,
            BaselineGatingResult.Warm);

        Assert.True(rising.IdleFragmentationIndex > 0d);
        Assert.True(falling.IdleFragmentationIndex < rising.IdleFragmentationIndex);
    }

    private static PrimitiveComputationResult BuildPrimitive(bool focusAvailable)
    {
        return new PrimitiveComputationResult(
            TypingRaw: 0.3d,
            ScrollRaw: 0.4d,
            FocusRaw: 0.5d,
            TypingSmoothed: 0.3d,
            ScrollSmoothed: 0.4d,
            FocusSmoothed: 0.5d,
            TypingChannelAvailable: true,
            ScrollChannelAvailable: true,
            FocusChannelAvailable: focusAvailable);
    }

    private static InputBucketRow BuildStableRow()
    {
        return new InputBucketRow(
            BucketSeq: 1,
            TimestampUtc: "2026-03-03T00:00:05.000Z",
            StartUtc: "2026-03-03T00:00:05.000Z",
            TypingBurstIntensity: 0.2d,
            ScrollVelocityIndex: 0.3d,
            FocusSwitchRate: 0.10d,
            KeyboardIkiMean: 120d,
            KeyboardIkiCv: 0.2d,
            KeyboardBurstRate: 0.4d,
            KeyboardBackspaceRate: 0.1d,
            MouseSpeedMean: 150d,
            MouseJerkP95: 1000d,
            MouseScrollSpeed: 80d,
            MouseMicroCorrectionRate: 0.1d,
            FocusAppEntropy: 1.0d,
            FocusSwitchCount: 1,
            DominantAppHash: "app_hash_work",
            FocusActiveAppCount: 2d,
            IdleSeconds: 0.2d,
            IdleMicroIdleCount: 1d,
            IdleMicroIdleSeconds: 0.2d,
            TrueBreakSeconds: 0d,
            TrueBreakFlag: 0,
            QualityScore: 0.95d,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
    }

    private static SqliteConnection OpenInMemoryDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE runtime_state (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static void InsertDerivedSessionState(
        SqliteConnection connection,
        SqliteTransaction tx,
        double sustainedAttentionDurationSec,
        double idleFragmentationIndex,
        string saContextAppHash,
        long saLastUpdateBucketSeq,
        string saFreezeReason = "")
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = "v1",
            sa_seconds = sustainedAttentionDurationSec,
            sa_context_app_hash = saContextAppHash,
            sa_last_update_bucket_seq = saLastUpdateBucketSeq,
            sa_freeze_reason = saFreezeReason,
            sustained_attention_duration_sec = sustainedAttentionDurationSec,
            idle_fragmentation_index = idleFragmentationIndex,
            dominant_app_hash = saContextAppHash,
            bucket_seq = saLastUpdateBucketSeq
        });
        InsertRawRuntimeState(connection, tx, PortaExecutionConstants.DerivedSessionMetrics.RuntimeStateKey, valueJson);
    }

    private static void InsertRawRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        string valueJson)
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
        cmd.Parameters.AddWithValue("$updated_utc", "2026-03-03T00:00:00.000Z");
        cmd.ExecuteNonQuery();
    }

    private static void PersistResultAsRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx,
        long bucketSeq,
        DerivedSessionMetricsResult result)
    {
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: result.SustainedAttentionDurationSec,
            idleFragmentationIndex: result.IdleFragmentationIndex,
            saContextAppHash: result.DominantAppHash,
            saLastUpdateBucketSeq: bucketSeq,
            saFreezeReason: result.SaFreezeReason);
    }
}
