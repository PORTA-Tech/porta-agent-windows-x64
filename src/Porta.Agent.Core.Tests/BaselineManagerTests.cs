using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class BaselineManagerTests
{
    [Fact]
    public void UpdateForFinalizedBucket_UpdatesBaselineStats_AndColdStartFlags()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        long tsMs = Time.epoch_anchor_ms;
        InputBucketRow row = BuildRow(bucketSeq: 0, startUtc: Time.toIsoUtcMsString(tsMs), quality: 0.90);
        InsertInputBucketRow(connection, tx, row);

        BaselineGatingResult gating = BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
        tx.Commit();

        Assert.Equal(BaselineMetricRegistry.Metrics.Count, ReadInt(connection, "SELECT COUNT(*) FROM baseline_stats;"));
        Assert.Equal(1, ReadInt(connection, "SELECT sample_count FROM baseline_stats WHERE metric_key='keyboard_iki_mean';"));
        Assert.False(gating.BaselineReady);
        Assert.True(gating.ColdStart);
        Assert.Equal(0.45d, gating.ConfidenceCap, precision: 6);
        Assert.False(gating.PoliciesEnabled);
        Assert.False(ReadRuntimeStateBool(connection, "baseline_ready"));
        Assert.True(ReadRuntimeStateBool(connection, "cold_start"));
        Assert.Equal(0.45d, ReadRuntimeStateDouble(connection, "confidence_cap"), precision: 6);
        Assert.False(ReadRuntimeStateBool(connection, "policies_enabled"));
    }

    [Fact]
    public void UpdateForFinalizedBucket_ReachesReadyAtMinSamplesWithGoodQuality()
    {
        using var connection = OpenInMemoryDb();
        long startMs = Time.epoch_anchor_ms;

        BaselineGatingResult? lastGating = null;
        for (int i = 0; i < PortaExecutionConstants.BaselineMinSamples; i++)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            long bucketSeq = i;
            long bucketStartMs = startMs + (bucketSeq * Time.bucket_ms);
            string startUtc = Time.toIsoUtcMsString(bucketStartMs);
            InputBucketRow row = BuildRow(bucketSeq, startUtc, quality: 0.95);
            InsertInputBucketRow(connection, tx, row);
            lastGating = BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
            tx.Commit();
        }

        Assert.True(lastGating.HasValue);
        Assert.True(lastGating.Value.BaselineReady);
        Assert.False(lastGating.Value.ColdStart);
        Assert.Equal(1.0d, lastGating.Value.ConfidenceCap, precision: 6);
        Assert.True(lastGating.Value.PoliciesEnabled);
        Assert.True(ReadRuntimeStateBool(connection, "baseline_ready"));
        Assert.False(ReadRuntimeStateBool(connection, "cold_start"));
        Assert.Equal(1.0d, ReadRuntimeStateDouble(connection, "confidence_cap"), precision: 6);
        Assert.True(ReadRuntimeStateBool(connection, "policies_enabled"));
        Assert.Equal(1.0d, ReadRuntimeStateDouble(connection, "baseline_ready_pct"), precision: 6);
        Assert.InRange(ReadRuntimeStateDouble(connection, "avg_quality_2m"), 0.94d, 0.96d);
    }

    [Fact]
    public void UpdateForFinalizedBucket_SkipsBaseline_WhenBucketIsPartial()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        long tsMs = Time.epoch_anchor_ms;
        InputBucketRow row = BuildRow(bucketSeq: 0, startUtc: Time.toIsoUtcMsString(tsMs), quality: 0.90) with { Partial = 1 };
        InsertInputBucketRow(connection, tx, row);

        BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
        tx.Commit();

        Assert.Equal(0, ReadInt(connection, "SELECT COUNT(*) FROM baseline_stats;"));
    }

    [Fact]
    public void UpdateForFinalizedBucket_SkipsBaseline_WhenQualityTooLow()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        long tsMs = Time.epoch_anchor_ms;
        InputBucketRow row = BuildRow(bucketSeq: 0, startUtc: Time.toIsoUtcMsString(tsMs), quality: 0.59);
        InsertInputBucketRow(connection, tx, row);

        BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
        tx.Commit();

        Assert.Equal(0, ReadInt(connection, "SELECT COUNT(*) FROM baseline_stats;"));
    }

    [Fact]
    public void UpdateForFinalizedBucket_SkipsBaseline_WhenOverloadActive()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        long tsMs = Time.epoch_anchor_ms;
        string startUtc = Time.toIsoUtcMsString(tsMs);
        InsertRuntimeState(connection, tx, "overload_active", "{\"schema_ver\":1,\"value\":true}", startUtc);

        InputBucketRow row = BuildRow(bucketSeq: 0, startUtc: startUtc, quality: 0.90);
        InsertInputBucketRow(connection, tx, row);

        BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
        tx.Commit();

        Assert.Equal(0, ReadInt(connection, "SELECT COUNT(*) FROM baseline_stats;"));
    }

    [Fact]
    public void UpdateForFinalizedBucket_SkipsExtremeSpike_WhenMetricBaselineIsMature()
    {
        using var connection = OpenInMemoryDb();
        long startMs = Time.epoch_anchor_ms;

        const int matureSamples = PortaExecutionConstants.BaselineUpdateGating.ZScoreMinSamples;
        for (int i = 0; i < matureSamples; i++)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            long bucketStartMs = startMs + (i * Time.bucket_ms);
            string startUtc = Time.toIsoUtcMsString(bucketStartMs);
            InputBucketRow row = BuildRow(bucketSeq: i, startUtc: startUtc, quality: 0.95);
            InsertInputBucketRow(connection, tx, row);
            BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
            tx.Commit();
        }

        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            long bucketSeq = matureSamples;
            long bucketStartMs = startMs + (bucketSeq * Time.bucket_ms);
            string startUtc = Time.toIsoUtcMsString(bucketStartMs);
            InputBucketRow outlier = BuildRow(bucketSeq, startUtc, quality: 0.95) with
            {
                KeyboardIkiMean = 10_000d
            };

            InsertInputBucketRow(connection, tx, outlier);
            BaselineManager.UpdateForFinalizedBucket(connection, tx, outlier);
            tx.Commit();
        }

        Assert.Equal(matureSamples, ReadInt(connection, "SELECT sample_count FROM baseline_stats WHERE metric_key='keyboard_iki_mean';"));
        Assert.Equal(matureSamples + 1, ReadInt(connection, "SELECT sample_count FROM baseline_stats WHERE metric_key='focus_switch_rate';"));
    }

    [Fact]
    public void UpdateForFinalizedBucket_UsesPopulationStdConvention_WithWelfordM2()
    {
        using var connection = OpenInMemoryDb();
        long startMs = Time.epoch_anchor_ms;
        double[] samples = [1d, 2d, 3d];

        for (int i = 0; i < samples.Length; i++)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            long bucketSeq = i;
            long bucketStartMs = startMs + (bucketSeq * Time.bucket_ms);
            string startUtc = Time.toIsoUtcMsString(bucketStartMs);
            InputBucketRow row = BuildRow(bucketSeq, startUtc, quality: 0.95) with
            {
                KeyboardIkiCv = samples[i]
            };

            InsertInputBucketRow(connection, tx, row);
            BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
            tx.Commit();
        }

        double mean = ReadDouble(connection, "SELECT mean FROM baseline_stats WHERE metric_key='keyboard_iki_cv';");
        double std = ReadDouble(connection, "SELECT std FROM baseline_stats WHERE metric_key='keyboard_iki_cv';");
        double m2 = ReadDouble(connection, "SELECT m2 FROM baseline_stats WHERE metric_key='keyboard_iki_cv';");
        int n = ReadInt(connection, "SELECT sample_count FROM baseline_stats WHERE metric_key='keyboard_iki_cv';");

        Assert.Equal(3, n);
        Assert.Equal(2d, mean, precision: 10);
        Assert.Equal(2d, m2, precision: 10);
        Assert.Equal(Math.Sqrt(2d / 3d), std, precision: 10);
    }

    [Fact]
    public void UpdateForFinalizedBucket_BaselineNotReady_WhenRecentQualityWindowIsIncomplete()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        long tsMs = Time.epoch_anchor_ms;
        string startUtc = Time.toIsoUtcMsString(tsMs);
        SeedCoverageAsReady(connection, tx, startUtc);

        InputBucketRow row = BuildRow(bucketSeq: 0, startUtc: startUtc, quality: 0.95);
        InsertInputBucketRow(connection, tx, row);
        BaselineGatingResult gating = BaselineManager.UpdateForFinalizedBucket(connection, tx, row);
        tx.Commit();

        Assert.False(gating.BaselineReady);
        Assert.True(gating.ColdStart);
        Assert.Equal(0.45d, gating.ConfidenceCap, precision: 6);
        Assert.False(gating.PoliciesEnabled);
        Assert.False(ReadRuntimeStateBool(connection, "baseline_ready"));
        Assert.True(ReadRuntimeStateBool(connection, "cold_start"));
    }

    private static SqliteConnection OpenInMemoryDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE baseline_stats (
                metric_key TEXT PRIMARY KEY,
                mean REAL NOT NULL,
                std REAL NOT NULL,
                m2 REAL NOT NULL,
                sample_count INTEGER NOT NULL,
                last_update_utc TEXT NOT NULL
            );

            CREATE TABLE input_buckets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                start_utc TEXT NOT NULL,
                bucket_seq INTEGER NOT NULL,
                typing_burst_intensity REAL NOT NULL,
                scroll_velocity_index REAL NOT NULL,
                focus_switch_rate REAL NOT NULL,
                keyboard_iki_mean REAL NOT NULL,
                keyboard_iki_cv REAL NOT NULL,
                keyboard_burst_rate REAL NOT NULL,
                keyboard_backspace_rate REAL NOT NULL,
                mouse_speed_mean REAL NOT NULL,
                mouse_jerk_p95 REAL NOT NULL,
                mouse_scroll_speed REAL NOT NULL,
                mouse_micro_correction_rate REAL NOT NULL,
                focus_app_entropy REAL NOT NULL,
                focus_active_app_count REAL NOT NULL,
                idle_seconds REAL NOT NULL,
                idle_micro_idle_count REAL NOT NULL,
                quality_score REAL NOT NULL,
                partial INTEGER NOT NULL,
                contributors_json TEXT NOT NULL
            );

            CREATE TABLE runtime_state (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static InputBucketRow BuildRow(long bucketSeq, string startUtc, double quality)
    {
        return new InputBucketRow(
            BucketSeq: bucketSeq,
            TimestampUtc: startUtc,
            StartUtc: startUtc,
            TypingBurstIntensity: 0.20d,
            ScrollVelocityIndex: 0.30d,
            FocusSwitchRate: 0.40d,
            KeyboardIkiMean: 120d,
            KeyboardIkiCv: 0.35d,
            KeyboardBurstRate: 0.45d,
            KeyboardBackspaceRate: 0.11d,
            MouseSpeedMean: 220d,
            MouseJerkP95: 1500d,
            MouseScrollSpeed: 80d,
            MouseMicroCorrectionRate: 0.09d,
            FocusAppEntropy: 1.2d,
            FocusActiveAppCount: 3d,
            IdleSeconds: 25d,
            IdleMicroIdleCount: 2d,
            QualityScore: quality,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
    }

    private static void InsertInputBucketRow(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow row)
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
                focus_active_app_count,
                idle_seconds,
                idle_micro_idle_count,
                quality_score,
                partial,
                contributors_json
            ) VALUES (
                $ts,
                $start,
                $bucket_seq,
                $typing,
                $scroll,
                $focus,
                $keyboard_iki_mean,
                $keyboard_iki_cv,
                $keyboard_burst_rate,
                $keyboard_backspace_rate,
                $mouse_speed_mean,
                $mouse_jerk_p95,
                $mouse_scroll_speed,
                $mouse_micro_correction_rate,
                $focus_app_entropy,
                $focus_active_app_count,
                $idle_seconds,
                $idle_micro_idle_count,
                $quality,
                $partial,
                $contributors
            );
            """;
        cmd.Parameters.AddWithValue("$ts", row.TimestampUtc);
        cmd.Parameters.AddWithValue("$start", row.StartUtc);
        cmd.Parameters.AddWithValue("$bucket_seq", row.BucketSeq);
        cmd.Parameters.AddWithValue("$typing", row.TypingBurstIntensity);
        cmd.Parameters.AddWithValue("$scroll", row.ScrollVelocityIndex);
        cmd.Parameters.AddWithValue("$focus", row.FocusSwitchRate);
        cmd.Parameters.AddWithValue("$keyboard_iki_mean", row.KeyboardIkiMean);
        cmd.Parameters.AddWithValue("$keyboard_iki_cv", row.KeyboardIkiCv);
        cmd.Parameters.AddWithValue("$keyboard_burst_rate", row.KeyboardBurstRate);
        cmd.Parameters.AddWithValue("$keyboard_backspace_rate", row.KeyboardBackspaceRate);
        cmd.Parameters.AddWithValue("$mouse_speed_mean", row.MouseSpeedMean);
        cmd.Parameters.AddWithValue("$mouse_jerk_p95", row.MouseJerkP95);
        cmd.Parameters.AddWithValue("$mouse_scroll_speed", row.MouseScrollSpeed);
        cmd.Parameters.AddWithValue("$mouse_micro_correction_rate", row.MouseMicroCorrectionRate);
        cmd.Parameters.AddWithValue("$focus_app_entropy", row.FocusAppEntropy);
        cmd.Parameters.AddWithValue("$focus_active_app_count", row.FocusActiveAppCount);
        cmd.Parameters.AddWithValue("$idle_seconds", row.IdleSeconds);
        cmd.Parameters.AddWithValue("$idle_micro_idle_count", row.IdleMicroIdleCount);
        cmd.Parameters.AddWithValue("$quality", row.QualityScore);
        cmd.Parameters.AddWithValue("$partial", row.Partial);
        cmd.Parameters.AddWithValue("$contributors", row.ContributorsJson);
        cmd.ExecuteNonQuery();
    }

    private static void InsertRuntimeState(
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
            VALUES ($key, $value_json, $updated_utc);
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.Parameters.AddWithValue("$updated_utc", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    private static void SeedCoverageAsReady(
        SqliteConnection connection,
        SqliteTransaction tx,
        string lastUpdateUtc)
    {
        foreach (BaselineMetricDefinition metric in BaselineMetricRegistry.Metrics)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO baseline_stats (metric_key, mean, std, m2, sample_count, last_update_utc)
                VALUES ($metric_key, 1.0, 0.1, 0.01, $sample_count, $last_update_utc);
                """;
            cmd.Parameters.AddWithValue("$metric_key", metric.MetricKey);
            cmd.Parameters.AddWithValue("$sample_count", PortaExecutionConstants.BaselineMinSamples);
            cmd.Parameters.AddWithValue("$last_update_utc", lastUpdateUtc);
            cmd.ExecuteNonQuery();
        }
    }

    private static int ReadInt(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        object? scalar = cmd.ExecuteScalar();
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static bool ReadRuntimeStateBool(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("value").GetBoolean();
    }

    private static double ReadRuntimeStateDouble(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("value").GetDouble();
    }

    private static double ReadDouble(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        object? scalar = cmd.ExecuteScalar();
        return Convert.ToDouble(scalar, CultureInfo.InvariantCulture);
    }
}
