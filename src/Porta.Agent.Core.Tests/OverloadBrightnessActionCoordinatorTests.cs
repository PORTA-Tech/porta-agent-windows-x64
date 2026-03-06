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

    private static OverloadTransitionContext BuildContext(bool previousActive, bool currentActive)
    {
        return new OverloadTransitionContext(
            BucketSeq: 123,
            BucketEndUtc: DateTimeOffset.UtcNow,
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

        public bool IsSupported()
        {
            IsSupportedCalls++;
            return Supported;
        }

        public BrightnessControllerLevelResult GetLevel()
        {
            GetLevelCalls++;
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
