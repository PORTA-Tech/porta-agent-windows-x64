using System.Text.Json;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests;

public sealed class Step20AcceptanceGatesTests
{
    private const int WarmupBuckets = 220;
    private const int ScenarioBucketsPerHour = 3600 / Time.bucketSeconds; // 720 at 5s buckets.
    private const int DeepWorkTransitionGatePerHour = 3;

    // Focused candidate aligned with Step 14 deep-work bounds.
    private static readonly ScriptedBucketInput FocusedWork = new(
        TypingBurstIntensity: 0.86d,
        ScrollVelocityIndex: 0.14d,
        FocusSwitchRate: 0.10d,
        AppHash: "app_focus",
        KeyboardKeydowns: 4,
        MouseMoves: 3,
        MouseWheels: 1,
        IdleSeconds: 0d);

    // Interruptions/instability candidate aligned with deep-work exit path.
    private static readonly ScriptedBucketInput Interrupted = new(
        TypingBurstIntensity: 0.92d,
        ScrollVelocityIndex: 0.95d,
        FocusSwitchRate: 0.95d,
        AppHash: "app_interrupt",
        KeyboardKeydowns: 10,
        MouseMoves: 9,
        MouseWheels: 7,
        IdleSeconds: 0d);

    // Mixed normal usage (not stable enough for sustained deep work).
    private static readonly ScriptedBucketInput MixedWork = new(
        TypingBurstIntensity: 0.62d,
        ScrollVelocityIndex: 0.42d,
        FocusSwitchRate: 0.38d,
        AppHash: "app_mixed",
        KeyboardKeydowns: 6,
        MouseMoves: 5,
        MouseWheels: 3,
        IdleSeconds: 0d);

    // Light overload burst that should not dominate the whole hour.
    private static readonly ScriptedBucketInput LightOverloadBurst = new(
        TypingBurstIntensity: 0.50d,
        ScrollVelocityIndex: 0.80d,
        FocusSwitchRate: 0.82d,
        AppHash: "app_burst",
        KeyboardKeydowns: 8,
        MouseMoves: 8,
        MouseWheels: 5,
        IdleSeconds: 0d);

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Gate_DeepWorkToggleCount_NormalScenarioA_IsAtMost3PerHour()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T16:00:00.000+00:00"),
            seed: 81051);
        WarmupBaseline(runner);

        ScenarioGateTrace trace = RunOneHourScenario(
            runner,
            bucketIndex => bucketIndex switch
            {
                < 220 => FocusedWork,   // Focused work period.
                < 380 => Interrupted,   // Ordinary interruption block.
                _ => FocusedWork        // Resume focused work.
            });

        Assert.True(
            trace.DeepWorkTransitions <= DeepWorkTransitionGatePerHour,
            BuildGateDebug("A", trace, runner.Seed));
        Assert.True(
            trace.SawDeepWork,
            $"Scenario A did not enter deep work at all (seed={runner.Seed}).");
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Gate_DeepWorkToggleCount_NormalScenarioB_IsAtMost3PerHour()
    {
        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-06T18:00:00.000+00:00"),
            seed: 81052);
        WarmupBaseline(runner);

        ScenarioGateTrace trace = RunOneHourScenario(
            runner,
            bucketIndex => bucketIndex switch
            {
                < 120 => MixedWork, // Work interval (normal mixed use).
                < 420 => FocusedWork, // Deep-work block.
                < 540 => Interrupted, // Short interruption period.
                < 660 => (bucketIndex % 20) < 3 ? LightOverloadBurst : MixedWork, // Light bursts.
                _ => MixedWork // Recovery period.
            });

        Assert.True(
            trace.DeepWorkTransitions <= DeepWorkTransitionGatePerHour,
            BuildGateDebug("B", trace, runner.Seed));
        Assert.True(
            trace.SawDeepWork,
            $"Scenario B did not enter deep work at all (seed={runner.Seed}).");
    }

    private static ScenarioGateTrace RunOneHourScenario(
        DeterministicFinalizerScenarioRunner runner,
        Func<int, ScriptedBucketInput> inputSelector)
    {
        var activeTrace = new List<bool>(ScenarioBucketsPerHour);
        FinalizerTickObservation last = default;

        bool previous = false;
        int transitions = 0;
        int activeBuckets = 0;
        int firstOnBucketIndex = -1;
        int lastOnBucketIndex = -1;
        bool sawDeepWork = false;

        for (int i = 0; i < ScenarioBucketsPerHour; i++)
        {
            last = runner.RunBucket(inputSelector(i));
            bool deepWorkActive = ReadDeepWorkActive(last.LatestState?.StateFlagsJson);
            activeTrace.Add(deepWorkActive);

            if (i > 0 && deepWorkActive != previous)
            {
                transitions++;
            }

            if (deepWorkActive)
            {
                sawDeepWork = true;
                activeBuckets++;
                if (firstOnBucketIndex < 0)
                {
                    firstOnBucketIndex = i;
                }

                lastOnBucketIndex = i;
            }

            previous = deepWorkActive;
        }

        return new ScenarioGateTrace(
            DeepWorkTransitions: transitions,
            ActiveBuckets: activeBuckets,
            SawDeepWork: sawDeepWork,
            FirstOnBucketIndex: firstOnBucketIndex,
            LastOnBucketIndex: lastOnBucketIndex,
            FinalObservation: last,
            ActiveTrace: activeTrace);
    }

    private static void WarmupBaseline(DeterministicFinalizerScenarioRunner runner)
    {
        for (int i = 0; i < WarmupBuckets; i++)
        {
            runner.RunBucket(BuildWarmupBucket(i));
        }
    }

    private static ScriptedBucketInput BuildWarmupBucket(int bucketIndex)
    {
        double phaseA = (bucketIndex % 17) / 16d;
        double phaseB = ((bucketIndex * 3) % 17) / 16d;
        return new ScriptedBucketInput(
            TypingBurstIntensity: 0.30d + (0.40d * phaseA),
            ScrollVelocityIndex: 0.15d + (0.35d * phaseA),
            FocusSwitchRate: 0.15d + (0.35d * phaseB),
            AppHash: "app_focus",
            KeyboardKeydowns: 4,
            MouseMoves: 3,
            MouseWheels: 1,
            IdleSeconds: 0d);
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

    private static string BuildGateDebug(string scenarioName, ScenarioGateTrace trace, int seed)
    {
        string suffix = trace.ActiveTrace.Count > 0
            ? string.Join(
                string.Empty,
                trace.ActiveTrace.TakeLast(60).Select(v => v ? "1" : "0"))
            : string.Empty;
        return
            $"Step20 Gate 1 failed for scenario={scenarioName}; " +
            $"deep_work_transitions={trace.DeepWorkTransitions} (max={DeepWorkTransitionGatePerHour}), " +
            $"active_buckets={trace.ActiveBuckets}, " +
            $"first_on_bucket={trace.FirstOnBucketIndex}, " +
            $"last_on_bucket={trace.LastOnBucketIndex}, " +
            $"final_ts={trace.FinalObservation.LatestState?.TimestampUtc:O}, " +
            $"final_flags={trace.FinalObservation.LatestState?.StateFlagsJson}, " +
            $"active_trace_last60={suffix}, seed={seed}";
    }

    private readonly record struct ScenarioGateTrace(
        int DeepWorkTransitions,
        int ActiveBuckets,
        bool SawDeepWork,
        int FirstOnBucketIndex,
        int LastOnBucketIndex,
        FinalizerTickObservation FinalObservation,
        IReadOnlyList<bool> ActiveTrace);
}
