using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct BaselineGatingResult(
    bool BaselineReady,
    bool ColdStart,
    double ConfidenceCap,
    bool GuidanceEnabled,
    bool SuggestionsEnabled,
    bool SystemActionsEnabled,
    string CalibrationStage)
{
    internal static BaselineGatingResult Warm => new(
        BaselineReady: true,
        ColdStart: false,
        ConfidenceCap: 1.0d,
        GuidanceEnabled: true,
        SuggestionsEnabled: true,
        SystemActionsEnabled: true,
        CalibrationStage: CalibrationTruth.StageCalibrated);

    internal static BaselineGatingResult Provisional(double confidenceCap) => new(
        BaselineReady: false,
        ColdStart: false,
        ConfidenceCap: Math.Clamp(
            confidenceCap,
            PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCapMin,
            PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCap),
        GuidanceEnabled: true,
        SuggestionsEnabled: true,
        SystemActionsEnabled: false,
        CalibrationStage: CalibrationTruth.StageProvisional);

    internal static BaselineGatingResult Cold(bool guidanceEnabled) => new(
        BaselineReady: false,
        ColdStart: true,
        ConfidenceCap: PortaExecutionConstants.BaselineReadiness.ColdStartConfidenceCap,
        GuidanceEnabled: guidanceEnabled,
        SuggestionsEnabled: false,
        SystemActionsEnabled: false,
        CalibrationStage: CalibrationTruth.StageCold);

    internal bool PoliciesEnabled => SuggestionsEnabled;
}

internal readonly record struct MetricNormalizationValue(
    string MetricKey,
    double RawValue,
    double WinsorizedValue,
    double Z,
    bool MetricReady,
    bool WasWinsorized,
    int NonFinite);

