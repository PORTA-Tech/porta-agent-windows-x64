namespace Porta.Agent.Core.Outputs;

public readonly record struct OverloadTransitionContext(
    long BucketSeq,
    DateTimeOffset BucketEndUtc,
    bool PreviousOverloadActive,
    bool CurrentOverloadActive,
    bool LowQualityPaused,
    double OverloadLikelihood,
    double Confidence);

public readonly record struct BrightnessActionRuntimeState(
    int SchemaVer,
    bool Active,
    bool BrightnessSupported,
    int PreviousLevel,
    int TargetLevel,
    int CurrentLevel,
    string RampDirection,
    long LastChangeUtcMs,
    long CooldownUntilUtcMs,
    string LastErrorCode,
    long BackoffUntilUtcMs,
    long LastTransitionSourceBucketSeq,
    bool LoggedUnsupported,
    string ActionState)
{
    public static BrightnessActionRuntimeState CreateDefault()
    {
        return new BrightnessActionRuntimeState(
            SchemaVer: 1,
            Active: false,
            BrightnessSupported: true,
            PreviousLevel: -1,
            TargetLevel: -1,
            CurrentLevel: -1,
            RampDirection: "idle",
            LastChangeUtcMs: 0,
            CooldownUntilUtcMs: 0,
            LastErrorCode: string.Empty,
            BackoffUntilUtcMs: 0,
            LastTransitionSourceBucketSeq: 0,
            LoggedUnsupported: false,
            ActionState: "inactive");
    }
}

public readonly record struct OverloadBrightnessActionOutcome(
    bool EnteringOverload,
    bool ExitingOverload,
    bool ReduceAttempted,
    bool RestoreAttempted,
    bool Succeeded,
    string ErrorCode,
    string ErrorDetail,
    bool StateChanged,
    string OutcomeReason);

public readonly record struct OverloadBrightnessTransitionExecution(
    OverloadBrightnessActionOutcome Outcome,
    BrightnessActionRuntimeState RuntimeState);

public interface IOverloadBrightnessActionCoordinator
{
    OverloadBrightnessTransitionExecution HandleOverloadTransition(
        OverloadTransitionContext context,
        BrightnessActionRuntimeState runtimeState);
}

public sealed class OverloadBrightnessActionCoordinator : IOverloadBrightnessActionCoordinator
{
    private readonly IBrightnessController _brightnessController;

    public OverloadBrightnessActionCoordinator(IBrightnessController brightnessController)
    {
        _brightnessController = brightnessController ?? throw new ArgumentNullException(nameof(brightnessController));
    }

    public OverloadBrightnessTransitionExecution HandleOverloadTransition(
        OverloadTransitionContext context,
        BrightnessActionRuntimeState runtimeState)
    {
        BrightnessActionRuntimeState state = NormalizeRuntimeState(runtimeState);
        long nowUtcMs = context.BucketEndUtc.ToUnixTimeMilliseconds();
        bool enteringTransition = !context.PreviousOverloadActive && context.CurrentOverloadActive;
        bool exitingTransition = context.PreviousOverloadActive && !context.CurrentOverloadActive;

        if (enteringTransition)
        {
            return HandleEntering(context, state, nowUtcMs);
        }

        if (state.Active && context.CurrentOverloadActive && string.Equals(state.RampDirection, "down", StringComparison.Ordinal))
        {
            return TryRampStep(context, state, nowUtcMs, enteringTransition: false, exitingTransition: false, direction: "down");
        }

        if (state.Active && !context.CurrentOverloadActive)
        {
            BrightnessActionRuntimeState restoreState = state with
            {
                RampDirection = "up",
                LastTransitionSourceBucketSeq = context.BucketSeq,
                ActionState = "ramping_up"
            };
            return TryRampStep(
                context,
                restoreState,
                nowUtcMs,
                enteringTransition: false,
                exitingTransition: exitingTransition || !context.CurrentOverloadActive,
                direction: "up");
        }

        return NoOp(state, "no_transition");
    }

