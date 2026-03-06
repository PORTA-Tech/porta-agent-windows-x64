using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests;

public sealed class BreakSuggestionIdempotencyScenarioTests
{
    private const int WarmupBuckets = 220;
    private const int SearchBuckets = 320;
    private static readonly ScriptedBucketInput BreakEligibleCandidate = new(
        TypingBurstIntensity: 0.98d,
        ScrollVelocityIndex: 0.98d,
        FocusSwitchRate: 0.10d,
        AppHash: "app_break",
        KeyboardKeydowns: 12,
        MouseMoves: 12,
        MouseWheels: 10,
        IdleSeconds: 0d);

    [Fact]
    public void Scenario_BreakSuggestionIdempotency_PreventsDuplicateBreakEventsWithinCooldownWindow()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T12:00:00.000+00:00"),
            seed: 71041);

        // Step M: deterministic bucket-driven warmup.
        for (int i = 0; i < WarmupBuckets; i++)
        {
            runner.RunBucket(BuildWarmupBucket(i));
        }

        // Pin robust normalization to the scripted overload candidate so activation is deterministic.
        runner.RunBucket(BreakEligibleCandidate);
        PinOverloadCandidateNormalization(runner.DbPath);

        FinalizerTickObservation activation = default;
        for (int i = 0; i < SearchBuckets; i++)
        {
            activation = runner.RunBucket(BreakEligibleCandidate);
            if (activation.LatestState?.OverloadActive ?? false)
            {
                break;
            }
        }

        Assert.True(
            activation.LatestState?.OverloadActive ?? false,
            $"Overload did not activate in {SearchBuckets} buckets (seed={runner.Seed}).");

        // Seed sustained attention near threshold so next eligible buckets cross break preconditions.
        SeedDerivedSessionRuntime(
            runner,
            sustainedAttentionSeconds: PortaExecutionConstants.BreakPolicy.SustainedAttentionSeconds - Time.bucketSeconds,
            dominantAppHash: BreakEligibleCandidate.AppHash,
            bucketSeq: activation.BucketSeq);

        int breakCountBefore = CountEvents(runner.ReadRecentEventRows(4000), "break_suggestion");
        int eligibleBucketsAfterFirstBreak = 0;
        int bucketsAfterFirstBreak = 0;
        FinalizerTickObservation? firstBreakObservation = null;

        for (int i = 0; i < 120; i++)
        {
            // Keep overload session active so this scenario stresses break cooldown/idempotency, not overload off-dwell.
            PinOverloadActiveRuntime(runner, activation.BucketSeq + i);

            FinalizerTickObservation observation = runner.RunBucket(BreakEligibleCandidate);
            bool breakEligible = IsBreakEligible(observation);
            int currentBreakCount = CountEvents(runner.ReadRecentEventRows(4000), "break_suggestion");

            if (firstBreakObservation is null && currentBreakCount == breakCountBefore + 1)
            {
                firstBreakObservation = observation;
            }
            else if (firstBreakObservation is not null)
            {
                bucketsAfterFirstBreak++;
                if (breakEligible)
                {
                    eligibleBucketsAfterFirstBreak++;
                }
            }

            if (firstBreakObservation is not null
                && bucketsAfterFirstBreak >= 8
                && eligibleBucketsAfterFirstBreak >= 3)
            {
                break;
            }
        }

        Assert.NotNull(firstBreakObservation);
        Assert.True(
            eligibleBucketsAfterFirstBreak >= 3,
            $"Expected multiple eligible buckets after first break event; got {eligibleBucketsAfterFirstBreak} (seed={runner.Seed}).");

        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> eventsAfterScenario =
            runner.ReadRecentEventRows(4000);
        int breakCountAfterScenario = CountEvents(eventsAfterScenario, "break_suggestion");
        Assert.Equal(breakCountBefore + 1, breakCountAfterScenario);

        string breakIdempotencyKey = eventsAfterScenario
            .First(row => string.Equals(row.Action, "break_suggestion", StringComparison.Ordinal))
            .IdempotencyKey;
        Assert.StartsWith("break_v1:", breakIdempotencyKey, StringComparison.Ordinal);

        string? cooldownJson = runner.ReadRuntimeStateValue(PortaExecutionConstants.BreakPolicy.RuntimeStateKey);
        Assert.False(string.IsNullOrWhiteSpace(cooldownJson));
        long cooldownUntilMs = ReadRuntimeInt64(cooldownJson, "break_cooldown_until_utc_ms");
        long lastFiredMs = ReadRuntimeInt64(cooldownJson, "break_last_fired_utc_ms");
        Assert.True(cooldownUntilMs > lastFiredMs);
        Assert.Equal(PortaExecutionConstants.BreakPolicy.CooldownWindowMs, cooldownUntilMs - lastFiredMs);

        // Strong extra assertion: replay finalize tick for the same bucket and sub-bucket offsets.
        DateTimeOffset replayAt = firstBreakObservation.Value.BucketEndUtc.AddMilliseconds(Time.finalizeDelayMs);
        runner.RunFinalizerTick(replayAt);
        runner.Clock.Advance(TimeSpan.FromSeconds(1));
        runner.RunFinalizerTick(runner.Clock.UtcNow);
        runner.Clock.Advance(TimeSpan.FromSeconds(2));
        runner.RunFinalizerTick(runner.Clock.UtcNow);

        int breakCountAfterReplay = CountEvents(runner.ReadRecentEventRows(4000), "break_suggestion");
        Assert.Equal(breakCountBefore + 1, breakCountAfterReplay);
    }

    private static bool IsBreakEligible(FinalizerTickObservation observation)
    {
        AgentLatestStateDetails? state = observation.LatestState;
        if (state is null)
        {
            return false;
        }

        return state.BaselineReady == true
            && state.OverloadActive
            && state.Confidence >= PortaExecutionConstants.BreakPolicy.ConfidenceMin
            && state.SustainedAttentionDurationSec >= PortaExecutionConstants.BreakPolicy.SustainedAttentionSeconds;
    }

    private static ScriptedBucketInput BuildWarmupBucket(int bucketIndex)
    {
        double phaseA = (bucketIndex % 17) / 16d;
        double phaseB = ((bucketIndex * 3) % 17) / 16d;
        return new ScriptedBucketInput(
            TypingBurstIntensity: 0.84d + (0.12d * phaseA),
            ScrollVelocityIndex: 0.02d + (0.06d * phaseA),
            FocusSwitchRate: 0.02d + (0.06d * phaseB),
            AppHash: "app_break",
            KeyboardKeydowns: 3,
            MouseMoves: 2,
            MouseWheels: 1,
            IdleSeconds: 0d);
    }

    private static void SeedDerivedSessionRuntime(
        DeterministicFinalizerScenarioRunner runner,
        int sustainedAttentionSeconds,
        string dominantAppHash,
        long bucketSeq)
    {
        string json = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.DerivedSessionMetrics.ContractVersion,
            bucket_seq = bucketSeq,
            sa_seconds = sustainedAttentionSeconds,
            sa_context_app_hash = dominantAppHash,
            sa_last_update_bucket_seq = bucketSeq,
            sa_freeze_reason = string.Empty,
            sa_untrusted = false,
            sustained_attention_duration_sec = sustainedAttentionSeconds,
            idle_fragmentation_index = 0d,
            partial = false,
            dominant_app_hash = dominantAppHash
        });
        runner.UpsertRuntimeStateValue(
            PortaExecutionConstants.DerivedSessionMetrics.RuntimeStateKey,
            json,
            runner.Clock.UtcNow);
    }

    private static void PinOverloadActiveRuntime(DeterministicFinalizerScenarioRunner runner, long bucketSeq)
    {
        long nowMs = runner.Clock.UtcNow.ToUnixTimeMilliseconds();
        string overloadStateJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.OverloadStabilization.ContractVersion,
            overload_active = true,
            on_dwell_ms = PortaExecutionConstants.OverloadStabilization.DwellOnSeconds * 1000,
            off_dwell_ms = 0,
            last_good_ts_ms = nowMs,
            last_bucket_seq = bucketSeq,
            last_transition_ts_ms = nowMs
        });
        string overloadActiveJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = PortaExecutionConstants.OverloadStabilization.ContractVersion,
            value = true,
            overload_active = true,
            bucket_seq = bucketSeq,
            updated_utc = runner.Clock.UtcNow.ToString("O")
        });
        runner.UpsertRuntimeStateValue(
            PortaExecutionConstants.OverloadStabilization.RuntimeStateKey,
            overloadStateJson,
            runner.Clock.UtcNow);
        runner.UpsertRuntimeStateValue(
            PortaExecutionConstants.OverloadStabilization.ActiveRuntimeStateKey,
            overloadActiveJson,
            runner.Clock.UtcNow);
    }

    private static int CountEvents(
        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> rows,
        string action)
    {
        return rows.Count(row => string.Equals(row.Action, action, StringComparison.Ordinal));
    }

    private static long ReadRuntimeInt64(string? valueJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return 0;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(valueJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(propertyName, out JsonElement node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetInt64(out long parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static void PinOverloadCandidateNormalization(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        Dictionary<string, double> latest = ReadLatestInputMetrics(connection);
        if (latest.Count == 0)
        {
            return;
        }

        string updatedUtc = DateTimeOffset.UtcNow.ToString("O");
        using SqliteTransaction tx = connection.BeginTransaction();
        foreach ((string metricKey, double rawValue) in latest)
        {
            double transformed = TransformForBaselineMetric(metricKey, rawValue);
            double p95 = Math.Max(transformed, 0.001d);
            double mean = Math.Max(0d, p95 * 0.10d);
            double std = Math.Max(0.05d, p95 * 0.05d);
            int sampleCount = 100_000;
            double m2 = std * std * sampleCount;

            using (SqliteCommand baselineCmd = connection.CreateCommand())
            {
                baselineCmd.Transaction = tx;
                baselineCmd.CommandText =
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
                baselineCmd.Parameters.AddWithValue("$metric_key", metricKey);
                baselineCmd.Parameters.AddWithValue("$mean", mean);
                baselineCmd.Parameters.AddWithValue("$std", std);
                baselineCmd.Parameters.AddWithValue("$sample_count", sampleCount);
                baselineCmd.Parameters.AddWithValue("$m2", m2);
                baselineCmd.Parameters.AddWithValue("$last_update_utc", updatedUtc);
                baselineCmd.ExecuteNonQuery();
            }

            using (SqliteCommand boundsCmd = connection.CreateCommand())
            {
                boundsCmd.Transaction = tx;
                boundsCmd.CommandText =
                    """
                    INSERT INTO metric_bounds (metric_key, p5, p95, bounds_sample_count, last_bounds_update_utc, samples_json, estimator_ver)
                    VALUES ($metric_key, $p5, $p95, $bounds_sample_count, $last_bounds_update_utc, $samples_json, $estimator_ver)
                    ON CONFLICT(metric_key) DO UPDATE SET
                        p5 = excluded.p5,
                        p95 = excluded.p95,
                        bounds_sample_count = excluded.bounds_sample_count,
                        last_bounds_update_utc = excluded.last_bounds_update_utc,
                        samples_json = excluded.samples_json,
                        estimator_ver = excluded.estimator_ver;
                    """;
                boundsCmd.Parameters.AddWithValue("$metric_key", metricKey);
                boundsCmd.Parameters.AddWithValue("$p5", 0d);
                boundsCmd.Parameters.AddWithValue("$p95", p95);
                boundsCmd.Parameters.AddWithValue("$bounds_sample_count", 600);
                boundsCmd.Parameters.AddWithValue("$last_bounds_update_utc", updatedUtc);
                boundsCmd.Parameters.AddWithValue("$samples_json", BuildPinnedBoundsSamplesJson(p95));
                boundsCmd.Parameters.AddWithValue("$estimator_ver", PortaExecutionConstants.RobustNormalization.BoundsEstimatorVersion);
                boundsCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static Dictionary<string, double> ReadLatestInputMetrics(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT keyboard_iki_mean,
                   keyboard_iki_cv,
                   keyboard_backspace_rate,
                   mouse_speed_mean,
                   mouse_jerk_p95,
                   mouse_scroll_speed,
                   focus_switch_rate,
                   focus_app_entropy,
                   focus_active_app_count
            FROM input_buckets
            ORDER BY timestamp_utc DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["keyboard_iki_mean"] = reader.GetDouble(0),
            ["keyboard_iki_cv"] = reader.GetDouble(1),
            ["keyboard_backspace_rate"] = reader.GetDouble(2),
            ["mouse_speed_mean"] = reader.GetDouble(3),
            ["mouse_jerk_p95"] = reader.GetDouble(4),
            ["mouse_scroll_speed"] = reader.GetDouble(5),
            ["focus_switch_rate"] = reader.GetDouble(6),
            ["focus_app_entropy"] = reader.GetDouble(7),
            ["focus_active_app_count"] = reader.GetDouble(8)
        };
    }

    private static string BuildPinnedBoundsSamplesJson(double p95)
    {
        var samples = new List<double>(600);
        for (int i = 0; i < 560; i++)
        {
            samples.Add(0d);
        }

        for (int i = 0; i < 40; i++)
        {
            samples.Add(p95);
        }

        return JsonSerializer.Serialize(samples);
    }

    private static double TransformForBaselineMetric(string metricKey, double raw)
    {
        double clamped = Math.Max(0d, raw);
        return metricKey switch
        {
            "keyboard_iki_mean" => Math.Log(1d + clamped),
            "mouse_speed_mean" => Math.Log(1d + clamped),
            "mouse_jerk_p95" => Math.Log(1d + clamped),
            "mouse_scroll_speed" => Math.Log(1d + clamped),
            _ => clamped
        };
    }
}