internal sealed record RobustNormalizationSnapshot(
    IReadOnlyDictionary<string, MetricNormalizationValue> Metrics,
    double TypingComponent,
    double ScrollComponent,
    double FocusComponent);

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
                MetricBoundsRow? existingBounds = ReadMetricBounds(connection, tx, metric.MetricKey);
                if (IsExtremeSpike(metric, existing, existingBounds, value))
                {
                    continue;
                }

                UpsertBaselineStat(connection, tx, metric.MetricKey, value, inputBucketRow.StartUtc, existing);
                UpsertMetricBounds(connection, tx, metric.MetricKey, value, inputBucketRow.StartUtc, existingBounds);
            }
        }

        int readyMetricCount = CountReadyMetrics(connection, tx, PortaExecutionConstants.BaselineMinSamples);
        int provisionalMetricCount = CountReadyMetrics(connection, tx, PortaExecutionConstants.BaselineReadiness.ProvisionalMetricMinSamples);
        int totalMetricCount = BaselineMetricRegistry.Metrics.Count;
        double baselineReadyPct = totalMetricCount <= 0 ? 0d : readyMetricCount / (double)totalMetricCount;
        double provisionalReadyPct = totalMetricCount <= 0 ? 0d : provisionalMetricCount / (double)totalMetricCount;
        QualityWindowStats qualityWindow = ReadRecentQualityWindow(
            connection,
            tx,
            inputBucketRow.BucketSeq,
            PortaExecutionConstants.BaselineReadiness.RecentQualityBuckets);
        QualityWindowStats provisionalQualityWindow = ReadRecentQualityWindow(
            connection,
            tx,
            inputBucketRow.BucketSeq,
            PortaExecutionConstants.BaselineReadiness.ProvisionalRecentQualityBuckets);
        bool hasFullQualityWindow = qualityWindow.BucketCount >= PortaExecutionConstants.BaselineReadiness.RecentQualityBuckets;
        bool hasProvisionalQualityWindow = provisionalQualityWindow.BucketCount >= PortaExecutionConstants.BaselineReadiness.ProvisionalRecentQualityBuckets;
        bool baselineReady = baselineReadyPct >= PortaExecutionConstants.BaselineReadiness.ReadyCoverageThreshold
            && hasFullQualityWindow
            && qualityWindow.AverageQuality >= PortaExecutionConstants.BaselineReadiness.RecentQualityThreshold;
        bool provisionalReady = provisionalReadyPct >= PortaExecutionConstants.BaselineReadiness.ProvisionalCoverageThreshold
            && hasProvisionalQualityWindow
            && provisionalQualityWindow.AverageQuality >= PortaExecutionConstants.BaselineReadiness.ProvisionalRecentQualityThreshold;
        double bootstrappingQuality = ResolveBootstrappingQuality(inputBucketRow, provisionalQualityWindow);
        bool guidanceEnabled = bootstrappingQuality >= PortaExecutionConstants.BaselineReadiness.BootstrappingGuidanceQualityFloor
            && HasEarlySignalEvidence(inputBucketRow);
        BaselineGatingResult gating = baselineReady
            ? BaselineGatingResult.Warm
            : (provisionalReady
                ? BaselineGatingResult.Provisional(
                    ResolveProvisionalConfidenceCap(
                        baselineReadyPct,
                        qualityWindow,
                        provisionalQualityWindow))
                : BaselineGatingResult.Cold(guidanceEnabled));

        UpsertRuntimeStateScalar(connection, tx, "baseline_ready", gating.BaselineReady, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "cold_start", gating.ColdStart, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "confidence_cap", gating.ConfidenceCap, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "guidance_enabled", gating.GuidanceEnabled, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "suggestions_enabled", gating.SuggestionsEnabled, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "policies_enabled", gating.PoliciesEnabled, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "system_actions_enabled", gating.SystemActionsEnabled, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "calibration_stage", gating.CalibrationStage, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "baseline_ready_pct", baselineReadyPct, inputBucketRow.StartUtc);
        UpsertRuntimeStateScalar(connection, tx, "provisional_ready_pct", provisionalReadyPct, inputBucketRow.StartUtc);
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
                guidance_enabled = gating.GuidanceEnabled,
                suggestions_enabled = gating.SuggestionsEnabled,
                policies_enabled = gating.PoliciesEnabled,
                system_actions_enabled = gating.SystemActionsEnabled,
                calibration_stage = gating.CalibrationStage,
                baseline_ready_pct = baselineReadyPct,
                provisional_ready_pct = provisionalReadyPct,
                avg_quality_2m = qualityWindow.AverageQuality,
                quality_window_bucket_count = qualityWindow.BucketCount,
                quality_window_required_buckets = PortaExecutionConstants.BaselineReadiness.RecentQualityBuckets,
                provisional_avg_quality = provisionalQualityWindow.AverageQuality,
                provisional_quality_window_bucket_count = provisionalQualityWindow.BucketCount,
                provisional_quality_window_required_buckets = PortaExecutionConstants.BaselineReadiness.ProvisionalRecentQualityBuckets,
                metric_count_ready = readyMetricCount,
                provisional_metric_count_ready = provisionalMetricCount,
                metric_count_total = totalMetricCount,
                baseline_min_samples = PortaExecutionConstants.BaselineMinSamples,
                provisional_min_samples = PortaExecutionConstants.BaselineReadiness.ProvisionalMetricMinSamples,
                metric_registry_ver = BaselineMetricRegistry.RegistryVersion,
                std_convention = PortaExecutionConstants.BaselineUpdateGating.StdConvention,
                std_epsilon = PortaExecutionConstants.BaselineUpdateGating.StdEpsilon,
                numeric_type = PortaExecutionConstants.BaselineUpdateGating.NumericType,
                std_floor_registry_ver = PortaExecutionConstants.RobustNormalization.StdFloorRegistryVersion,
                metric_ready_min_samples = PortaExecutionConstants.RobustNormalization.MetricReadyMinSamples,
                bounds_estimator_ver = PortaExecutionConstants.RobustNormalization.BoundsEstimatorVersion,
                bounds_quantile_method = PortaExecutionConstants.RobustNormalization.BoundsQuantileMethod,
                bounds_buffer_size = PortaExecutionConstants.RobustNormalization.RollingBoundsBufferSize,
                winsor_fallback_strategy = PortaExecutionConstants.RobustNormalization.WinsorFallbackStrategy
            },
            inputBucketRow.StartUtc);

        return gating;
    }

    private static double ResolveBootstrappingQuality(
        InputBucketRow inputBucketRow,
        QualityWindowStats provisionalQualityWindow)
    {
        double quality = provisionalQualityWindow.BucketCount > 0
            ? provisionalQualityWindow.AverageQuality
            : inputBucketRow.QualityScore;
        return Math.Clamp(double.IsFinite(quality) ? quality : 0d, 0d, 1d);
    }

    private static bool HasEarlySignalEvidence(InputBucketRow row)
    {
        bool keyboardEvidence = row.KeyboardBurstRate > 0d
            || row.KeyboardIkiMean > 0d
            || row.KeyboardBackspaceRate > 0d
            || row.TypingBurstIntensity > 0d;
        bool mouseEvidence = row.MouseSpeedMean > 0d
            || row.MouseScrollSpeed > 0d
            || row.MouseJerkP95 > 0d
            || row.MouseMicroCorrectionRate > 0d
            || row.ScrollVelocityIndex > 0d;
        bool focusEvidence = row.FocusActiveAppCount > 0d
            || row.FocusSwitchCount > 0
            || row.FocusAppEntropy > 0d
            || !string.IsNullOrWhiteSpace(row.DominantAppHash);

        return keyboardEvidence || mouseEvidence || focusEvidence;
    }

    private static double ResolveProvisionalConfidenceCap(
        double baselineReadyPct,
        QualityWindowStats qualityWindow,
        QualityWindowStats provisionalQualityWindow)
    {
        double coverageProgress = 0d;
        double coverageRange = PortaExecutionConstants.BaselineReadiness.ReadyCoverageThreshold
            - PortaExecutionConstants.BaselineReadiness.ProvisionalCoverageThreshold;
        if (coverageRange > 0d)
        {
            coverageProgress = Math.Clamp(
                (baselineReadyPct - PortaExecutionConstants.BaselineReadiness.ProvisionalCoverageThreshold) / coverageRange,
                0d,
                1d);
        }

        double observedQuality = qualityWindow.BucketCount > 0
            ? qualityWindow.AverageQuality
            : provisionalQualityWindow.AverageQuality;
        double qualityRange = PortaExecutionConstants.BaselineReadiness.RecentQualityThreshold
            - PortaExecutionConstants.BaselineReadiness.ProvisionalRecentQualityThreshold;
        double qualityProgress = qualityRange <= 0d
            ? 1d
            : Math.Clamp(
                (observedQuality - PortaExecutionConstants.BaselineReadiness.ProvisionalRecentQualityThreshold) / qualityRange,
                0d,
                1d);

        double progress = (0.60d * coverageProgress) + (0.40d * qualityProgress);
        return PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCapMin
            + ((PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCap
                - PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCapMin) * progress);
    }

    internal static RobustNormalizationSnapshot ComputeRobustNormalizationForBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow inputBucketRow)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tx);

        var metrics = new Dictionary<string, MetricNormalizationValue>(StringComparer.Ordinal);
        foreach (BaselineMetricDefinition metric in BaselineMetricRegistry.Metrics)
        {
            if (!metric.TryGetTransformedValue(inputBucketRow, out double rawTransformed))
            {
                metrics[metric.MetricKey] = new MetricNormalizationValue(
                    MetricKey: metric.MetricKey,
                    RawValue: 0d,
                    WinsorizedValue: 0d,
                    Z: 0d,
                    MetricReady: false,
                    WasWinsorized: false,
                    NonFinite: 0);
                continue;
            }

            BaselineStatRow? baseline = ReadBaselineStat(connection, tx, metric.MetricKey);
            MetricBoundsRow? bounds = ReadMetricBounds(connection, tx, metric.MetricKey);
            RobustNormalizeResult normalized = RobustNormalize.Normalize(
                metricKey: metric.MetricKey,
                rawValue: rawTransformed,
                baseline: baseline.HasValue
                    ? new RobustBaselineStats(baseline.Value.Mean, baseline.Value.Std, baseline.Value.SampleCount)
                    : new RobustBaselineStats(0d, 0d, 0),
                rollingBounds: bounds.HasValue
                    ? new RobustRollingBounds(bounds.Value.P5, bounds.Value.P95)
                    : null);

            metrics[metric.MetricKey] = new MetricNormalizationValue(
                MetricKey: metric.MetricKey,
                RawValue: rawTransformed,
                WinsorizedValue: normalized.WinsorizedValue,
                Z: normalized.Z,
                MetricReady: normalized.BaselineReadyForMetric,
                WasWinsorized: normalized.WasWinsorized,
                NonFinite: normalized.NonFinite);
        }

        double typingComponent = ResolveComponentValue(
            metrics,
            [
                "keyboard_iki_mean",
                "keyboard_iki_cv",
                "keyboard_burst_rate",
                "keyboard_backspace_rate"
            ],
            fallback: inputBucketRow.TypingBurstIntensity);
        double scrollComponent = ResolveComponentValue(
            metrics,
            [
                "mouse_speed_mean",
                "mouse_jerk_p95",
                "mouse_scroll_speed",
                "mouse_micro_correction_rate"
            ],
            fallback: inputBucketRow.ScrollVelocityIndex);
        double focusComponent = ResolveComponentValue(
            metrics,
            [
                "focus_switch_rate",
                "focus_app_entropy",
                "focus_active_app_count"
            ],
            fallback: inputBucketRow.FocusSwitchRate);

        return new RobustNormalizationSnapshot(metrics, typingComponent, scrollComponent, focusComponent);
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

    private static bool IsExtremeSpike(
        BaselineMetricDefinition metric,
        BaselineStatRow? existing,
        MetricBoundsRow? existingBounds,
        double value)
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

        RobustRollingBounds? rollingBounds = null;
        bool boundsMature = false;
        if (existingBounds.HasValue)
        {
            MetricBoundsRow bounds = existingBounds.Value;
            rollingBounds = new RobustRollingBounds(bounds.P5, bounds.P95);
            boundsMature = bounds.BoundsSampleCount >= PortaExecutionConstants.BaselineUpdateGating.ZScoreMinSamples;
        }

        RobustNormalizeResult normalized = RobustNormalize.Normalize(
            metricKey: metric.MetricKey,
            rawValue: value,
            baseline: new RobustBaselineStats(prev.Mean, prev.Std, prev.SampleCount),
            rollingBounds: rollingBounds,
            zClampRange: new RobustZClampRange(
                -PortaExecutionConstants.BaselineUpdateGating.ZScoreMax * 2d,
                PortaExecutionConstants.BaselineUpdateGating.ZScoreMax * 2d));

        if (!normalized.BaselineReadyForMetric)
        {
            return false;
        }

        return Math.Abs(normalized.Z) > PortaExecutionConstants.BaselineUpdateGating.ZScoreMax
            || (boundsMature && normalized.WasWinsorized);
    }

    private static MetricBoundsRow? ReadMetricBounds(
        SqliteConnection connection,
        SqliteTransaction tx,
        string metricKey)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT p5, p95, bounds_sample_count, last_bounds_update_utc, samples_json, estimator_ver
            FROM metric_bounds
            WHERE metric_key = $metric_key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$metric_key", metricKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MetricBoundsRow(
            P5: reader.GetDouble(0),
            P95: reader.GetDouble(1),
            BoundsSampleCount: reader.GetInt32(2),
            LastBoundsUpdateUtc: reader.GetString(3),
            SamplesJson: reader.GetString(4),
            EstimatorVersion: reader.GetInt32(5));
    }

    private static void UpsertMetricBounds(
        SqliteConnection connection,
        SqliteTransaction tx,
        string metricKey,
        double sampleValue,
        string lastUpdateUtc,
        MetricBoundsRow? existing)
    {
        List<double> samples = existing.HasValue
            ? DeserializeSamples(existing.Value.SamplesJson)
            : new List<double>(capacity: PortaExecutionConstants.RobustNormalization.RollingBoundsBufferSize);
        samples.Add(ClampFiniteNonNegative(sampleValue));

        int capacity = PortaExecutionConstants.RobustNormalization.RollingBoundsBufferSize;
        if (samples.Count > capacity)
        {
            samples.RemoveRange(0, samples.Count - capacity);
        }

        double[] sorted = samples.OrderBy(static value => value).ToArray();
        double p5 = ComputeNearestRankPercentile(sorted, PortaExecutionConstants.RobustNormalization.BoundsP5Quantile);
        double p95 = ComputeNearestRankPercentile(sorted, PortaExecutionConstants.RobustNormalization.BoundsP95Quantile);
        if (p95 < p5)
        {
            (p5, p95) = (p95, p5);
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO metric_bounds (
                metric_key,
                p5,
                p95,
                bounds_sample_count,
                last_bounds_update_utc,
                samples_json,
                estimator_ver
            ) VALUES (
                $metric_key,
                $p5,
                $p95,
                $bounds_sample_count,
                $last_bounds_update_utc,
                $samples_json,
                $estimator_ver
            )
            ON CONFLICT(metric_key) DO UPDATE SET
                p5 = excluded.p5,
                p95 = excluded.p95,
                bounds_sample_count = excluded.bounds_sample_count,
                last_bounds_update_utc = excluded.last_bounds_update_utc,
                samples_json = excluded.samples_json,
                estimator_ver = excluded.estimator_ver;
            """;
        cmd.Parameters.AddWithValue("$metric_key", metricKey);
        cmd.Parameters.AddWithValue("$p5", p5);
        cmd.Parameters.AddWithValue("$p95", p95);
        cmd.Parameters.AddWithValue("$bounds_sample_count", samples.Count);
        cmd.Parameters.AddWithValue("$last_bounds_update_utc", lastUpdateUtc);
        cmd.Parameters.AddWithValue("$samples_json", JsonSerializer.Serialize(samples));
        cmd.Parameters.AddWithValue("$estimator_ver", PortaExecutionConstants.RobustNormalization.BoundsEstimatorVersion);
        cmd.ExecuteNonQuery();
    }

    private static List<double> DeserializeSamples(string? samplesJson)
    {
        if (string.IsNullOrWhiteSpace(samplesJson))
        {
            return [];
        }

        try
        {
            double[]? values = JsonSerializer.Deserialize<double[]>(samplesJson);
            if (values == null || values.Length == 0)
            {
                return [];
            }

            return values
                .Where(static value => double.IsFinite(value))
                .Select(ClampFiniteNonNegative)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static double ComputeNearestRankPercentile(IReadOnlyList<double> sortedAscending, double quantile)
    {
        if (sortedAscending.Count == 0)
        {
            return 0d;
        }

        double q = Math.Clamp(quantile, 0d, 1d);
        int rank = (int)Math.Ceiling(q * sortedAscending.Count);
        int index = Math.Clamp(rank - 1, 0, sortedAscending.Count - 1);
        return sortedAscending[index];
    }

    private static double ResolveComponentValue(
        IReadOnlyDictionary<string, MetricNormalizationValue> metrics,
        IReadOnlyList<string> keys,
        double fallback)
    {
        double sum = 0d;
        int count = 0;
        foreach (string key in keys)
        {
            if (!metrics.TryGetValue(key, out MetricNormalizationValue value))
            {
                continue;
            }

            if (!value.MetricReady)
            {
                continue;
            }

            // Step 9 contract: z -> sigmoid(1.2*z) for primitive unit-space conversion.
            double unit = 1d / (1d + Math.Exp(-(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * value.Z)));
            if (!double.IsFinite(unit))
            {
                continue;
            }

            sum += Math.Clamp(unit, 0d, 1d);
            count++;
        }

        if (count == 0)
        {
            return Math.Clamp(fallback, 0d, 1d);
        }

        return Math.Clamp(sum / count, 0d, 1d);
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
    private readonly record struct MetricBoundsRow(
        double P5,
        double P95,
        int BoundsSampleCount,
        string LastBoundsUpdateUtc,
        string SamplesJson,
        int EstimatorVersion);
}