    private OverloadBrightnessTransitionExecution HandleEntering(
        OverloadTransitionContext context,
        BrightnessActionRuntimeState state,
        long nowUtcMs)
    {
        if (state.Active)
        {
            if (string.Equals(state.RampDirection, "up", StringComparison.Ordinal))
            {
                BrightnessActionRuntimeState resumed = state with
                {
                    RampDirection = "down",
                    LastTransitionSourceBucketSeq = context.BucketSeq,
                    ActionState = "ramping_down"
                };
                return TryRampStep(context, resumed, nowUtcMs, enteringTransition: true, exitingTransition: false, direction: "down");
            }

            return NoOp(state, "already_active");
        }

        if (!_brightnessController.IsSupported())
        {
            if (state.LoggedUnsupported)
            {
                BrightnessActionRuntimeState latchedUnsupportedState = state with
                {
                    BrightnessSupported = false,
                    LastErrorCode = "brightness_unsupported",
                    ActionState = "unsupported_latched",
                    LastTransitionSourceBucketSeq = context.BucketSeq
                };
                return NoOp(latchedUnsupportedState, "unsupported_latched");
            }

            return BuildFailure(
                state with { LoggedUnsupported = true },
                context,
                enteringTransition: true,
                exitingTransition: false,
                reduceAttempted: false,
                restoreAttempted: false,
                errorCode: "brightness_unsupported",
                errorDetail: "Brightness controller reported unsupported device.",
                actionState: "unsupported",
                nowUtcMs: nowUtcMs);
        }

        if (nowUtcMs < state.CooldownUntilUtcMs)
        {
            return NoOp(state, "cooldown_active");
        }

        if (nowUtcMs < state.BackoffUntilUtcMs)
        {
            return NoOp(state with { ActionState = "backoff_wait" }, "backoff_active");
        }

        BrightnessControllerLevelResult levelResult = _brightnessController.GetLevel();
        if (!levelResult.Succeeded || !levelResult.IsSupported)
        {
            string errorCode = string.IsNullOrWhiteSpace(levelResult.ErrorCode)
                ? "brightness_level_read_failed"
                : levelResult.ErrorCode;
            return BuildFailure(
                state,
                context,
                enteringTransition: true,
                exitingTransition: false,
                reduceAttempted: false,
                restoreAttempted: false,
                errorCode: errorCode,
                errorDetail: levelResult.ErrorDetail,
                actionState: "read_failed",
                nowUtcMs: nowUtcMs);
        }

        int prevLevel = ClampLevel(levelResult.Level);
        int targetLevel = Math.Max(
            prevLevel - PortaExecutionConstants.BrightnessAdjustment.ReductionDelta,
            PortaExecutionConstants.BrightnessAdjustment.MinSafeBrightness);
        BrightnessActionRuntimeState activeState = state with
        {
            Active = targetLevel < prevLevel,
            BrightnessSupported = true,
            PreviousLevel = prevLevel,
            TargetLevel = targetLevel,
            CurrentLevel = prevLevel,
            RampDirection = targetLevel < prevLevel ? "down" : "idle",
            LastChangeUtcMs = nowUtcMs,
            CooldownUntilUtcMs = Math.Max(0, state.CooldownUntilUtcMs),
            LastErrorCode = string.Empty,
            BackoffUntilUtcMs = 0,
            LastTransitionSourceBucketSeq = context.BucketSeq,
            ActionState = targetLevel < prevLevel ? "ramping_down" : "no_adjustment_needed"
        };

        if (targetLevel >= prevLevel)
        {
            return new OverloadBrightnessTransitionExecution(
                Outcome: new OverloadBrightnessActionOutcome(
                    EnteringOverload: true,
                    ExitingOverload: false,
                    ReduceAttempted: false,
                    RestoreAttempted: false,
                    Succeeded: true,
                    ErrorCode: string.Empty,
                    ErrorDetail: string.Empty,
                    StateChanged: false,
                    OutcomeReason: "already_at_min_safe"),
                RuntimeState: activeState);
        }

        return TryRampStep(context, activeState, nowUtcMs, enteringTransition: true, exitingTransition: false, direction: "down");
    }

