using System.Text.Json;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests;

public sealed class DeepWorkSuppressionScenarioTests
{
    private const int WarmupBuckets = 220;
    private static readonly ScriptedBucketInput DeepWorkCandidate = new(
        TypingBurstIntensity: 0.86d,
        ScrollVelocityIndex: 0.14d,
        FocusSwitchRate: 0.10d,
        AppHash: "app_focus",
        KeyboardKeydowns: 4,
        MouseMoves: 3,
        MouseWheels: 1,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput DeepWorkExit = new(
        TypingBurstIntensity: 0.92d,
        ScrollVelocityIndex: 0.95d,
        FocusSwitchRate: 0.95d,
        AppHash: "app_disrupt",
        KeyboardKeydowns: 10,
        MouseMoves: 9,
        MouseWheels: 7,
        IdleSeconds: 0d);

    [Fact]
    public void Scenario_DeepWorkSustained_SuppressionTogglesExactlyOnce()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T00:00:00.000+00:00"),
            seed: 51019);

        ActivationOutcome activation = ActivateDeepWork(runner);
        FinalizerTickObservation lastObservation = activation.LastObservation;

        string debug = BuildDebugSummary(runner, lastObservation, activation.ActivationCount, activation.SearchBuckets);
        Assert.True(activation.ActivationCount == 1, debug);
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
            lastObservation = runner.RunBucket(DeepWorkCandidate);
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

