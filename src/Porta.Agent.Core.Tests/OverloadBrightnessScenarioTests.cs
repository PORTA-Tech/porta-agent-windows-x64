using Microsoft.Data.Sqlite;
using System.Text.Json;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests;

public sealed class OverloadBrightnessScenarioTests
{
    private const int WarmupBuckets = 220;
    private static readonly ScriptedBucketInput OverloadCandidate = new(
        TypingBurstIntensity: 0.05d,
        ScrollVelocityIndex: 1.00d,
        FocusSwitchRate: 1.00d,
        AppHash: "app_stress",
        KeyboardKeydowns: 14,
        MouseMoves: 12,
        MouseWheels: 10,
        IdleSeconds: 0d);
    private static readonly ScriptedBucketInput RecoveryCandidate = new(
        TypingBurstIntensity: 0.92d,
        ScrollVelocityIndex: 0.02d,
        FocusSwitchRate: 0.02d,
        AppHash: "app_recovery",
        KeyboardKeydowns: 3,
        MouseMoves: 1,
        MouseWheels: 0,
        IdleSeconds: 0d);

    [Fact]
    public void Scenario_OverloadEnters_BrightnessReducesGraduallyWithoutDuplicates()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T06:00:00.000+00:00"),
            seed: 61031);

        OverloadActivationOutcome activation = ActivateOverload(runner);
        FinalizerTickObservation last = activation.LastObservation;
        Assert.True(activation.IsActive, activation.Debug);
        Assert.Equal(70, last.BrightnessStatus.PreviousLevel);
        Assert.Equal(65, last.BrightnessStatus.TargetLevel);

        for (int i = 0; i < 25; i++)
        {
            last = runner.RunBucket(OverloadCandidate);
            Assert.True(last.LatestState?.OverloadActive ?? false);
        }

        int[] sequence = runner.BrightnessController.Calls
            .Where(c => string.Equals(c.Operation, nameof(FakeBrightnessController.SetLevel), StringComparison.Ordinal))
            .Where(c => c.Succeeded)
            .Select(c => c.LevelAfter)
            .ToArray();
        Assert.Equal([69, 68, 67, 66, 65], sequence);
        Assert.Equal(5, runner.BrightnessController.SetLevelCalls);
        Assert.Equal(5, runner.BrightnessController.LevelChangeCount);

        int reducedEvents = CountEvents(
            runner.ReadRecentEventRows(4000),
            "overload_brightness_reduced");
        Assert.Equal(1, reducedEvents);

        for (int i = 0; i < 20; i++)
        {
            last = runner.RunBucket(OverloadCandidate);
        }

        int reducedEventsAfterHold = CountEvents(
            runner.ReadRecentEventRows(4000),
            "overload_brightness_reduced");
        Assert.Equal(1, reducedEventsAfterHold);
        Assert.Equal(5, runner.BrightnessController.SetLevelCalls);
    }

    [Fact]
    public void Scenario_OverloadExits_RampsUpRestoresAndStartsCooldown()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T08:00:00.000+00:00"),
            seed: 61032);

        OverloadActivationOutcome activation = ActivateOverload(runner);
        FinalizerTickObservation last = activation.LastObservation;
        Assert.True(activation.IsActive, activation.Debug);

        // Ensure the reduction ramp has completed before testing restore behavior.
        for (int i = 0; i < 20; i++)
        {
            if (last.BrightnessStatus.CurrentLevel == last.BrightnessStatus.TargetLevel)
            {
                break;
            }

            last = runner.RunBucket(OverloadCandidate);
        }

        Assert.Equal(last.BrightnessStatus.TargetLevel, last.BrightnessStatus.CurrentLevel);
        Assert.True(last.BrightnessStatus.Active);

        int setCallsBeforeRestore = runner.BrightnessController.SetLevelCalls;
        bool sawOverloadDeactivate = false;
        bool sawRampUp = false;
        for (int i = 0; i < 80; i++)
        {
            last = runner.RunBucket(RecoveryCandidate);
            bool overloadActive = last.LatestState?.OverloadActive ?? false;
            if (!overloadActive)
            {
                sawOverloadDeactivate = true;
            }

            if (last.BrightnessStatus.Active
                && string.Equals(last.BrightnessStatus.RampDirection, "up", StringComparison.Ordinal))
            {
                sawRampUp = true;
            }

            if (!last.BrightnessStatus.Active
                && last.BrightnessStatus.CurrentLevel == last.BrightnessStatus.PreviousLevel)
            {
                break;
            }
        }

        Assert.True(sawOverloadDeactivate);
        Assert.True(sawRampUp);
        Assert.False(last.BrightnessStatus.Active);
        Assert.Equal(last.BrightnessStatus.PreviousLevel, last.BrightnessStatus.CurrentLevel);
        Assert.NotNull(last.BrightnessStatus.CooldownUntilUtc);
        Assert.True(last.BrightnessStatus.CooldownUntilUtc > last.BucketEndUtc);

        int[] restoreSequence = runner.BrightnessController.Calls
            .Where(c => string.Equals(c.Operation, nameof(FakeBrightnessController.SetLevel), StringComparison.Ordinal))
            .Where(c => c.Succeeded)
            .Skip(setCallsBeforeRestore)
            .Select(c => c.LevelAfter)
            .ToArray();
        Assert.Equal([66, 67, 68, 69, 70], restoreSequence);

        int restoredEvents = CountEvents(
            runner.ReadRecentEventRows(4000),
            "overload_brightness_restored");
        Assert.Equal(1, restoredEvents);
    }

    [Fact]
    public void Scenario_RestartMidBrightnessRamp_ReconcilesContinuesAndRestoresWithoutDuplicateReducedEvent()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T10:00:00.000+00:00"),
            seed: 61033);

        OverloadActivationOutcome activation = ActivateOverload(runner);
        FinalizerTickObservation last = activation.LastObservation;
        Assert.True(activation.IsActive, activation.Debug);

        // Move into a clear mid-ramp-down state.
        last = runner.RunBucket(OverloadCandidate);
        Assert.True(last.BrightnessStatus.Active);
        Assert.True(last.BrightnessStatus.CurrentLevel > last.BrightnessStatus.TargetLevel);
        Assert.Equal("down", last.BrightnessStatus.RampDirection);

        int reducedEventsBeforeRestart = CountEvents(
            runner.ReadRecentEventRows(4000),
            "overload_brightness_reduced");
        Assert.Equal(1, reducedEventsBeforeRestart);

        // Simulate OS-level drift while service is down; startup reconcile should align runtime_state.
        runner.BrightnessController.CurrentLevel = Math.Max(
            last.BrightnessStatus.TargetLevel,
            runner.BrightnessController.CurrentLevel - 1);
        int osLevelBeforeRestart = runner.BrightnessController.CurrentLevel;

        runner.Restart();

        int runtimeCurrentAfterRestart = ReadRuntimeInt(
            runner.ReadRuntimeStateValue(PortaExecutionConstants.BrightnessAdjustment.RuntimeStateKey),
            "current_level");
        Assert.Equal(osLevelBeforeRestart, runtimeCurrentAfterRestart);

        for (int i = 0; i < 20; i++)
        {
            last = runner.RunBucket(OverloadCandidate);
            if (last.BrightnessStatus.CurrentLevel == last.BrightnessStatus.TargetLevel)
            {
                break;
            }
        }

        Assert.Equal(last.BrightnessStatus.TargetLevel, last.BrightnessStatus.CurrentLevel);
        int reducedEventsAfterRestart = CountEvents(
            runner.ReadRecentEventRows(4000),
            "overload_brightness_reduced");
        Assert.Equal(1, reducedEventsAfterRestart);

        bool sawRampUp = false;
        for (int i = 0; i < 80; i++)
        {
            last = runner.RunBucket(RecoveryCandidate);
            if (last.BrightnessStatus.Active
                && string.Equals(last.BrightnessStatus.RampDirection, "up", StringComparison.Ordinal))
            {
                sawRampUp = true;
            }

            if (!last.BrightnessStatus.Active
                && last.BrightnessStatus.CurrentLevel == last.BrightnessStatus.PreviousLevel)
            {
                break;
            }
        }

        Assert.True(sawRampUp);
        Assert.False(last.BrightnessStatus.Active);
        Assert.Equal(last.BrightnessStatus.PreviousLevel, last.BrightnessStatus.CurrentLevel);
        int restoredEvents = CountEvents(
            runner.ReadRecentEventRows(4000),
            "overload_brightness_restored");
        Assert.Equal(1, restoredEvents);
    }

    private static ScriptedBucketInput BuildWarmupBucket(int bucketIndex)
    {
        double phaseA = (bucketIndex % 17) / 16d;
        double phaseB = ((bucketIndex * 3) % 17) / 16d;
        double typing = 0.84d + (0.12d * phaseA);
        double scroll = 0.02d + (0.06d * phaseA);
        double focus = 0.02d + (0.06d * phaseB);
        return new ScriptedBucketInput(
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: scroll,
            FocusSwitchRate: focus,
            AppHash: "app_stress",
            KeyboardKeydowns: 3,
            MouseMoves: 2,
            MouseWheels: 1,
            IdleSeconds: 0d);
    }

    private static int CountEvents(
        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> rows,
        string action)
    {
        return rows.Count(row => string.Equals(row.Action, action, StringComparison.Ordinal));
    }

    private static OverloadActivationOutcome ActivateOverload(DeterministicFinalizerScenarioRunner runner)
    {
        bool previousOverloadActive = false;
        for (int i = 0; i < WarmupBuckets; i++)
        {
            FinalizerTickObservation warm = runner.RunBucket(BuildWarmupBucket(i));
            previousOverloadActive = warm.LatestState?.OverloadActive ?? false;
        }

        runner.RunBucket(OverloadCandidate);
        PinOverloadCandidateNormalization(runner.DbPath);

        int overloadActivations = 0;
        FinalizerTickObservation last = default;
        double maxOverload = 0d;
        double maxConfidence = 0d;
        const int searchBuckets = 320;
        for (int i = 0; i < searchBuckets; i++)
        {
            last = runner.RunBucket(OverloadCandidate);
            bool overloadActive = last.LatestState?.OverloadActive ?? false;
            maxOverload = Math.Max(maxOverload, last.LatestState?.OverloadLikelihood ?? 0d);
            maxConfidence = Math.Max(maxConfidence, last.LatestState?.Confidence ?? 0d);
            if (!previousOverloadActive && overloadActive)
            {
                overloadActivations++;
            }

            previousOverloadActive = overloadActive;
            if (overloadActive && last.BrightnessStatus.Active)
            {
                break;
            }
        }

        string debug = $"overload_activations={overloadActivations} search={searchBuckets} "
            + $"last_overload={last.LatestState?.OverloadLikelihood:F3} "
            + $"last_conf={last.LatestState?.Confidence:F3} "
            + $"max_overload={maxOverload:F3} "
            + $"max_conf={maxConfidence:F3} "
            + $"last_active={last.LatestState?.OverloadActive} "
            + $"seed={runner.Seed}";
        return new OverloadActivationOutcome(
            IsActive: last.LatestState?.OverloadActive ?? false,
            LastObservation: last,
            Debug: debug);
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

    private static int ReadRuntimeInt(string? valueJson, string propertyName)
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
                && node.TryGetInt32(out int parsed))
            {
                return parsed;
            }
        }
        catch
        {
        }

        return 0;
    }

    private readonly record struct OverloadActivationOutcome(
        bool IsActive,
        FinalizerTickObservation LastObservation,
        string Debug);
}