    private OverloadBrightnessTransitionExecution TryRampStep(
        OverloadTransitionContext context,
        BrightnessActionRuntimeState state,
        long nowUtcMs,
        bool enteringTransition,
        bool exitingTransition,
        string direction)
    {
        if (nowUtcMs < state.BackoffUntilUtcMs)
        {
            return NoOp(state with { ActionState = "backoff_wait" }, "backoff_active");
        }

        if (!_brightnessController.IsSupported())
        {
            if (state.LoggedUnsupported)
            {
                BrightnessActionRuntimeState latchedUnsupportedState = state with
                {
                    Active = false,
                    BrightnessSupported = false,
                    RampDirection = "idle",
                    LastErrorCode = "brightness_unsupported",
                    ActionState = "unsupported_latched",
                    LastTransitionSourceBucketSeq = context.BucketSeq
                };
                return NoOp(latchedUnsupportedState, "unsupported_latched");
            }

            return BuildFailure(
                state with
                {
                    LoggedUnsupported = true,
                    Active = false,
                    RampDirection = "idle"
                },
                context,
                enteringTransition: enteringTransition,
                exitingTransition: exitingTransition,
                reduceAttempted: string.Equals(direction, "down", StringComparison.Ordinal),
                restoreAttempted: string.Equals(direction, "up", StringComparison.Ordinal),
                errorCode: "brightness_unsupported",
                errorDetail: "Brightness controller reported unsupported device during ramp operation.",
                actionState: "unsupported",
                nowUtcMs: nowUtcMs);
        }

        int currentLevel = ClampLevel(state.CurrentLevel < 0 ? state.PreviousLevel : state.CurrentLevel);
        int destination = string.Equals(direction, "down", StringComparison.Ordinal)
            ? Math.Max(state.TargetLevel, currentLevel - PortaExecutionConstants.BrightnessAdjustment.RampStepPerBucket)
            : Math.Min(state.PreviousLevel, currentLevel + PortaExecutionConstants.BrightnessAdjustment.RampStepPerBucket);
        destination = ClampLevel(destination);

        if (destination == currentLevel)
        {
            BrightnessActionRuntimeState completedState = state with
            {
                CurrentLevel = destination,
                ActionState = ResolveCompletedActionState(direction),
                RampDirection = ResolveCompletedRampDirection(direction),
                Active = ResolveCompletedActive(direction),
                CooldownUntilUtcMs = string.Equals(direction, "up", StringComparison.Ordinal)
                    ? checked(nowUtcMs + (PortaExecutionConstants.BrightnessAdjustment.CooldownSeconds * 1000L))
                    : state.CooldownUntilUtcMs
            };
            return FinalizeRampState(
                completedState,
                enteringTransition,
                exitingTransition,
                reduceAttempted: false,
                restoreAttempted: false,
                stateChanged: false,
                outcomeReason: ResolveCompletedOutcomeReason(direction));
        }

        int clampedDestination = ClampLevel(destination);
        BrightnessControllerSetResult setResult = _brightnessController.SetLevel(clampedDestination);
        if (!setResult.Succeeded || !setResult.IsSupported)
        {
            string errorCode = string.IsNullOrWhiteSpace(setResult.ErrorCode)
                ? "brightness_set_failed"
                : setResult.ErrorCode;
            return BuildFailure(
                state,
                context,
                enteringTransition: enteringTransition,
                exitingTransition: exitingTransition,
                reduceAttempted: string.Equals(direction, "down", StringComparison.Ordinal),
                restoreAttempted: string.Equals(direction, "up", StringComparison.Ordinal),
                errorCode: errorCode,
                errorDetail: setResult.ErrorDetail,
                actionState: string.Equals(direction, "down", StringComparison.Ordinal) ? "ramp_down_failed" : "ramp_up_failed",
                nowUtcMs: nowUtcMs);
        }

        int appliedLevel = ClampLevel(setResult.AppliedLevel);
        bool reachedTarget = string.Equals(direction, "down", StringComparison.Ordinal)
            ? appliedLevel <= state.TargetLevel
            : appliedLevel >= state.PreviousLevel;
        BrightnessActionRuntimeState nextState = state with
        {
            Active = string.Equals(direction, "down", StringComparison.Ordinal) || !reachedTarget,
            BrightnessSupported = true,
            CurrentLevel = appliedLevel,
            RampDirection = reachedTarget ? ResolveCompletedRampDirection(direction) : direction,
            LastChangeUtcMs = nowUtcMs,
            LastErrorCode = string.Empty,
            BackoffUntilUtcMs = 0,
            LastTransitionSourceBucketSeq = context.BucketSeq,
            ActionState = reachedTarget ? ResolveCompletedActionState(direction) : (string.Equals(direction, "down", StringComparison.Ordinal) ? "ramping_down" : "ramping_up")
        };

        if (reachedTarget && string.Equals(direction, "up", StringComparison.Ordinal))
        {
            nextState = nextState with
            {
                Active = false,
                CooldownUntilUtcMs = checked(nowUtcMs + (PortaExecutionConstants.BrightnessAdjustment.CooldownSeconds * 1000L))
            };
        }

        return new OverloadBrightnessTransitionExecution(
            Outcome: new OverloadBrightnessActionOutcome(
                EnteringOverload: enteringTransition,
                ExitingOverload: exitingTransition,
                ReduceAttempted: string.Equals(direction, "down", StringComparison.Ordinal),
                RestoreAttempted: string.Equals(direction, "up", StringComparison.Ordinal),
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: setResult.StateChanged,
                OutcomeReason: reachedTarget ? ResolveCompletedOutcomeReason(direction) : "ramping"),
            RuntimeState: nextState);
    }