    [Fact]
    public void Scenario_DeepWorkExitAfterMinOn_RestoresExactlyOnce()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T02:00:00.000+00:00"),
            seed: 51020);

        ActivationOutcome activation = ActivateDeepWork(runner);
        Assert.True(activation.IsActive, BuildDebugSummary(runner, activation.LastObservation, activation.ActivationCount, activation.SearchBuckets));

        // Keep deep work active for >= min-on (120s) to allow immediate restore once exit stabilization triggers.
        for (int i = 0; i < 24; i++)
        {
            FinalizerTickObservation hold = runner.RunBucket(DeepWorkCandidate);
            Assert.True(ReadDeepWorkActive(hold.LatestState?.StateFlagsJson));
        }

        int deactivations = 0;
        bool previousDeepWorkActive = true;
        FinalizerTickObservation last = default;
        for (int i = 0; i < 40; i++)
        {
            last = runner.RunBucket(DeepWorkExit);
            bool deepWorkActive = ReadDeepWorkActive(last.LatestState?.StateFlagsJson);
            if (previousDeepWorkActive && !deepWorkActive)
            {
                deactivations++;
            }

            previousDeepWorkActive = deepWorkActive;
            if (!deepWorkActive && !last.NotificationStatus.Active)
            {
                break;
            }
        }

        Assert.Equal(1, deactivations);
        Assert.Equal(1, runner.NotificationController.RestoreCalls);
        Assert.False(runner.NotificationController.IsSuppressed);

        int restoredEvents = CountEvents(
            runner.ReadRecentEventRows(2000),
            "deep_work_notifications_restored");
        Assert.Equal(1, restoredEvents);

        string? runtimeStateJson = runner.ReadRuntimeStateValue(PortaExecutionConstants.NotificationSuppression.RuntimeStateKey);
        Assert.False(string.IsNullOrWhiteSpace(runtimeStateJson));
        using JsonDocument runtimeDoc = JsonDocument.Parse(runtimeStateJson!);
        JsonElement root = runtimeDoc.RootElement;
        Assert.False(root.GetProperty("active").GetBoolean());
        Assert.True(root.TryGetProperty("snapshot", out JsonElement snapshotNode));
        Assert.Equal(JsonValueKind.Null, snapshotNode.ValueKind);
    }

    [Fact]
    public void Scenario_DeepWorkExitBeforeMinOn_DelaysRestoreUntilMinOnExpires()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T04:00:00.000+00:00"),
            seed: 51021);

        ActivationOutcome activation = ActivateDeepWork(runner);
        Assert.True(activation.IsActive, BuildDebugSummary(runner, activation.LastObservation, activation.ActivationCount, activation.SearchBuckets));

        // Exit signals are EMA-smoothed, so the off-dwell only starts accumulating once the smoothed
        // components cross the exit thresholds. Deactivation therefore lands a couple of buckets past
        // the raw DwellOff window (~bucket 13: one smoothing-lag bucket + 60s / 12-bucket DwellOff),
        // but still well inside the 120s (24-bucket) min-on window. Drive exit buckets until
        // deep_work_active turns off rather than hard-coding an exact bucket count.
        const int MinOnBuckets = 24; // 120s min-on at 5s buckets.
        int deactivations = 0;
        bool previousDeepWorkActive = true;
        FinalizerTickObservation last = default;
        for (int i = 0; i < MinOnBuckets - 1; i++)
        {
            last = runner.RunBucket(DeepWorkExit);
            bool deepWorkActive = ReadDeepWorkActive(last.LatestState?.StateFlagsJson);
            if (previousDeepWorkActive && !deepWorkActive)
            {
                deactivations++;
                break;
            }

            previousDeepWorkActive = deepWorkActive;
        }

        // deep_work_active turned off before the min-on window elapsed...
        Assert.Equal(1, deactivations);
        // ...so restore must be deferred: no restore call yet, suppression still active, pending flagged.
        Assert.Equal(0, runner.NotificationController.RestoreCalls);
        Assert.True(last.NotificationStatus.Active);
        Assert.True(last.NotificationStatus.RestorePending);

        // Keep driving exit buckets until the min-on window expires; restore then fires exactly once.
        for (int i = 0; i < 40; i++)
        {
            last = runner.RunBucket(DeepWorkExit);
            if (runner.NotificationController.RestoreCalls > 0)
            {
                break;
            }
        }

        Assert.Equal(1, runner.NotificationController.RestoreCalls);
        Assert.False(last.NotificationStatus.Active);
        Assert.False(last.NotificationStatus.RestorePending);

        int restoredEvents = CountEvents(
            runner.ReadRecentEventRows(2000),
            "deep_work_notifications_restored");
        Assert.Equal(1, restoredEvents);
    }

    [Fact]
    public void Scenario_RestartDuringNotificationSuppression_ReconcilesWithoutDuplicateAndRestoresOnExit()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T06:00:00.000+00:00"),
            seed: 51022);

        ActivationOutcome activation = ActivateDeepWork(runner);
        FinalizerTickObservation last = activation.LastObservation;
        Assert.True(activation.IsActive, BuildDebugSummary(runner, activation.LastObservation, activation.ActivationCount, activation.SearchBuckets));

        for (int i = 0; i < 10; i++)
        {
            last = runner.RunBucket(DeepWorkCandidate);
            Assert.True(ReadDeepWorkActive(last.LatestState?.StateFlagsJson));
        }

        int suppressedEventsBeforeRestart = CountEvents(
            runner.ReadRecentEventRows(2000),
            "deep_work_notifications_suppressed");
        int applyCallsBeforeRestart = runner.NotificationController.ApplyCalls;
        Assert.True(last.NotificationStatus.Active);
        Assert.True(ReadRuntimeBool(
            runner.ReadRuntimeStateValue(PortaExecutionConstants.NotificationSuppression.RuntimeStateKey),
            "active"));
        Assert.True(HasRuntimeSnapshot(
            runner.ReadRuntimeStateValue(PortaExecutionConstants.NotificationSuppression.RuntimeStateKey)));

        runner.Restart();

        Assert.Equal(applyCallsBeforeRestart, runner.NotificationController.ApplyCalls);
        int suppressedEventsAfterRestart = CountEvents(
            runner.ReadRecentEventRows(2000),
            "deep_work_notifications_suppressed");
        Assert.Equal(suppressedEventsBeforeRestart, suppressedEventsAfterRestart);
        Assert.True(ReadRuntimeBool(
            runner.ReadRuntimeStateValue(PortaExecutionConstants.NotificationSuppression.RuntimeStateKey),
            "active"));

        // Ensure min-on window elapsed before exit path.
        for (int i = 0; i < 24; i++)
        {
            last = runner.RunBucket(DeepWorkCandidate);
            Assert.True(ReadDeepWorkActive(last.LatestState?.StateFlagsJson));
        }

        int deactivations = 0;
        bool previousDeepWorkActive = true;
        for (int i = 0; i < 50; i++)
        {
            last = runner.RunBucket(DeepWorkExit);
            bool deepWorkActive = ReadDeepWorkActive(last.LatestState?.StateFlagsJson);
            if (previousDeepWorkActive && !deepWorkActive)
            {
                deactivations++;
            }

            previousDeepWorkActive = deepWorkActive;
            if (!deepWorkActive && !last.NotificationStatus.Active)
            {
                break;
            }
        }

        Assert.Equal(1, deactivations);
        Assert.Equal(1, runner.NotificationController.RestoreCalls);
        Assert.False(last.NotificationStatus.Active);
        Assert.False(last.NotificationStatus.RestorePending);

        int restoredEvents = CountEvents(
            runner.ReadRecentEventRows(2000),
            "deep_work_notifications_restored");
        Assert.Equal(1, restoredEvents);
    }

    [Fact]
    public void Scenario_NotificationApplyFailure_SetsBackoffLogsFailureAndDoesNotRetryAggressively()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T09:00:00.000+00:00"),
            seed: 51023);
        runner.NotificationController.FailNextApply = true;

        ActivationOutcome activation = ActivateDeepWork(runner);
        FinalizerTickObservation last = activation.LastObservation;
        Assert.True(activation.IsActive, BuildDebugSummary(runner, activation.LastObservation, activation.ActivationCount, activation.SearchBuckets));

        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> eventsAfterActivation =
            runner.ReadRecentEventRows(4000);
        int failureEvents = CountEvents(eventsAfterActivation, "notification_suppress_failed");
        Assert.Equal(1, failureEvents);
        ScenarioAssertions.AssertEventsDescendingByTimestamp(eventsAfterActivation);

        Assert.Equal(1, runner.NotificationController.GetSnapshotCalls);
        Assert.Equal(1, runner.NotificationController.ApplyCalls);
        Assert.Equal(0, runner.NotificationController.RestoreCalls);
        ScenarioAssertions.AssertTimestampsNonDecreasing(
            runner.NotificationController.Calls.Select(c => c.TimestampUtc));

        Assert.False(last.NotificationStatus.Ready);
        Assert.Equal("suppression_apply_failed", last.NotificationStatus.LastError);
        Assert.NotNull(last.NotificationStatus.BackoffUntilUtc);
        Assert.True(last.NotificationStatus.BackoffUntilUtc > last.BucketEndUtc);

        for (int i = 0; i < 24; i++)
        {
            last = runner.RunBucket(DeepWorkCandidate);
            Assert.True(ReadDeepWorkActive(last.LatestState?.StateFlagsJson));
        }

        int failureEventsAfterHold = CountEvents(
            runner.ReadRecentEventRows(4000),
            "notification_suppress_failed");
        Assert.Equal(1, failureEventsAfterHold);
        Assert.Equal(1, runner.NotificationController.ApplyCalls);

        LocalApiProjectionSnapshot projection = runner.ReadLocalApiProjection();
        Assert.True(projection.Status.deep_work_active);
        Assert.False(projection.Status.notifications_suppressed);
        Assert.False(projection.Status.NotificationSuppressionReady);
        Assert.Equal("suppression_apply_failed", projection.Status.NotificationSuppressionError);
        Assert.NotNull(projection.Status.NotificationSuppressionBackoffUntilUtc);

        ScenarioAssertions.AssertRuntimeStateRowCount(
            runner.DbPath,
            PortaExecutionConstants.NotificationSuppression.RuntimeStateKey);
        ScenarioAssertions.AssertLatestStateBucketJsonContracts(runner.DbPath);
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

    private static bool ReadRuntimeBool(string? valueJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(valueJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(propertyName, out JsonElement node)
                && (node.ValueKind == JsonValueKind.True || node.ValueKind == JsonValueKind.False))
            {
                return node.GetBoolean();
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool HasRuntimeSnapshot(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(valueJson);
            JsonElement root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("snapshot", out JsonElement node)
                && node.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static ActivationOutcome ActivateDeepWork(DeterministicFinalizerScenarioRunner runner)
    {
        FinalizerTickObservation last = default;
        bool previousDeepWorkActive = false;
        for (int i = 0; i < WarmupBuckets; i++)
        {
            last = runner.RunBucket(BuildWarmupBucket(runner));
            previousDeepWorkActive = ReadDeepWorkActive(last.LatestState?.StateFlagsJson);
        }

        int activations = 0;
        const int searchBuckets = 320;
        for (int i = 0; i < searchBuckets; i++)
        {
            last = runner.RunBucket(DeepWorkCandidate);
            bool deepWorkActive = ReadDeepWorkActive(last.LatestState?.StateFlagsJson);
            if (!previousDeepWorkActive && deepWorkActive)
            {
                activations++;
            }

            previousDeepWorkActive = deepWorkActive;
            if (deepWorkActive)
            {
                break;
            }
        }

        return new ActivationOutcome(
            IsActive: ReadDeepWorkActive(last.LatestState?.StateFlagsJson),
            ActivationCount: activations,
            LastObservation: last,
            SearchBuckets: searchBuckets);
    }

    private static ScriptedBucketInput BuildWarmupBucket(DeterministicFinalizerScenarioRunner runner)
    {
        double typing = 0.30d + (0.60d * runner.Random.NextDouble());
        double scroll = 0.15d + (0.70d * runner.Random.NextDouble());
        double focus = 0.15d + (0.70d * runner.Random.NextDouble());
        return new ScriptedBucketInput(
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: scroll,
            FocusSwitchRate: focus,
            AppHash: "app_focus",
            KeyboardKeydowns: 4,
            MouseMoves: 3,
            MouseWheels: 1,
            IdleSeconds: 0d);
    }

    private readonly record struct ActivationOutcome(
        bool IsActive,
        int ActivationCount,
        FinalizerTickObservation LastObservation,
        int SearchBuckets);
}
