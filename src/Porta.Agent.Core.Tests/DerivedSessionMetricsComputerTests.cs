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
        Assert.Equal(15d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("keep", result.ContinuityState);
        Assert.Equal(1d, result.TaskContinuityScore, precision: 6);
        Assert.Equal(string.Empty, result.SaFreezeReason);
        Assert.False(result.SaUntrusted);
    }

    [Fact]
    public void ComputeForFinalizedBucket_StableContext_SettlesBeforeStartingFocusEpisode()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 0d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow();
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);

        DerivedSessionMetricsResult first = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);
        PersistResultAsRuntimeState(connection, tx, row.BucketSeq, first);

        DerivedSessionMetricsResult second = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row with { BucketSeq = 2 },
            primitive,
            BaselineGatingResult.Warm);
        PersistResultAsRuntimeState(connection, tx, 2, second);

        DerivedSessionMetricsResult third = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row with { BucketSeq = 3 },
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(0d, first.TaskContinuitySeconds, precision: 6);
        Assert.Equal("pause", first.ContinuityState);
        Assert.Equal("focus_settling", first.SaFreezeReason);
        Assert.Equal(5d, first.FocusEpisodeCandidateSeconds, precision: 6);

        Assert.Equal(0d, second.TaskContinuitySeconds, precision: 6);
        Assert.Equal("pause", second.ContinuityState);
        Assert.Equal("focus_settling", second.SaFreezeReason);
        Assert.Equal(10d, second.FocusEpisodeCandidateSeconds, precision: 6);

        Assert.Equal(15d, third.TaskContinuitySeconds, precision: 6);
        Assert.Equal("keep", third.ContinuityState);
        Assert.Equal(string.Empty, third.SaFreezeReason);
        Assert.Equal(0d, third.FocusEpisodeCandidateSeconds, precision: 6);
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
            BaselineGatingResult.Cold(guidanceEnabled: true));

        Assert.Equal(30d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("keep", result.ContinuityState);
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

        Assert.Equal(0d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("focus_settling", result.SaFreezeReason);
        Assert.Equal(5d, result.FocusEpisodeCandidateSeconds, precision: 6);
        Assert.False(result.SaUntrusted);
    }

    [Fact]
    public void ComputeForFinalizedBucket_FocusMissing_PausesContinuityAndMarksPartial()
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
        Assert.Equal(20d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal(0.33d, result.IdleFragmentationIndex, precision: 6);
        Assert.Equal("pause", result.ContinuityState);
        Assert.True(result.ContinuityConfidence < 0.7d);
        Assert.Equal("focus_unavailable", result.SaFreezeReason);
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
    public void ComputeForFinalizedBucket_ContextChanged_HoldsDuringGraceWindow()
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

        Assert.Equal(20d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("pause", result.ContinuityState);
        Assert.Equal("context_excursion", result.SaFreezeReason);
        Assert.Equal("app_hash_other", result.DominantAppHash);
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
            FocusAppEntropy = 2.60d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(15d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("decay", result.ContinuityState);
        Assert.Equal("focus_instability", result.SaFreezeReason);
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
            FocusSwitchCount = 5
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(15d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("decay", result.ContinuityState);
        Assert.Equal("focus_instability", result.SaFreezeReason);
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
            QualityScore = 0.55d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Cold(guidanceEnabled: true));

        Assert.Equal(25d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("keep", result.ContinuityState);
        Assert.Equal(string.Empty, result.SaFreezeReason);
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
    public void Acceptance_LongBreakBucket_ResetsSustainedAttention()
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
            TrueBreakSeconds = 305d,
            IdleSeconds = 305d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(focusAvailable: true);
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            primitive,
            BaselineGatingResult.Warm);

        Assert.Equal(0d, result.SustainedAttentionDurationSec, precision: 6);
        Assert.Equal("reset", result.ContinuityState);
        Assert.Equal("long_break", result.SaFreezeReason);
    }

    [Fact]
    public void Acceptance_PassiveViewingBuckets_DoNotAccumulateFocusContinuity()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 0d,
            idleFragmentationIndex: 0d,
            saContextAppHash: "app_hash_video",
            saLastUpdateBucketSeq: 0);

        DerivedSessionMetricsResult result = default;
        for (int i = 1; i <= 12; i++)
        {
            InputBucketRow passiveRow = BuildStableRow() with
            {
                BucketSeq = i,
                TimestampUtc = Time.toIsoUtcMsString(Time.epoch_anchor_ms + (i * Time.bucket_ms)),
                StartUtc = Time.toIsoUtcMsString(Time.epoch_anchor_ms + (i * Time.bucket_ms)),
                DominantAppHash = "app_hash_video",
                KeyboardIkiMean = 0d,
                KeyboardIkiCv = 0d,
                KeyboardBurstRate = 0d,
                KeyboardBackspaceRate = 0d,
                ScrollVelocityIndex = 0d,
                MouseSpeedMean = 0d,
                MouseJerkP95 = 0d,
                MouseScrollSpeed = 0d,
                MouseMicroCorrectionRate = 0d,
                IdleSeconds = 0.2d,
                IdleMicroIdleCount = 0d,
                IdleMicroIdleSeconds = 0d
            };

            result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
                connection,
                tx,
                passiveRow,
                BuildPrimitive(focusAvailable: true),
                BaselineGatingResult.Warm);

            PersistResultAsRuntimeState(connection, tx, passiveRow.BucketSeq, result);
        }

        Assert.Equal(0d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("pause", result.ContinuityState);
    }

    [Fact]
    public void ComputeForFinalizedBucket_LongBucketGap_ResetsStaleContinuityBeforeNewActivity()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        const long previousBucketSeq = 100;
        const int sevenHoursSeconds = 7 * 60 * 60;
        long resumeBucketSeq = previousBucketSeq + (sevenHoursSeconds / Time.bucketSeconds) + 1;
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 600d,
            idleFragmentationIndex: 0.2d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: previousBucketSeq);

        InputBucketRow resumedRow = BuildStableRow() with
        {
            BucketSeq = resumeBucketSeq,
            TimestampUtc = Time.toIsoUtcMsString(Time.epoch_anchor_ms + (resumeBucketSeq * Time.bucket_ms)),
            StartUtc = Time.toIsoUtcMsString(Time.epoch_anchor_ms + (resumeBucketSeq * Time.bucket_ms))
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            resumedRow,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        Assert.Equal(0d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("pause", result.ContinuityState);
        Assert.Equal("focus_settling", result.SaFreezeReason);
        Assert.Equal(Time.bucketSeconds, result.FocusEpisodeCandidateSeconds, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_KeyboardOnly_MaintainsContinuityAndLowersConfidence()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            ScrollVelocityIndex = 0d,
            MouseSpeedMean = 0d,
            MouseJerkP95 = 0d,
            MouseScrollSpeed = 0d,
            MouseMicroCorrectionRate = 0d,
            ContributorsJson = "{\"schema_ver\":1,\"missing_major_channels\":[\"mouse\"]}"
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        Assert.False(result.Partial);
        Assert.Equal(25d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("keep", result.ContinuityState);
        Assert.True(result.ContinuityConfidence < 0.95d);
    }

    [Fact]
    public void ComputeForFinalizedBucket_MouseOnly_MaintainsContinuityAndLowersConfidence()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 20d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0);

        InputBucketRow row = BuildStableRow() with
        {
            KeyboardIkiMean = 0d,
            KeyboardIkiCv = 0d,
            KeyboardBurstRate = 0d,
            KeyboardBackspaceRate = 0d,
            ContributorsJson = "{\"schema_ver\":1,\"missing_major_channels\":[\"keyboard\"]}"
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        Assert.False(result.Partial);
        Assert.Equal(25d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("keep", result.ContinuityState);
        Assert.True(result.ContinuityConfidence < 0.95d);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ThirtySecondThinkingPause_PausesWithoutReset()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 120d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0,
            continuityIdlePauseSeconds: 0d);

        InputBucketRow row = BuildStableRow() with
        {
            IdleSeconds = 30d,
            KeyboardIkiMean = 0d,
            KeyboardIkiCv = 0d,
            KeyboardBurstRate = 0d,
            KeyboardBackspaceRate = 0d,
            ScrollVelocityIndex = 0d,
            MouseSpeedMean = 0d,
            MouseJerkP95 = 0d,
            MouseScrollSpeed = 0d,
            MouseMicroCorrectionRate = 0d
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        Assert.Equal(120d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("pause", result.ContinuityState);
        Assert.Equal("short_pause", result.SaFreezeReason);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ShortPassiveThinking_PreservesAndAccumulatesExistingEpisode()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 120d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0,
            continuityIdlePauseSeconds: 0d);

        InputBucketRow row = BuildStableRow() with
        {
            IdleSeconds = 5d,
            KeyboardIkiMean = 0d,
            KeyboardIkiCv = 0d,
            KeyboardBurstRate = 0d,
            KeyboardBackspaceRate = 0d,
            ScrollVelocityIndex = 0d,
            MouseSpeedMean = 0d,
            MouseJerkP95 = 0d,
            MouseScrollSpeed = 0d,
            MouseMicroCorrectionRate = 0d
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        Assert.Equal(125d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("keep", result.ContinuityState);
        Assert.Equal("passive_focus", result.SaFreezeReason);
        Assert.Equal(5d, result.SaIdlePauseSeconds, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ProlongedNoInteraction_DegradesConfidenceMoreThanShortQuiet()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 120d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0,
            continuityIdlePauseSeconds: 0d);

        InputBucketRow shortQuiet = BuildStableRow() with
        {
            IdleSeconds = 5d,
            KeyboardIkiMean = 0d,
            KeyboardIkiCv = 0d,
            KeyboardBurstRate = 0d,
            KeyboardBackspaceRate = 0d,
            ScrollVelocityIndex = 0d,
            MouseSpeedMean = 0d,
            MouseJerkP95 = 0d,
            MouseScrollSpeed = 0d,
            MouseMicroCorrectionRate = 0d
        };
        DerivedSessionMetricsResult shortQuietResult = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            shortQuiet,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 120d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0,
            continuityIdlePauseSeconds: 0d);

        InputBucketRow longQuiet = shortQuiet with
        {
            IdleSeconds = 40d
        };
        DerivedSessionMetricsResult longQuietResult = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            longQuiet,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        Assert.Equal("keep", shortQuietResult.ContinuityState);
        Assert.Equal("pause", longQuietResult.ContinuityState);
        Assert.True(shortQuietResult.ContinuityConfidence > longQuietResult.ContinuityConfidence);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ContextExcursionBeyondGrace_DecaysWithoutReset()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 120d,
            idleFragmentationIndex: 0.1d,
            saContextAppHash: "app_hash_work",
            saLastUpdateBucketSeq: 0,
            continuityContextSwitchSeconds: 60d);

        InputBucketRow row = BuildStableRow() with
        {
            DominantAppHash = "app_hash_other"
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            BuildPrimitive(focusAvailable: true),
            BaselineGatingResult.Warm);

        Assert.Equal(115d, result.TaskContinuitySeconds, precision: 6);
        Assert.Equal("decay", result.ContinuityState);
        Assert.Equal("context_excursion_decay", result.SaFreezeReason);
        Assert.Equal("app_hash_work", result.DominantAppHash);
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
        string saFreezeReason = "",
        double continuityIdlePauseSeconds = 0d,
        double continuityContextSwitchSeconds = 0d,
        string continuityState = "reset",
        double continuityScore = 0d,
        double continuityInstabilitySeconds = 0d,
        double focusEpisodeCandidateSeconds = 0d)
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = "v1",
            task_continuity_score = continuityScore,
            task_continuity_seconds = sustainedAttentionDurationSec,
            continuity_state = continuityState,
            continuity_confidence = 1d,
            continuity_anchor_app_hash = saContextAppHash,
            continuity_last_update_bucket_seq = saLastUpdateBucketSeq,
            continuity_idle_pause_seconds = continuityIdlePauseSeconds,
            continuity_context_switch_seconds = continuityContextSwitchSeconds,
            continuity_reason_code = saFreezeReason,
            continuity_instability_seconds = continuityInstabilitySeconds,
            focus_episode_candidate_seconds = focusEpisodeCandidateSeconds,
            sa_seconds = sustainedAttentionDurationSec,
            sa_context_app_hash = saContextAppHash,
            sa_last_update_bucket_seq = saLastUpdateBucketSeq,
            sa_freeze_reason = saFreezeReason,
            sa_idle_pause_seconds = continuityIdlePauseSeconds,
            sa_context_switch_seconds = continuityContextSwitchSeconds,
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
            saFreezeReason: result.SaFreezeReason,
            continuityIdlePauseSeconds: result.SaIdlePauseSeconds,
            continuityContextSwitchSeconds: result.SaContextSwitchSeconds,
            continuityState: result.ContinuityState,
            continuityScore: result.TaskContinuityScore,
            focusEpisodeCandidateSeconds: result.FocusEpisodeCandidateSeconds);
    }
}