    private static OverloadBrightnessTransitionExecution FinalizeRampState(
        BrightnessActionRuntimeState state,
        bool enteringTransition,
        bool exitingTransition,
        bool reduceAttempted,
        bool restoreAttempted,
        bool stateChanged,
        string outcomeReason)
    {
        return new OverloadBrightnessTransitionExecution(
            Outcome: new OverloadBrightnessActionOutcome(
                EnteringOverload: enteringTransition,
                ExitingOverload: exitingTransition,
                ReduceAttempted: reduceAttempted,
                RestoreAttempted: restoreAttempted,
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: stateChanged,
                OutcomeReason: outcomeReason),
            RuntimeState: state);
    }

    private static bool ResolveCompletedActive(string direction)
    {
        return string.Equals(direction, "down", StringComparison.Ordinal);
    }

    private static string ResolveCompletedRampDirection(string direction)
    {
        _ = direction;
        return "idle";
    }

    private static string ResolveCompletedActionState(string direction)
    {
        return string.Equals(direction, "down", StringComparison.Ordinal)
            ? "dimmed"
            : "restored";
    }

    private static string ResolveCompletedOutcomeReason(string direction)
    {
        return string.Equals(direction, "down", StringComparison.Ordinal)
            ? "dimmed"
            : "restored";
    }

    private static int ClampLevel(int level)
    {
        return Math.Clamp(level, 0, 100);
    }

