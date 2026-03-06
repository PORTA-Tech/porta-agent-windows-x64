using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests;

public sealed class DeterministicOutputHarnessTests
{
    [Fact]
    public void ScenarioRunner_DeepWorkHeldAcrossBuckets_SuppressesOnlyOnce()
    {
        using var env = new OutputActionScenarioEnvironment(
            startUtc: DateTimeOffset.Parse("2026-03-06T00:00:00.000+00:00"),
            seed: 19001,
            bucketSeconds: 5);
        env.BrightnessController.Supported = true;
        env.BrightnessController.CurrentLevel = 70;

        for (int i = 0; i < 24; i++)
        {
            env.Runner.Tick(new OutputActionStep(
                DeepWorkActive: true,
                OverloadActive: false));
        }

        Assert.True(env.Runner.NotificationRuntimeState.Active);
        Assert.Equal(1, env.NotificationController.GetSnapshotCalls);
        Assert.Equal(1, env.NotificationController.ApplyCalls);
        Assert.Equal(0, env.NotificationController.RestoreCalls);
        Assert.Equal(
            DateTimeOffset.Parse("2026-03-06T00:00:00.000+00:00"),
            env.NotificationController.Calls[0].TimestampUtc);
        Assert.Equal(
            DateTimeOffset.Parse("2026-03-06T00:00:00.000+00:00"),
            env.NotificationController.Calls[1].TimestampUtc);
    }

    [Fact]
    public void ScenarioRunner_OverloadHeld_RampsBrightnessOnePercentPerBucketToTarget()
    {
        using var env = new OutputActionScenarioEnvironment(
            startUtc: DateTimeOffset.Parse("2026-03-06T00:00:00.000+00:00"),
            seed: 19002,
            bucketSeconds: 5);
        env.BrightnessController.Supported = true;
        env.BrightnessController.CurrentLevel = 70;

        for (int i = 0; i < 6; i++)
        {
            env.Runner.Tick(new OutputActionStep(
                DeepWorkActive: false,
                OverloadActive: true));
        }

        int[] appliedLevels = env.BrightnessController.Calls
            .Where(c => string.Equals(c.Operation, nameof(FakeBrightnessController.SetLevel), StringComparison.Ordinal))
            .Select(c => c.LevelAfter)
            .ToArray();
        Assert.Equal([69, 68, 67, 66, 65], appliedLevels);
        Assert.Equal(5, env.BrightnessController.SetLevelCalls);
        Assert.True(env.Runner.BrightnessRuntimeState.Active);
        Assert.Equal(65, env.Runner.BrightnessRuntimeState.TargetLevel);
    }

    [Fact]
    public void ScenarioRunner_RestartMidAction_ReconcilesAndContinuesWithoutDuplicateEntry()
    {
        using var env = new OutputActionScenarioEnvironment(
            startUtc: DateTimeOffset.Parse("2026-03-06T00:00:00.000+00:00"),
            seed: 19003,
            bucketSeconds: 5);
        env.BrightnessController.Supported = true;
        env.BrightnessController.CurrentLevel = 70;

        env.Runner.Tick(new OutputActionStep(DeepWorkActive: true, OverloadActive: true));
        env.Runner.Tick(new OutputActionStep(DeepWorkActive: true, OverloadActive: true));
        int applyCallsBeforeRestart = env.NotificationController.ApplyCalls;
        int setCallsBeforeRestart = env.BrightnessController.SetLevelCalls;

        OutputActionRestartResult restart = env.Runner.Restart();
        Assert.False(restart.NotificationReconciliation.ShouldLogDiagnosticEvent);
        Assert.True(restart.BrightnessReconciliation.Reconciled);

        env.Runner.Tick(new OutputActionStep(DeepWorkActive: true, OverloadActive: true));

        Assert.Equal(applyCallsBeforeRestart, env.NotificationController.ApplyCalls);
        Assert.Equal(setCallsBeforeRestart + 1, env.BrightnessController.SetLevelCalls);
    }
}
