using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

public sealed class OverloadBrightnessActionCoordinatorTests
{
    [Fact]
    public void HandleOverloadTransition_NoTransition_NoControllerCalls()
    {
        var fake = new FakeBrightnessController();
        var coordinator = new OverloadBrightnessActionCoordinator(fake);

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: false),
            BrightnessActionRuntimeState.CreateDefault());

        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("no_transition", execution.Outcome.OutcomeReason);
        Assert.Equal(0, fake.IsSupportedCalls);
        Assert.Equal(0, fake.GetLevelCalls);
        Assert.Equal(0, fake.SetLevelCalls);
    }

    [Fact]
    public void HandleOverloadTransition_EnteringSupported_CapturesPrevAndStartsRampDown()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 72
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: true),
            BrightnessActionRuntimeState.CreateDefault());

        Assert.True(execution.Outcome.Succeeded);
        Assert.True(execution.Outcome.EnteringOverload);
        Assert.True(execution.Outcome.ReduceAttempted);
        Assert.Equal(72, execution.RuntimeState.PreviousLevel);
        Assert.Equal(67, execution.RuntimeState.TargetLevel);
        Assert.Equal("down", execution.RuntimeState.RampDirection);
        Assert.Equal(1, fake.GetLevelCalls);
        Assert.Equal(1, fake.SetLevelCalls);
        Assert.Equal(71, fake.LastSetLevel);
    }

    [Fact]
    public void HandleOverloadTransition_EnteringUnsupported_SetsLoggedUnsupported()
    {
        var fake = new FakeBrightnessController
        {
            Supported = false
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: true),
            BrightnessActionRuntimeState.CreateDefault());

        Assert.False(execution.Outcome.Succeeded);
        Assert.Equal("brightness_unsupported", execution.Outcome.ErrorCode);
        Assert.False(execution.RuntimeState.BrightnessSupported);
        Assert.True(execution.RuntimeState.LoggedUnsupported);
        Assert.Equal("unsupported", execution.RuntimeState.ActionState);
    }

    [Fact]
    public void HandleOverloadTransition_UnsupportedLatched_DoesNotReEmitFailure()
    {
        var fake = new FakeBrightnessController
        {
            Supported = false
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        OverloadBrightnessTransitionExecution first = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: true, nowUtc: now),
            BrightnessActionRuntimeState.CreateDefault());

        OverloadBrightnessTransitionExecution second = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: true, nowUtc: now.AddMinutes(20)),
            first.RuntimeState);

        Assert.False(first.Outcome.Succeeded);
        Assert.Equal("brightness_unsupported", first.Outcome.ErrorCode);
        Assert.True(second.Outcome.Succeeded);
        Assert.Equal("unsupported_latched", second.Outcome.OutcomeReason);
        Assert.False(second.RuntimeState.BrightnessSupported);
        Assert.True(second.RuntimeState.LoggedUnsupported);
        Assert.Equal(0, fake.GetLevelCalls);
        Assert.Equal(0, fake.SetLevelCalls);
    }

    [Fact]
    public void HandleOverloadTransition_UsesPinnedMinSafeBrightness()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 18
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: true),
            BrightnessActionRuntimeState.CreateDefault());

        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("already_at_min_safe", execution.Outcome.OutcomeReason);
        Assert.Equal(18, execution.RuntimeState.PreviousLevel);
        Assert.Equal(20, execution.RuntimeState.TargetLevel);
        Assert.Equal("idle", execution.RuntimeState.RampDirection);
        Assert.Equal(0, fake.SetLevelCalls);
    }

    [Fact]
    public void HandleOverloadTransition_ExitingActive_RampsUpTowardPrevious()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 67
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 67,
            RampDirection = "up"
        };

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: true, currentActive: false),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.True(execution.Outcome.RestoreAttempted);
        Assert.Equal(68, fake.LastSetLevel);
        Assert.Equal("up", execution.RuntimeState.RampDirection);
    }

    [Fact]
    public void HandleOverloadTransition_RestoreCompletion_SetsSessionCooldown()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 71
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 71,
            RampDirection = "up"
        };

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: true, currentActive: false, nowUtc: now),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("restored", execution.Outcome.OutcomeReason);
        Assert.False(execution.RuntimeState.Active);
        Assert.Equal("idle", execution.RuntimeState.RampDirection);
        Assert.True(execution.RuntimeState.CooldownUntilUtcMs >= now.ToUnixTimeMilliseconds() + 600_000 - 1);
    }

    [Fact]
    public void HandleOverloadTransition_SetFailure_UsesFixedTenMinuteBackoff()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 70,
            FailSetLevel = true,
            SetLevelErrorCode = "SET_FAIL"
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 70,
            RampDirection = "down"
        };

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: true, currentActive: true, nowUtc: now),
            state);

        Assert.False(execution.Outcome.Succeeded);
        Assert.Equal("SET_FAIL", execution.Outcome.ErrorCode);
        Assert.True(execution.RuntimeState.BrightnessSupported);
        Assert.True(execution.RuntimeState.BackoffUntilUtcMs >= now.ToUnixTimeMilliseconds() + 600_000 - 1);
    }

    [Fact]
    public void HandleOverloadTransition_BackoffActive_DoesNotRetryControllerCalls()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 70
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 70,
            RampDirection = "down",
            BackoffUntilUtcMs = now.ToUnixTimeMilliseconds() + 300_000
        };

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: true, currentActive: true, nowUtc: now),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("backoff_active", execution.Outcome.OutcomeReason);
        Assert.Equal(0, fake.GetLevelCalls);
        Assert.Equal(0, fake.SetLevelCalls);
    }

    [Fact]
    public void HandleOverloadTransition_StayOverloaded_RampsOnePercentPerBucketUntilTarget()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 72
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: true, nowUtc: now),
            BrightnessActionRuntimeState.CreateDefault());
        BrightnessActionRuntimeState state = execution.RuntimeState;

        Assert.Equal(71, state.CurrentLevel);
        Assert.Equal(67, state.TargetLevel);

        int expectedLevel = 70;
        while (state.CurrentLevel > state.TargetLevel)
        {
            now = now.AddSeconds(5);
            execution = coordinator.HandleOverloadTransition(
                BuildContext(previousActive: true, currentActive: true, nowUtc: now),
                state);
            state = execution.RuntimeState;
            Assert.Equal(expectedLevel, state.CurrentLevel);
            expectedLevel--;
        }

        Assert.Equal(67, state.CurrentLevel);
        Assert.Equal("idle", state.RampDirection);
        Assert.True(state.Active);
        Assert.Equal(5, fake.SetLevelCalls);
    }

    [Fact]
    public void HandleOverloadTransition_EnteringWhileAlreadyActive_DoesNotOverwritePreviousLevel()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 69
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 69,
            RampDirection = "down",
            ActionState = "ramping_down"
        };

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: false, currentActive: true),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("already_active", execution.Outcome.OutcomeReason);
        Assert.Equal(72, execution.RuntimeState.PreviousLevel);
        Assert.Equal(67, execution.RuntimeState.TargetLevel);
        Assert.Equal(0, fake.GetLevelCalls);
        Assert.Equal(0, fake.SetLevelCalls);
    }

    [Fact]
    public void HandleOverloadTransition_ExitOverload_RampsUpToPreviousAndCompletes()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 67
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 67,
            RampDirection = "idle",
            ActionState = "dimmed"
        };

        OverloadBrightnessTransitionExecution execution;
        do
        {
            now = now.AddSeconds(5);
            execution = coordinator.HandleOverloadTransition(
                BuildContext(previousActive: true, currentActive: false, nowUtc: now),
                state);
            state = execution.RuntimeState;
        }
        while (state.Active);

        Assert.Equal("restored", execution.Outcome.OutcomeReason);
        Assert.False(state.Active);
        Assert.Equal(72, state.CurrentLevel);
        Assert.Equal("idle", state.RampDirection);
        Assert.True(state.CooldownUntilUtcMs >= now.ToUnixTimeMilliseconds() + 600_000 - 1);
        Assert.Equal(5, fake.SetLevelCalls);
    }

    [Fact]
    public void ReconcileOnStartup_ActiveRampingDown_AlignsCurrentLevelAndKeepsDirection()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 69
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 70,
            RampDirection = "down",
            ActionState = "ramping_down"
        };

        BrightnessStartupReconciliation reconciliation = coordinator.ReconcileOnStartup(state, DateTimeOffset.UtcNow);

        Assert.True(reconciliation.Reconciled);
        Assert.True(reconciliation.RuntimeState.Active);
        Assert.Equal("down", reconciliation.RuntimeState.RampDirection);
        Assert.Equal(69, reconciliation.RuntimeState.CurrentLevel);
    }

    [Fact]
    public void ReconcileOnStartup_ActiveRampingUpAndRestored_CompletesSessionAndCooldown()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 72
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrightnessActionRuntimeState state = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 70,
            RampDirection = "up",
            ActionState = "ramping_up"
        };

        BrightnessStartupReconciliation reconciliation = coordinator.ReconcileOnStartup(state, now);

        Assert.True(reconciliation.Reconciled);
        Assert.False(reconciliation.RuntimeState.Active);
        Assert.Equal("idle", reconciliation.RuntimeState.RampDirection);
        Assert.Equal("restored", reconciliation.RuntimeState.ActionState);
        Assert.True(reconciliation.RuntimeState.CooldownUntilUtcMs >= now.ToUnixTimeMilliseconds() + 600_000 - 1);
    }

    [Fact]
    public void ReconcileOnStartup_ActiveMidRampDown_ContinuesSafelyOnNextBucket()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 69
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BrightnessActionRuntimeState persisted = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 72,
            TargetLevel = 67,
            CurrentLevel = 70,
            RampDirection = "down",
            ActionState = "ramping_down"
        };

        BrightnessStartupReconciliation reconciliation = coordinator.ReconcileOnStartup(persisted, now);
        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: true, currentActive: true, nowUtc: now.AddSeconds(5)),
            reconciliation.RuntimeState);

        Assert.True(reconciliation.Reconciled);
        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("ramping", execution.Outcome.OutcomeReason);
        Assert.Equal(68, execution.RuntimeState.CurrentLevel);
        Assert.Equal("down", execution.RuntimeState.RampDirection);
    }

    [Fact]
    public void HandleOverloadTransition_ClampsOutgoingSetLevelTo_0_100()
    {
        var fake = new FakeBrightnessController
        {
            Supported = true,
            CurrentLevel = 72
        };
        var coordinator = new OverloadBrightnessActionCoordinator(fake);
        BrightnessActionRuntimeState corrupted = BrightnessActionRuntimeState.CreateDefault() with
        {
            Active = true,
            PreviousLevel = 150,
            TargetLevel = -20,
            CurrentLevel = 150,
            RampDirection = "down"
        };

        OverloadBrightnessTransitionExecution execution = coordinator.HandleOverloadTransition(
            BuildContext(previousActive: true, currentActive: true),
            corrupted);

        Assert.True(execution.Outcome.Succeeded);
        Assert.InRange(fake.LastSetLevel, 0, 100);
    }

    private static OverloadTransitionContext BuildContext(bool previousActive, bool currentActive, DateTimeOffset? nowUtc = null)
    {
        return new OverloadTransitionContext(
            BucketSeq: 123,
            BucketEndUtc: nowUtc ?? DateTimeOffset.UtcNow,
            PreviousOverloadActive: previousActive,
            CurrentOverloadActive: currentActive,
            LowQualityPaused: false,
            OverloadLikelihood: 0.82d,
            Confidence: 0.78d);
    }

    private sealed class FakeBrightnessController : IBrightnessController
    {
        public bool Supported { get; init; } = true;
        public int CurrentLevel { get; set; } = 55;
        public int LastSetLevel { get; private set; } = -1;
        public int IsSupportedCalls { get; private set; }
        public int GetLevelCalls { get; private set; }
        public int SetLevelCalls { get; private set; }
        public bool FailGetLevel { get; init; }
        public bool FailSetLevel { get; init; }
        public string SetLevelErrorCode { get; init; } = "brightness_set_failed";

        public bool IsSupported()
        {
            IsSupportedCalls++;
            return Supported;
        }

        public BrightnessControllerLevelResult GetLevel()
        {
            GetLevelCalls++;
            if (FailGetLevel)
            {
                return new BrightnessControllerLevelResult(
                    Succeeded: false,
                    ErrorCode: "brightness_get_failed",
                    ErrorDetail: "simulated get failure",
                    IsSupported: true,
                    Level: CurrentLevel);
            }
            return new BrightnessControllerLevelResult(
                Succeeded: Supported,
                ErrorCode: Supported ? string.Empty : "brightness_unsupported",
                ErrorDetail: Supported ? string.Empty : "not supported",
                IsSupported: Supported,
                Level: CurrentLevel);
        }

        public BrightnessControllerSetResult SetLevel(int level)
        {
            SetLevelCalls++;
            LastSetLevel = level;
            if (!Supported)
            {
                return new BrightnessControllerSetResult(
                    Succeeded: false,
                    ErrorCode: "brightness_unsupported",
                    ErrorDetail: "not supported",
                    IsSupported: false,
                    StateChanged: false,
                    AppliedLevel: CurrentLevel);
            }

            if (FailSetLevel)
            {
                return new BrightnessControllerSetResult(
                    Succeeded: false,
                    ErrorCode: SetLevelErrorCode,
                    ErrorDetail: "simulated set failure",
                    IsSupported: true,
                    StateChanged: false,
                    AppliedLevel: CurrentLevel);
            }

            bool changed = CurrentLevel != level;
            CurrentLevel = level;
            return new BrightnessControllerSetResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                IsSupported: true,
                StateChanged: changed,
                AppliedLevel: CurrentLevel);
        }
    }
}
