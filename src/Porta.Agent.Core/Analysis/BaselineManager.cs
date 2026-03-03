using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct BaselineGatingResult(
    bool BaselineReady,
    bool ColdStart,
    double ConfidenceCap,
    bool PoliciesEnabled)
{
    internal static BaselineGatingResult Warm => new(
        BaselineReady: true,
        ColdStart: false,
        ConfidenceCap: 1.0d,
        PoliciesEnabled: true);

    internal static BaselineGatingResult Cold => new(
        BaselineReady: false,
        ColdStart: true,
        ConfidenceCap: PortaExecutionConstants.BaselineReadiness.ColdStartConfidenceCap,
        PoliciesEnabled: false);
}

internal static class BaselineManager
{
    internal static BaselineGatingResult UpdateForFinalizedBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow inputBucketRow)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tx);

        bool canUpdateBaseline = IsStableForBaselineUpdate(connection, tx, inputBucketRow);
        if (canUpdateBaseline)
        {
            foreach (BaselineMetricDefinition metric in BaselineMetricRegistry.Metrics)
            {
                if (!metric.TryGetTransformedValue(inputBucketRow, out double value))
                {
                    continue;
                }

                BaselineStatRow? existing = ReadBaselineStat(connection, tx, metric.MetricKey);
                if (IsExtremeSpike(existing, value))
                {
                    continue;
                }

                UpsertBaselineStat(connection, tx, metric.MetricKey, value, inputBucketRow.StartUtc, existing);
            }
        }

        int readyMetricCount = CountReadyMetrics(connection, tx, PortaExecutionConstants.BaselineMinSamples);
        int totalMetricCount = BaselineMetricRegistry.Metrics.Count;
        double baselineReadyPct = totalMetricCount <= 0 ? 0d : readyMetricCount / (double)totalMetricCount;
        QualityWindowStats qualityWindow = ReadRecentQualityWindow(
            connection,
            tx,
            inputBucketRow.BucketSeq,
            PortaExecutionConstants.BaselineReadiness.RecentQualityBuckets);
        bool hasFullQualityWindow = qualityWindow.BucketCount >= PortaExecutionConstants.BaselineReadiness.RecentQualityBuckets;
        bool baselineReady = baselineReadyPct >= PortaExecutionConstants.BaselineReadiness.ReadyCoverageThreshold
            && hasFullQualityWindow
            && qualityWindow.AverageQuality >= PortaExecutionConstants.BaselineReadiness.RecentQualityThreshold;
        BaselineGatingResult gating = baselineReady ? BaselineGatingResult.Warm : BaselineGatingResult.Cold;

        UpsertRuntimeStateScalar(connection, tx, "baseline_ready", gating.BaselineReady, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "cold_start", gating.ColdStart, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "confidence_cap", gating.ConfidenceCap, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "policies_enabled", gating.PoliciesEnabled, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "baseline_ready_pct", baselineReadyPct, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "avg_quality_2m", qualityWindow.AverageQuality, inputBucketRow.StartUtc);
        UpsertRuntimeStateObject(
            connection,
            tx,
            "baseline_status",
            new
            {
                schema_ver = 1,
                baseline_ready = gating.BaselineReady,
                cold_start = gating.ColdStart,
                confidence_cap = gating.ConfidenceCap,
                policies_enabled = gating.PoliciesEnabled,
                baseline_ready_pct = baselineReadyPct,
                avg_quality_2m = qualityWindow.AverageQuality,
                quality_window_bucket_count = qualityWindow.BucketCount,
                quality_window_required_buckets = PortaExecutionConstants.BaselineReadiness.RecentQualityBuckets,
                metric_count_ready = readyMetricCount,
                metric_count_total = totalMetricCount,
                baseline_min_samples = PortaExecutionConstants.BaselineMinSamples,
                metric_registry_ver = BaselineMetricRegistry.RegistryVersion,
                std_convention = PortaExecutionConstants.BaselineUpdateGating.StdConvention,
                std_epsilon = PortaExecutionConstants.BaselineUpdateGating.StdEpsilon,
                numeric_type = PortaExecutionConstants.BaselineUpdateGating.NumericType
            },
            inputBucketRow.StartUtc);

        return gating;
    }

    private static void UpsertBaselineStat(
        SqliteConnection connection,
        SqliteTransaction tx,
        string metricKey,
        double sampleValue,
        string lastUpdateUtc,
        BaselineStatRow? existing)
    {
        double value = ClampFiniteNonNegative(sampleValue);
        double nextMean;
        double nextStd;
        double nextM2;
        int nextCount;

        if (existing == null)
        {
            nextMean = value;
            nextStd = 0d;
            nextM2 = 0d;
            nextCount = 1;
        }
        else
        {
            BaselineStatRow prev = existing.Value;
            nextCount = checked(prev.SampleCount + 1);
            double delta = value - prev.Mean;
            nextMean = prev.Mean + (delta / nextCount);
            nextM2 = Math.Max(0d, prev.M2 + (delta * (value - nextMean)));
            // Baseline contract: store population stddev (sqrt(m2 / n)) using float64 math.
            nextStd = Math.Sqrt(Math.Max(0d, nextM2 / nextCount));
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO baseline_stats (metric_key, mean, std, sample_count, m2, last_update_utc)
            VALUES ($metric_key, $mean, $std, $sample_count, $m2, $last_update_utc)
            ON CONFLICT(metric_key) DO UPDATE SET
                mean = excluded.mean,
                std = excluded.std,
                sample_count = excluded.sample_count,
                m2 = excluded.m2,
                last_update_utc = excluded.last_update_utc;
            """;
        cmd.Parameters.AddWithValue("$metric_key", metricKey);
        cmd.Parameters.AddWithValue("$mean", nextMean);
        cmd.Parameters.AddWithValue("$std", nextStd);
        cmd.Parameters.AddWithValue("$sample_count", nextCount);
        cmd.Parameters.AddWithValue("$m2", nextM2);
        cmd.Parameters.AddWithValue("$last_update_utc", lastUpdateUtc);
        cmd.ExecuteNonQuery();
    }

    private static bool IsStableForBaselineUpdate(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow row)
    {
        if (row.Partial != 0)
        {
            return false;
        }

        if (row.QualityScore < PortaExecutionConstants.BaselineUpdateGating.MinQualityScore)
        {
            return false;
        }

        return !ReadOverloadActive(connection, tx);
    }

    private static bool ReadOverloadActive(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT value_json
            FROM runtime_state
            WHERE key = 'overload_active'
            LIMIT 1;
            """;

        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return false;
        }

        string raw = Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.True || root.ValueKind == JsonValueKind.False)
            {
                return root.GetBoolean();
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("value", out JsonElement valueElement)
                    && (valueElement.ValueKind == JsonValueKind.True || valueElement.ValueKind == JsonValueKind.False))
                {
                    return valueElement.GetBoolean();
                }

                if (root.TryGetProperty("overload_active", out JsonElement overloadElement)
                    && (overloadElement.ValueKind == JsonValueKind.True || overloadElement.ValueKind == JsonValueKind.False))
                {
                    return overloadElement.GetBoolean();
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsExtremeSpike(BaselineStatRow? existing, double value)
    {
        if (!existing.HasValue)
        {
            return false;
        }

        BaselineStatRow prev = existing.Value;
        if (prev.SampleCount < PortaExecutionConstants.BaselineUpdateGating.ZScoreMinSamples)
        {
            return false;
        }

        double denominator = Math.Max(prev.Std, PortaExecutionConstants.BaselineUpdateGating.StdEpsilon);
        double z = Math.Abs(value - prev.Mean) / denominator;
        return z > PortaExecutionConstants.BaselineUpdateGating.ZScoreMax;
    }

    private static BaselineStatRow? ReadBaselineStat(
        SqliteConnection connection,
        SqliteTransaction tx,
        string metricKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT mean, std, m2, sample_count
            FROM baseline_stats
            WHERE metric_key = $metric_key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$metric_key", metricKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new BaselineStatRow(
            Mean: reader.GetDouble(0),
            Std: reader.GetDouble(1),
            M2: reader.GetDouble(2),
            SampleCount: reader.GetInt32(3));
    }

    private static int CountReadyMetrics(SqliteConnection connection, SqliteTransaction tx, int minSamples)
    {
        int ready = 0;
        foreach (BaselineMetricDefinition metric in BaselineMetricRegistry.Metrics)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                SELECT sample_count
                FROM baseline_stats
                WHERE metric_key = $metric_key
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$metric_key", metric.MetricKey);
            object? scalar = cmd.ExecuteScalar();
            int count = scalar == null || scalar == DBNull.Value ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
            if (count >= minSamples)
            {
                ready++;
            }
        }

        return ready;
    }

    private static QualityWindowStats ReadRecentQualityWindow(
        SqliteConnection connection,
        SqliteTransaction tx,
        long upToBucketSeq,
        int maxBuckets)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT quality_score
            FROM input_buckets
            WHERE bucket_seq <= $up_to_bucket_seq
            ORDER BY bucket_seq DESC
            LIMIT $max_buckets;
            """;
        cmd.Parameters.AddWithValue("$up_to_bucket_seq", upToBucketSeq);
        cmd.Parameters.AddWithValue("$max_buckets", maxBuckets);

        using SqliteDataReader reader = cmd.ExecuteReader();
        double sum = 0d;
        int count = 0;
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            double quality = Clamp01(reader.GetDouble(0));
            sum += quality;
            count++;
        }

        if (count == 0)
        {
            return new QualityWindowStats(0, 0d);
        }

        return new QualityWindowStats(count, Clamp01(sum / count));
    }

    private static void UpsertRuntimeStateScalar(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        object value,
        string updatedUtc)
    {
        UpsertRuntimeStateObject(
            connection,
            tx,
            key,
            new
            {
                schema_ver = 1,
                value
            },
            updatedUtc);
    }

    private static void UpsertRuntimeStateObject(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        object value,
        string updatedUtc)
    {
        string valueJson = JsonSerializer.Serialize(value);
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

    private static double ClampFiniteNonNegative(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
        {
            return 0d;
        }

        return value;
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private readonly record struct BaselineStatRow(double Mean, double Std, double M2, int SampleCount);
    private readonly record struct QualityWindowStats(int BucketCount, double AverageQuality);
}
