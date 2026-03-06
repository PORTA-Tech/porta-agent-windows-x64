using System.Text.Json;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests;

public sealed class DeepWorkSuppressionScenarioTests
{
    [Fact]
    public void Scenario_DeepWorkSustained_SuppressionTogglesExactlyOnce()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T00:00:00.000+00:00"),
            seed: 51019);

        var candidate = new ScriptedBucketInput(
            TypingBurstIntensity: 0.86d,
            ScrollVelocityIndex: 0.14d,
            FocusSwitchRate: 0.10d,
            AppHash: "app_focus",
            KeyboardKeydowns: 4,
            MouseMoves: 3,
            MouseWheels: 1,
            IdleSeconds: 0d);

        bool previousDeepWorkActive = false;
        int deepWorkActivations = 0;
        FinalizerTickObservation lastObservation = default;
        const int warmupBuckets = 220;
        for (int i = 0; i < warmupBuckets; i++)
        {
            // Deterministic broad warmup profile so robust bounds are not degenerate.
            double typing = 0.30d + (0.60d * runner.Random.NextDouble());
            double scroll = 0.15d + (0.70d * runner.Random.NextDouble());
            double focus = 0.15d + (0.70d * runner.Random.NextDouble());
            var warmup = new ScriptedBucketInput(
                TypingBurstIntensity: typing,
                ScrollVelocityIndex: scroll,
                FocusSwitchRate: focus,
                AppHash: "app_focus",
                KeyboardKeydowns: 4,
                MouseMoves: 3,
                MouseWheels: 1,
                IdleSeconds: 0d);

            lastObservation = runner.RunBucket(warmup);
            previousDeepWorkActive = ReadDeepWorkActive(lastObservation.LatestState?.StateFlagsJson);
        }

        const int maxBuckets = 180;
        for (int i = 0; i < maxBuckets; i++)
        {
            lastObservation = runner.RunBucket(candidate);
            bool deepWorkActive = ReadDeepWorkActive(lastObservation.LatestState?.StateFlagsJson);
            if (!previousDeepWorkActive && deepWorkActive)
            {
                deepWorkActivations++;
            }

            previousDeepWorkActive = deepWorkActive;
            if (deepWorkActive)
            {
                break;
            }
        }

        string debug = BuildDebugSummary(runner, lastObservation, deepWorkActivations, maxBuckets);
        Assert.True(deepWorkActivations == 1, debug);
        Assert.NotNull(lastObservation.LatestState);
        Assert.True(ReadDeepWorkActive(lastObservation.LatestState!.StateFlagsJson));
        Assert.True(lastObservation.LatestState.BaselineReady ?? false);
        Assert.True(lastObservation.LatestState.SustainedAttentionDurationSec >= PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds);

        int suppressedEventCountAtActivation = CountEvents(
            runner.ReadRecentEventRows(2000),
            "deep_work_notifications_suppressed");
        Assert.Equal(1, suppressedEventCountAtActivation);
        Assert.Equal(1, runner.NotificationController.ApplyCalls);
        Assert.True(runner.NotificationController.IsSuppressed);

        string? runtimeStateJson = runner.ReadRuntimeStateValue(PortaExecutionConstants.NotificationSuppression.RuntimeStateKey);
        Assert.False(string.IsNullOrWhiteSpace(runtimeStateJson));
        using (JsonDocument runtimeDoc = JsonDocument.Parse(runtimeStateJson!))
        {
            JsonElement root = runtimeDoc.RootElement;
            Assert.Equal(1, root.GetProperty("schema_ver").GetInt32());
            Assert.True(root.GetProperty("active").GetBoolean());
            Assert.True(root.TryGetProperty("snapshot", out JsonElement snapshotNode));
            Assert.Equal(JsonValueKind.Object, snapshotNode.ValueKind);
        }

        for (int i = 0; i < 30; i++)
        {
            lastObservation = runner.RunBucket(candidate);
            Assert.True(ReadDeepWorkActive(lastObservation.LatestState?.StateFlagsJson));
        }

        int suppressedEventCountAfterHold = CountEvents(
            runner.ReadRecentEventRows(2000),
            "deep_work_notifications_suppressed");
        Assert.Equal(1, suppressedEventCountAfterHold);
        Assert.Equal(1, runner.NotificationController.ApplyCalls);

        LocalApiProjectionSnapshot projection = runner.ReadLocalApiProjection();
        Assert.True(projection.Status.deep_work_active);
        Assert.True(projection.Status.notifications_suppressed);
        Assert.True(projection.Snapshot.deep_work_active);
        Assert.True(projection.Snapshot.notifications_suppressed);
    }

    private static bool ReadDeepWorkActive(string? stateFlagsJson)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("deep_work_active", out JsonElement flag))
            {
                return false;
            }

            return flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when flag.TryGetInt32(out int n) => n != 0,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static int CountEvents(
        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> rows,
        string action)
    {
        return rows.Count(row => string.Equals(row.Action, action, StringComparison.Ordinal));
    }

    private static string BuildDebugSummary(
        DeterministicFinalizerScenarioRunner runner,
        FinalizerTickObservation lastObservation,
        int deepWorkActivations,
        int maxBuckets)
    {
        string? deepWorkRuntime = runner.ReadRuntimeStateValue(PortaExecutionConstants.DeepWorkDetection.RuntimeStateKey);
        string? notificationRuntime = runner.ReadRuntimeStateValue(PortaExecutionConstants.NotificationSuppression.RuntimeStateKey);
        return
            $"DeepWork activation mismatch: activations={deepWorkActivations}, maxBuckets={maxBuckets}, " +
            $"latestState.ts={lastObservation.LatestState?.TimestampUtc:O}, " +
            $"latestState.sa={lastObservation.LatestState?.SustainedAttentionDurationSec:F1}, " +
            $"latestState.conf={lastObservation.LatestState?.Confidence:F3}, " +
            $"latestState.overload={lastObservation.LatestState?.OverloadLikelihood:F3}, " +
            $"latestState.flags={lastObservation.LatestState?.StateFlagsJson}, " +
            $"dwRuntime={deepWorkRuntime}, notifRuntime={notificationRuntime}, seed={runner.Seed}";
    }
}
