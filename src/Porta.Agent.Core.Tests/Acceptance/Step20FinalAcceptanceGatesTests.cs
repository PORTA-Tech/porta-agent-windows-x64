using System.Globalization;
using System.Text.Json;
using Porta.Agent.Common;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests.Acceptance;

public sealed class Step20FinalAcceptanceGatesTests
{
    private const int WarmupBuckets = 220;
    private const int BreakWarmupBuckets = 120;
    private const int BucketsPerHour = 3600 / Time.bucketSeconds;
    private const int DeepWorkToggleThresholdPerHour = 3;
    private const int BrightnessToggleThresholdPerHour = 2;
    private const long BreakWindowMs = PortaExecutionConstants.BreakPolicy.CooldownWindowMs;

    private static readonly ScriptedBucketInput FocusedWork = new(
        TypingBurstIntensity: 0.86d,
        ScrollVelocityIndex: 0.14d,
        FocusSwitchRate: 0.10d,
        AppHash: "app_focus",
        KeyboardKeydowns: 4,
        MouseMoves: 3,
        MouseWheels: 1,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput InterruptedWork = new(
        TypingBurstIntensity: 0.92d,
        ScrollVelocityIndex: 0.95d,
        FocusSwitchRate: 0.95d,
        AppHash: "app_interrupt",
        KeyboardKeydowns: 10,
        MouseMoves: 9,
        MouseWheels: 7,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput MixedWork = new(
        TypingBurstIntensity: 0.62d,
        ScrollVelocityIndex: 0.42d,
        FocusSwitchRate: 0.38d,
        AppHash: "app_mixed",
        KeyboardKeydowns: 6,
        MouseMoves: 5,
        MouseWheels: 3,
        IdleSeconds: 0d);

    private static readonly ScriptedBucketInput LightOverloadBurst = new(
        TypingBurstIntensity: 0.50d,
        ScrollVelocityIndex: 0.80d,
        FocusSwitchRate: 0.82d,
        AppHash: "app_burst",
        KeyboardKeydowns: 8,
        MouseMoves: 8,
        MouseWheels: 5,
        IdleSeconds: 0d);

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

    private static readonly ScriptedBucketInput BreakEligibleCandidate = new(
        TypingBurstIntensity: 0.98d,
        ScrollVelocityIndex: 0.98d,
        FocusSwitchRate: 0.10d,
        AppHash: "app_break",
        KeyboardKeydowns: 12,
        MouseMoves: 12,
        MouseWheels: 10,
        IdleSeconds: 0d);
    private static readonly ScriptedBucketInput BreakBoundaryCandidate = new(
        TypingBurstIntensity: 0.10d,
        ScrollVelocityIndex: 0.10d,
        FocusSwitchRate: 0.05d,
        AppHash: "app_break",
        KeyboardKeydowns: 1,
        MouseMoves: 1,
        MouseWheels: 0,
        IdleSeconds: 4d);

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Gates1And2_NormalScenarios_EnforceHourlyToggleThresholds()
    {
        var report = new AcceptanceGateReport();

        using (var runnerA1 = new DeterministicFinalizerScenarioRunner(
                   startUtc: DateTimeOffset.Parse("2026-03-06T20:00:00.000+00:00"),
                   seed: 91061))
        {
            WarmupBaseline(runnerA1);
            ScenarioTrace trace = RunScenario(runnerA1, BucketsPerHour, SelectNormalScenarioA);
            RecordHourlyToggleGates(report, "normal_A_1h", trace, startBucket: 0, hourCount: 1);
        }

        using (var runnerA2 = new DeterministicFinalizerScenarioRunner(
                   startUtc: DateTimeOffset.Parse("2026-03-06T22:00:00.000+00:00"),
                   seed: 91062))
        {
            WarmupBaseline(runnerA2);
            ScenarioTrace trace = RunScenario(runnerA2, BucketsPerHour * 2, SelectNormalScenarioA);
            RecordHourlyToggleGates(report, "normal_A_2h", trace, startBucket: 0, hourCount: 2);
        }

        using (var runnerB = new DeterministicFinalizerScenarioRunner(
                   startUtc: DateTimeOffset.Parse("2026-03-07T00:00:00.000+00:00"),
                   seed: 91063))
        {
            WarmupBaseline(runnerB);
            ScenarioTrace trace = RunScenario(runnerB, BucketsPerHour, SelectNormalScenarioB);
            RecordHourlyToggleGates(report, "normal_B_1h", trace, startBucket: 0, hourCount: 1);
        }

        report.AssertAllPassed();
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Gate3_RestartNeverLeavesNotificationsSuppressedPermanently()
    {
        var report = new AcceptanceGateReport();
        string[] cases = ["immediately_after_apply", "while_active", "during_pending_restore"];

        for (int i = 0; i < cases.Length; i++)
        {
            string caseName = cases[i];
            using var runner = new DeterministicFinalizerScenarioRunner(
                startUtc: DateTimeOffset.Parse("2026-03-07T02:00:00.000+00:00").AddHours(i),
                seed: 92070 + i);
            WarmupBaseline(runner);

            string initialFocusAssist = runner.NotificationController.CurrentFocusAssistMode;
            bool initialQuietHours = runner.NotificationController.CurrentQuietHoursEnabled;
            bool initialToast = runner.NotificationController.CurrentToastBannersEnabled;

            ActivateDeepWork(runner);

            FinalizerTickObservation last = default;
            if (string.Equals(caseName, "while_active", StringComparison.Ordinal))
            {
                for (int b = 0; b < 12; b++)
                {
                    last = runner.RunBucket(FocusedWork);
                }

                runner.Restart();
            }
            else if (string.Equals(caseName, "during_pending_restore", StringComparison.Ordinal))
            {
                for (int b = 0; b < 40; b++)
                {
                    last = runner.RunBucket(InterruptedWork);
                    if (last.NotificationStatus.RestorePending)
                    {
                        break;
                    }
                }

                Assert.True(last.NotificationStatus.RestorePending, $"Expected restore-pending before restart in case={caseName}.");
                runner.Restart();
            }
            else
            {
                runner.Restart();
            }

            for (int b = 0; b < 120; b++)
            {
                last = runner.RunBucket(InterruptedWork);
                if (!last.NotificationStatus.Active && !last.NotificationStatus.RestorePending)
                {
                    break;
                }
            }

            IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> events = runner.ReadRecentEventRows(4000);
            int suppressedCount = CountEvents(events, "deep_work_notifications_suppressed");
            int restoredCount = CountEvents(events, "deep_work_notifications_restored");
            int restoreFailedCount = CountEvents(events, "notification_restore_failed");

            bool restoredStateMatches =
                !runner.NotificationController.IsSuppressed
                && string.Equals(runner.NotificationController.CurrentFocusAssistMode, initialFocusAssist, StringComparison.Ordinal)
                && runner.NotificationController.CurrentQuietHoursEnabled == initialQuietHours
                && runner.NotificationController.CurrentToastBannersEnabled == initialToast
                && !last.NotificationStatus.Active
                && !last.NotificationStatus.RestorePending;

            report.AddCheck(
                gateName: "restart_notification_restore",
                scenarioName: caseName,
                passed: restoredStateMatches && suppressedCount == 1 && restoredCount == 1 && restoreFailedCount <= 1,
                details: $"suppressed={suppressedCount}, restored={restoredCount}, restore_failed={restoreFailedCount}, final_active={last.NotificationStatus.Active}, final_pending={last.NotificationStatus.RestorePending}");

            ScenarioAssertions.AssertEventsDescendingByTimestamp(events);
            ScenarioAssertions.AssertTimestampsNonDecreasing(runner.NotificationController.Calls.Select(c => c.TimestampUtc));
            ScenarioAssertions.AssertRuntimeStateRowCount(runner.DbPath, PortaExecutionConstants.NotificationSuppression.RuntimeStateKey);
            ScenarioAssertions.AssertLatestStateBucketJsonContracts(runner.DbPath);
        }

        report.AssertAllPassed();
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Gate4_RestartNeverLeavesBrightnessDimPermanently()
    {
        var report = new AcceptanceGateReport();
        string[] cases =
        [
            "restart_before_target",
            "restart_fully_reduced",
            "restart_during_restore_ramp",
            "restart_after_restore_completion",
            "restart_during_transient_failure_backoff"
        ];

        for (int i = 0; i < cases.Length; i++)
        {
            string caseName = cases[i];
            using var runner = new DeterministicFinalizerScenarioRunner(
                startUtc: DateTimeOffset.Parse("2026-03-07T06:00:00.000+00:00").AddHours(i),
                seed: 93080 + i);

            int originalLevel = runner.BrightnessController.CurrentLevel;
            OverloadActivationOutcome activation = ActivateOverload(runner);
            Assert.True(activation.IsActive, activation.Debug);

            FinalizerTickObservation last = activation.LastObservation;
            bool sawBackoff = false;

            switch (caseName)
            {
                case "restart_before_target":
                    last = runner.RunBucket(OverloadCandidate);
                    Assert.True(last.BrightnessStatus.CurrentLevel > last.BrightnessStatus.TargetLevel);
                    runner.Restart();
                    break;

                case "restart_fully_reduced":
                    for (int b = 0; b < 40; b++)
                    {
                        last = runner.RunBucket(OverloadCandidate);
                        if (last.BrightnessStatus.CurrentLevel == last.BrightnessStatus.TargetLevel)
                        {
                            break;
                        }
                    }

                    Assert.Equal(last.BrightnessStatus.TargetLevel, last.BrightnessStatus.CurrentLevel);
                    runner.Restart();
                    break;

                case "restart_during_restore_ramp":
                    for (int b = 0; b < 40; b++)
                    {
                        last = runner.RunBucket(OverloadCandidate);
                        if (last.BrightnessStatus.CurrentLevel == last.BrightnessStatus.TargetLevel)
                        {
                            break;
                        }
                    }

                    for (int b = 0; b < 80; b++)
                    {
                        last = runner.RunBucket(RecoveryCandidate);
                        if (last.BrightnessStatus.Active
                            && string.Equals(last.BrightnessStatus.RampDirection, "up", StringComparison.Ordinal)
                            && last.BrightnessStatus.CurrentLevel < originalLevel)
                        {
                            break;
                        }
                    }

                    Assert.True(last.BrightnessStatus.Active);
                    Assert.Equal("up", last.BrightnessStatus.RampDirection);
                    runner.Restart();
                    break;

                case "restart_after_restore_completion":
                    RunToBrightnessRestored(runner, originalLevel, out last);
                    runner.Restart();
                    break;

                case "restart_during_transient_failure_backoff":
                    last = runner.RunBucket(OverloadCandidate);
                    runner.BrightnessController.FailNextRampDown = true;
                    last = runner.RunBucket(OverloadCandidate);
                    sawBackoff = last.BrightnessStatus.BackoffUntilUtc.HasValue
                        && last.BrightnessStatus.BackoffUntilUtc.Value > last.BucketEndUtc;
                    Assert.True(sawBackoff, "Expected backoff after injected SetLevel failure.");
                    runner.Restart();
                    for (int b = 0; b < 140; b++)
                    {
                        last = runner.RunBucket(OverloadCandidate);
                    }

                    break;
            }

            RunToBrightnessRestored(runner, originalLevel, out last);

            IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> events = runner.ReadRecentEventRows(4000);
            int reducedCount = CountEvents(events, "overload_brightness_reduced");
            int restoredCount = CountEvents(events, "overload_brightness_restored");
            int failedCount = CountEvents(events, "brightness_control_failed");

            bool finalRestored =
                runner.BrightnessController.CurrentLevel == originalLevel
                && !last.BrightnessStatus.Active
                && string.Equals(last.BrightnessStatus.RampDirection, "idle", StringComparison.Ordinal)
                && last.BrightnessStatus.CurrentLevel == originalLevel;

            report.AddCheck(
                gateName: "restart_brightness_restore",
                scenarioName: caseName,
                passed: finalRestored && reducedCount >= 1 && restoredCount >= 1,
                details: $"reduced={reducedCount}, restored={restoredCount}, failed={failedCount}, final_level={runner.BrightnessController.CurrentLevel}, final_active={last.BrightnessStatus.Active}, final_ramp={last.BrightnessStatus.RampDirection}, saw_backoff={sawBackoff}");

            ScenarioAssertions.AssertEventsDescendingByTimestamp(events);
            ScenarioAssertions.AssertTimestampsNonDecreasing(runner.BrightnessController.Calls.Select(c => c.TimestampUtc));
            ScenarioAssertions.AssertRuntimeStateRowCount(runner.DbPath, PortaExecutionConstants.BrightnessAdjustment.RuntimeStateKey);
            ScenarioAssertions.AssertLatestStateBucketJsonContracts(runner.DbPath);
        }

        report.AssertAllPassed();
    }

    [Fact]
    [Trait("Category", "AcceptanceGate")]
    public void Gate5_BreakSuggestionRateAndIdempotency_NoDuplicatesPerWindow()
    {
        var report = new AcceptanceGateReport();

        using var runner = new DeterministicFinalizerScenarioRunner(
            startUtc: DateTimeOffset.Parse("2026-03-07T12:00:00.000+00:00"),
            seed: 94090);
        WarmupBaseline(runner, BreakWarmupBuckets);

        FinalizerTickObservation activation = runner.RunBucket(BreakEligibleCandidate);
        PinOverloadCandidateNormalization(runner.DbPath);
        SeedBreakContinuityHistory(
            runner.DbPath,
            endExclusiveBucketSeq: activation.BucketSeq - BreakWarmupBuckets,
            bucketCount: 180,
            dominantAppHash: BreakEligibleCandidate.AppHash);
        PinOverloadActiveRuntime(runner, activation.BucketSeq);

        bool replayDone = false;
        int previousBreakCount = 0;
        for (int i = 0; i < BucketsPerHour; i++)
        {
            PinOverloadActiveRuntime(runner, activation.BucketSeq + i);
            FinalizerTickObservation observation = runner.RunBucket(SelectBreakScenarioBucket(i));

            int currentBreakCount = CountBreakFamilyEvents(runner.ReadRecentEventRows(4000));
            if (!replayDone && currentBreakCount > previousBreakCount)
            {
                DateTimeOffset replayAt = observation.BucketEndUtc.AddMilliseconds(Time.finalizeDelayMs);
                runner.RunFinalizerTick(replayAt);
                runner.Restart();
                runner.RunFinalizerTick(replayAt.AddSeconds(1));
                runner.RunFinalizerTick(replayAt.AddSeconds(2));
                replayDone = true;
            }

            previousBreakCount = currentBreakCount;
        }

        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> allEvents = runner.ReadRecentEventRows(8000);
        var breakEvents = allEvents
            .Where(e => IsBreakFamilyAction(e.Action))
            .Select(e => new BreakEventTrace(
                TimestampUtc: DateTimeOffset.Parse(e.TimestampUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                IdempotencyKey: e.IdempotencyKey))
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        Assert.NotEmpty(breakEvents);

        Dictionary<long, int> windowCounts = breakEvents
            .GroupBy(e => ResolveWindowStartMs(e.TimestampUtc.ToUnixTimeMilliseconds(), BreakWindowMs))
            .ToDictionary(g => g.Key, g => g.Count());
        bool eachWindowAtMostOne = windowCounts.Values.All(count => count <= 1);

        bool noDuplicateIdempotencyKeys =
            breakEvents.Count == breakEvents.Select(e => e.IdempotencyKey).Distinct(StringComparer.Ordinal).Count();

        bool hourlyBoundRespected = breakEvents.Count <= 2;

        report.AddCheck(
            gateName: "break_idempotency_window",
            scenarioName: "continuous_break_eligible_with_retry_restart",
            passed: eachWindowAtMostOne && noDuplicateIdempotencyKeys && hourlyBoundRespected,
            details: $"break_count={breakEvents.Count}, windows={windowCounts.Count}, max_per_window={windowCounts.Values.DefaultIfEmpty(0).Max()}, replay_done={replayDone}");

        ScenarioAssertions.AssertEventsDescendingByTimestamp(allEvents);
        ScenarioAssertions.AssertLatestStateBucketJsonContracts(runner.DbPath);
        report.AssertAllPassed();
    }

    private static ScriptedBucketInput SelectNormalScenarioA(int bucketIndex)
    {
        int hourIndex = bucketIndex % BucketsPerHour;
        return hourIndex switch
        {
            < 220 => FocusedWork,
            < 380 => InterruptedWork,
            _ => FocusedWork
        };
    }

    private static ScriptedBucketInput SelectNormalScenarioB(int bucketIndex)
    {
        return bucketIndex switch
        {
            < 120 => MixedWork,
            < 420 => FocusedWork,
            < 540 => InterruptedWork,
            < 660 => (bucketIndex % 20) < 3 ? LightOverloadBurst : MixedWork,
            _ => MixedWork
        };
    }

    private static ScriptedBucketInput SelectBreakScenarioBucket(int bucketIndex)
    {
        return (bucketIndex % 12) == 11
            ? BreakBoundaryCandidate
            : BreakEligibleCandidate;
    }

    private static ScenarioTrace RunScenario(
        DeterministicFinalizerScenarioRunner runner,
        int totalBuckets,
        Func<int, ScriptedBucketInput> inputSelector)
    {
        var deepWork = new List<bool>(totalBuckets);
        var brightnessActive = new List<bool>(totalBuckets);
        FinalizerTickObservation last = default;

        for (int i = 0; i < totalBuckets; i++)
        {
            last = runner.RunBucket(inputSelector(i));
            deepWork.Add(ReadDeepWorkActive(last.LatestState?.StateFlagsJson));
            brightnessActive.Add(last.BrightnessStatus.Active);
        }

        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> events = runner.ReadRecentEventRows(4000);
        ScenarioAssertions.AssertEventsDescendingByTimestamp(events);
        ScenarioAssertions.AssertLatestStateBucketJsonContracts(runner.DbPath);

        return new ScenarioTrace(
            DeepWorkActive: deepWork,
            BrightnessActionActive: brightnessActive,
            LastObservation: last);
    }

    private static void RecordHourlyToggleGates(
        AcceptanceGateReport report,
        string scenarioName,
        ScenarioTrace trace,
        int startBucket,
        int hourCount)
    {
        for (int hour = 0; hour < hourCount; hour++)
        {
            int hourStart = startBucket + (hour * BucketsPerHour);
            int hourEndExclusive = hourStart + BucketsPerHour;

            int deepWorkTransitions = CountStateTransitions(trace.DeepWorkActive, hourStart, hourEndExclusive);
            int brightnessTransitions = CountStateTransitions(trace.BrightnessActionActive, hourStart, hourEndExclusive);

            report.AddThreshold(
                gateName: "deep_work_toggle_count",
                scenarioName: $"{scenarioName}:hour{hour + 1}",
                measuredValue: deepWorkTransitions,
                thresholdValue: DeepWorkToggleThresholdPerHour,
                passed: deepWorkTransitions <= DeepWorkToggleThresholdPerHour);

            // Gate definition (explicit):
            // brightness_change_count counts action-state transitions (inactive<->active),
            // not per-bucket SetLevel ramp steps.
            report.AddThreshold(
                gateName: "brightness_change_count",
                scenarioName: $"{scenarioName}:hour{hour + 1}",
                measuredValue: brightnessTransitions,
                thresholdValue: BrightnessToggleThresholdPerHour,
                passed: brightnessTransitions <= BrightnessToggleThresholdPerHour,
                details: "measured as action-state toggles, not SetLevel ramp calls");
        }
    }

    private static int CountStateTransitions(
        IReadOnlyList<bool> states,
        int startInclusive,
        int endExclusive)
    {
        if (states.Count == 0 || startInclusive >= endExclusive)
        {
            return 0;
        }

        int start = Math.Max(0, startInclusive);
        int end = Math.Min(states.Count, endExclusive);
        if (start >= end)
        {
            return 0;
        }

        bool previous = start > 0 ? states[start - 1] : states[start];
        int transitions = 0;
        for (int i = start; i < end; i++)
        {
            bool current = states[i];
            if (current != previous)
            {
                transitions++;
            }

            previous = current;
        }

        return transitions;
    }

    private static void WarmupBaseline(DeterministicFinalizerScenarioRunner runner, int bucketCount = WarmupBuckets)
    {
        for (int i = 0; i < bucketCount; i++)
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

    private static void ActivateDeepWork(DeterministicFinalizerScenarioRunner runner)
    {
        EnsureDeepWorkInactive(runner);

        bool previousActive = false;
        bool sawActivation = false;
        for (int i = 0; i < 320; i++)
        {
            FinalizerTickObservation observation = runner.RunBucket(FocusedWork);
            bool active = ReadDeepWorkActive(observation.LatestState?.StateFlagsJson);
            if (!previousActive && active)
            {
                sawActivation = true;
            }

            if (sawActivation && active && observation.NotificationStatus.Active)
            {
                return;
            }

            previousActive = active;
        }

        throw new Xunit.Sdk.XunitException($"Deep work did not activate in deterministic search window (seed={runner.Seed}).");
    }

    private static void EnsureDeepWorkInactive(DeterministicFinalizerScenarioRunner runner)
    {
        for (int i = 0; i < 240; i++)
        {
            FinalizerTickObservation observation = runner.RunBucket(InterruptedWork);
            if (!ReadDeepWorkActive(observation.LatestState?.StateFlagsJson)
                && !observation.NotificationStatus.Active
                && !observation.NotificationStatus.RestorePending)
            {
                return;
            }
        }
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

        FinalizerTickObservation last = default;
        int overloadActivations = 0;
        for (int i = 0; i < 360; i++)
        {
            last = runner.RunBucket(OverloadCandidate);
            bool overloadActive = last.LatestState?.OverloadActive ?? false;
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

        return new OverloadActivationOutcome(
            IsActive: last.LatestState?.OverloadActive ?? false,
            LastObservation: last,
            Debug: $"overload_activations={overloadActivations}, seed={runner.Seed}, last_overload={last.LatestState?.OverloadLikelihood:F3}, last_conf={last.LatestState?.Confidence:F3}");
    }

    private static void RunToBrightnessRestored(
        DeterministicFinalizerScenarioRunner runner,
        int originalLevel,
        out FinalizerTickObservation last)
    {
        last = default;
        for (int i = 0; i < 200; i++)
        {
            last = runner.RunBucket(RecoveryCandidate);
            if (!last.BrightnessStatus.Active
                && last.BrightnessStatus.CurrentLevel == originalLevel
                && string.Equals(last.BrightnessStatus.RampDirection, "idle", StringComparison.Ordinal))
            {
                return;
            }
        }
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

    private static int CountBreakFamilyEvents(
        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> rows)
    {
        return rows.Count(row => IsBreakFamilyAction(row.Action));
    }

    private static bool IsBreakFamilyAction(string action)
    {
        return PortaBreakSemantics.IsBreakSuggestionAction(action);
    }

    private static void SeedBreakContinuityHistory(
        string dbPath,
        long endExclusiveBucketSeq,
        int bucketCount,
        string dominantAppHash)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using SqliteTransaction tx = connection.BeginTransaction();

        for (int i = Math.Max(0, bucketCount); i > 0; i--)
        {
            long bucketSeq = endExclusiveBucketSeq - i;
            if (bucketSeq < 0)
            {
                continue;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO state_buckets (
                    timestamp_utc,
                    typing_burst_intensity_raw,
                    scroll_velocity_index_raw,
                    focus_switch_rate_raw,
                    typing_burst_intensity_smoothed,
                    scroll_velocity_index_smoothed,
                    focus_switch_rate_smoothed,
                    focus_strength,
                    overload_likelihood,
                    fatigue_probability,
                    cognitive_stability,
                    dominant_state_name,
                    dominant_state_value,
                    confidence,
                    sustained_attention_duration_sec,
                    idle_fragmentation_index,
                    activation_window_triggered,
                    cooldown_triggered,
                    activation_window_open,
                    contributors_json,
                    state_flags_json
                ) VALUES (
                    $ts,
                    0.9,
                    0.9,
                    0.1,
                    0.9,
                    0.9,
                    0.1,
                    0.6,
                    0.7,
                    0.2,
                    0.8,
                    'focus',
                    0.8,
                    0.8,
                    $sa_seconds,
                    0.05,
                    0,
                    0,
                    1,
                    $contributors_json,
                    $state_flags_json
                );
                """;
            cmd.Parameters.AddWithValue("$ts", Time.toIsoUtcMsString(Time.bucketStartEnd(bucketSeq).endUtcMs));
            cmd.Parameters.AddWithValue("$sa_seconds", Math.Max(0, (bucketCount - i + 1) * Time.bucketSeconds));
            cmd.Parameters.AddWithValue("$contributors_json", "{\"schema_ver\":1}");
            cmd.Parameters.AddWithValue(
                "$state_flags_json",
                JsonSerializer.Serialize(new
                {
                    schema_ver = 1,
                    task_continuity_score = 1.0,
                    behavior_continuity_score = 1.0,
                    task_continuity_seconds = Math.Max(0, (bucketCount - i + 1) * Time.bucketSeconds),
                    continuity_state = "keep",
                    continuity_confidence = 0.85,
                    continuity_anchor_app_hash = dominantAppHash
                }));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static long ResolveWindowStartMs(long nowUtcMs, long windowMs)
    {
        if (windowMs <= 0)
        {
            return nowUtcMs;
        }

        long quotient = Math.DivRem(nowUtcMs, windowMs, out long remainder);
        if (remainder < 0)
        {
            quotient--;
        }

        return checked(quotient * windowMs);
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

    private readonly record struct ScenarioTrace(
        IReadOnlyList<bool> DeepWorkActive,
        IReadOnlyList<bool> BrightnessActionActive,
        FinalizerTickObservation LastObservation);

    private readonly record struct OverloadActivationOutcome(
        bool IsActive,
        FinalizerTickObservation LastObservation,
        string Debug);

    private readonly record struct BreakEventTrace(
        DateTimeOffset TimestampUtc,
        string IdempotencyKey);
}