    private static BrightnessActionRuntimeState NormalizeRuntimeState(BrightnessActionRuntimeState runtimeState)
    {
        string actionState = string.IsNullOrWhiteSpace(runtimeState.ActionState)
            ? (runtimeState.Active ? "active" : "inactive")
            : runtimeState.ActionState;
        string rampDirection = string.IsNullOrWhiteSpace(runtimeState.RampDirection)
            ? "idle"
            : runtimeState.RampDirection.Trim().ToLowerInvariant();
        if (rampDirection is not ("up" or "down" or "idle"))
        {
            rampDirection = "idle";
        }

        return new BrightnessActionRuntimeState(
            SchemaVer: runtimeState.SchemaVer <= 0 ? 1 : runtimeState.SchemaVer,
            Active: runtimeState.Active,
            BrightnessSupported: runtimeState.BrightnessSupported,
            PreviousLevel: ClampLevel(runtimeState.PreviousLevel < 0 ? 0 : runtimeState.PreviousLevel),
            TargetLevel: ClampLevel(runtimeState.TargetLevel < 0 ? 0 : runtimeState.TargetLevel),
            CurrentLevel: ClampLevel(runtimeState.CurrentLevel < 0 ? 0 : runtimeState.CurrentLevel),
            RampDirection: rampDirection,
            LastChangeUtcMs: Math.Max(0, runtimeState.LastChangeUtcMs),
            CooldownUntilUtcMs: Math.Max(0, runtimeState.CooldownUntilUtcMs),
            LastErrorCode: runtimeState.LastErrorCode ?? string.Empty,
            BackoffUntilUtcMs: Math.Max(0, runtimeState.BackoffUntilUtcMs),
            LastTransitionSourceBucketSeq: Math.Max(0, runtimeState.LastTransitionSourceBucketSeq),
            LoggedUnsupported: runtimeState.LoggedUnsupported,
            ActionState: actionState);
    }

    private static OverloadBrightnessTransitionExecution BuildFailure(
        BrightnessActionRuntimeState runtimeState,
        OverloadTransitionContext context,
        bool enteringTransition,
        bool exitingTransition,
        bool reduceAttempted,
        bool restoreAttempted,
        string errorCode,
        string errorDetail,
        string actionState,
        long nowUtcMs)
    {
        long backoffUntilUtcMs = checked(nowUtcMs + (PortaExecutionConstants.BrightnessAdjustment.FailureBackoffSeconds * 1000L));
        string normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "unknown_brightness_error" : errorCode;
        BrightnessActionRuntimeState failedState = runtimeState with
        {
            BrightnessSupported = !string.Equals(normalizedErrorCode, "brightness_unsupported", StringComparison.Ordinal),
            LastErrorCode = normalizedErrorCode,
            BackoffUntilUtcMs = backoffUntilUtcMs,
            LastTransitionSourceBucketSeq = context.BucketSeq,
            LoggedUnsupported = runtimeState.LoggedUnsupported
                || string.Equals(normalizedErrorCode, "brightness_unsupported", StringComparison.Ordinal),
            ActionState = string.IsNullOrWhiteSpace(actionState) ? runtimeState.ActionState : actionState
        };
        return new OverloadBrightnessTransitionExecution(
            Outcome: new OverloadBrightnessActionOutcome(
                EnteringOverload: enteringTransition,
                ExitingOverload: exitingTransition,
                ReduceAttempted: reduceAttempted,
                RestoreAttempted: restoreAttempted,
                Succeeded: false,
                ErrorCode: normalizedErrorCode,
                ErrorDetail: errorDetail ?? string.Empty,
                StateChanged: false,
                OutcomeReason: "failure"),
            RuntimeState: failedState);
    }

    private static OverloadBrightnessTransitionExecution NoOp(BrightnessActionRuntimeState state, string reason)
    {
        return new OverloadBrightnessTransitionExecution(
            Outcome: new OverloadBrightnessActionOutcome(
                EnteringOverload: false,
                ExitingOverload: false,
                ReduceAttempted: false,
                RestoreAttempted: false,
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: false,
                OutcomeReason: reason),
            RuntimeState: state);
    }
}
