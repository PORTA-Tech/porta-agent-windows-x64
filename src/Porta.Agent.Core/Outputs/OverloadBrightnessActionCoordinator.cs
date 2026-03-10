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

public readonly record struct BrightnessStartupReconciliation(
    BrightnessActionRuntimeState RuntimeState,
    bool Reconciled,
    string OutcomeReason);

public interface IOverloadBrightnessActionCoordinator
{
    OverloadBrightnessTransitionExecution HandleOverloadTransition(
        OverloadTransitionContext context,
        BrightnessActionRuntimeState runtimeState,
        RuntimeBrightnessAdjustmentSettings? policy = null);

    BrightnessStartupReconciliation ReconcileOnStartup(
        BrightnessActionRuntimeState runtimeState,
        DateTimeOffset nowUtc,
        RuntimeBrightnessAdjustmentSettings? policy = null);
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
        BrightnessActionRuntimeState runtimeState,
        RuntimeBrightnessAdjustmentSettings? policy = null)
    {
        RuntimeBrightnessAdjustmentSettings settings = policy ?? new RuntimeBrightnessAdjustmentSettings(
            PortaExecutionConstants.BrightnessAdjustment.ReductionDelta,
            PortaExecutionConstants.BrightnessAdjustment.MinSafeBrightness,
            PortaExecutionConstants.BrightnessAdjustment.RampStepPerBucket,
            PortaExecutionConstants.BrightnessAdjustment.CooldownSeconds,
            PortaExecutionConstants.BrightnessAdjustment.FailureBackoffSeconds);
        BrightnessActionRuntimeState state = NormalizeRuntimeState(runtimeState);
        long nowUtcMs = context.BucketEndUtc.ToUnixTimeMilliseconds();
        bool enteringTransition = !context.PreviousOverloadActive && context.CurrentOverloadActive;
        bool exitingTransition = context.PreviousOverloadActive && !context.CurrentOverloadActive;

        if (enteringTransition)
        {
            return HandleEntering(context, state, nowUtcMs, settings);
        }

        if (state.Active && context.CurrentOverloadActive && string.Equals(state.RampDirection, "down", StringComparison.Ordinal))
        {
            return TryRampStep(context, state, nowUtcMs, enteringTransition: false, exitingTransition: false, direction: "down", settings);
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
                direction: "up",
                settings);
        }

        return NoOp(state, "no_transition");
    }

    public BrightnessStartupReconciliation ReconcileOnStartup(
        BrightnessActionRuntimeState runtimeState,
        DateTimeOffset nowUtc,
        RuntimeBrightnessAdjustmentSettings? policy = null)
    {
        RuntimeBrightnessAdjustmentSettings settings = policy ?? new RuntimeBrightnessAdjustmentSettings(
            PortaExecutionConstants.BrightnessAdjustment.ReductionDelta,
            PortaExecutionConstants.BrightnessAdjustment.MinSafeBrightness,
            PortaExecutionConstants.BrightnessAdjustment.RampStepPerBucket,
            PortaExecutionConstants.BrightnessAdjustment.CooldownSeconds,
            PortaExecutionConstants.BrightnessAdjustment.FailureBackoffSeconds);
        BrightnessActionRuntimeState state = NormalizeRuntimeState(runtimeState);
        long nowUtcMs = nowUtc.ToUnixTimeMilliseconds();

        if (!state.Active)
        {
            return new BrightnessStartupReconciliation(
                RuntimeState: state,
                Reconciled: false,
                OutcomeReason: "startup_inactive");
        }

        if (!_brightnessController.IsSupported())
        {
            BrightnessActionRuntimeState unsupported = state with
            {
                Active = false,
                BrightnessSupported = false,
                RampDirection = "idle",
                LastErrorCode = "brightness_unsupported",
                BackoffUntilUtcMs = Math.Max(
                    state.BackoffUntilUtcMs,
                    checked(nowUtcMs + (settings.FailureBackoffSeconds * 1000L))),
                LoggedUnsupported = true,
                ActionState = "startup_unsupported"
            };
            return new BrightnessStartupReconciliation(
                RuntimeState: unsupported,
                Reconciled: true,
                OutcomeReason: "startup_unsupported");
        }

        BrightnessControllerLevelResult levelResult = _brightnessController.GetLevel();
        if (!levelResult.Succeeded || !levelResult.IsSupported)
        {
            string errorCode = string.IsNullOrWhiteSpace(levelResult.ErrorCode)
                ? "startup_brightness_get_failed"
                : levelResult.ErrorCode;
            BrightnessActionRuntimeState failed = state with
            {
                BrightnessSupported = true,
                LastErrorCode = errorCode,
                BackoffUntilUtcMs = Math.Max(
                    state.BackoffUntilUtcMs,
                    checked(nowUtcMs + (settings.FailureBackoffSeconds * 1000L))),
                ActionState = "startup_reconcile_failed"
            };
            return new BrightnessStartupReconciliation(
                RuntimeState: failed,
                Reconciled: true,
                OutcomeReason: "startup_get_level_failed");
        }

        int actualLevel = ClampLevel(levelResult.Level);
        string direction = NormalizeDirection(state.RampDirection);
        if (direction == "idle")
        {
            if (actualLevel > state.TargetLevel)
            {
                direction = "down";
            }
            else if (actualLevel < state.PreviousLevel)
            {
                direction = "up";
            }
        }

        BrightnessActionRuntimeState reconciled = state with
        {
            BrightnessSupported = true,
            CurrentLevel = actualLevel,
            LastChangeUtcMs = nowUtcMs,
            LastErrorCode = string.Empty,
            BackoffUntilUtcMs = 0
        };

        if (direction == "down")
        {
            if (actualLevel <= state.TargetLevel)
            {
                reconciled = reconciled with
                {
                    Active = true,
                    RampDirection = "idle",
                    ActionState = "dimmed"
                };
            }
            else
            {
                reconciled = reconciled with
                {
                    Active = true,
                    RampDirection = "down",
                    ActionState = "ramping_down"
                };
            }
        }
        else if (direction == "up")
        {
            if (actualLevel >= state.PreviousLevel)
            {
                reconciled = reconciled with
                {
                    Active = false,
                    RampDirection = "idle",
                    CooldownUntilUtcMs = checked(nowUtcMs + (settings.CooldownSeconds * 1000L)),
                    ActionState = "restored"
                };
            }
            else
            {
                reconciled = reconciled with
                {
                    Active = true,
                    RampDirection = "up",
                    ActionState = "ramping_up"
                };
            }
        }
        else
        {
            reconciled = reconciled with
            {
                Active = actualLevel < state.PreviousLevel,
                RampDirection = "idle",
                ActionState = actualLevel >= state.PreviousLevel ? "restored" : "dimmed"
            };
        }

        return new BrightnessStartupReconciliation(
            RuntimeState: reconciled,
            Reconciled: !reconciled.Equals(state),
            OutcomeReason: "startup_reconciled");
    }

    private OverloadBrightnessTransitionExecution HandleEntering(
        OverloadTransitionContext context,
        BrightnessActionRuntimeState state,
        long nowUtcMs,
        RuntimeBrightnessAdjustmentSettings settings)
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
                return TryRampStep(context, resumed, nowUtcMs, enteringTransition: true, exitingTransition: false, direction: "down", settings);
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
                nowUtcMs: nowUtcMs,
                settings);
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
                nowUtcMs: nowUtcMs,
                settings);
        }

        int prevLevel = ClampLevel(levelResult.Level);
        int targetLevel = Math.Max(
            prevLevel - settings.ReductionDelta,
            settings.MinSafeBrightness);
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

        return TryRampStep(context, activeState, nowUtcMs, enteringTransition: true, exitingTransition: false, direction: "down", settings);
    }

    private OverloadBrightnessTransitionExecution TryRampStep(
        OverloadTransitionContext context,
        BrightnessActionRuntimeState state,
        long nowUtcMs,
        bool enteringTransition,
        bool exitingTransition,
        string direction,
        RuntimeBrightnessAdjustmentSettings settings)
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
                nowUtcMs: nowUtcMs,
                settings);
        }

        int currentLevel = ClampLevel(state.CurrentLevel < 0 ? state.PreviousLevel : state.CurrentLevel);
        int destination = string.Equals(direction, "down", StringComparison.Ordinal)
            ? Math.Max(state.TargetLevel, currentLevel - settings.RampStepPerBucket)
            : Math.Min(state.PreviousLevel, currentLevel + settings.RampStepPerBucket);
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
                    ? checked(nowUtcMs + (settings.CooldownSeconds * 1000L))
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
                nowUtcMs: nowUtcMs,
                settings);
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
                CooldownUntilUtcMs = checked(nowUtcMs + (settings.CooldownSeconds * 1000L))
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
        string rampDirection = NormalizeDirection(runtimeState.RampDirection);

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

    private static string NormalizeDirection(string direction)
    {
        string normalized = string.IsNullOrWhiteSpace(direction)
            ? "idle"
            : direction.Trim().ToLowerInvariant();
        return normalized is "up" or "down" or "idle"
            ? normalized
            : "idle";
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
        long nowUtcMs,
        RuntimeBrightnessAdjustmentSettings? policy = null)
    {
        RuntimeBrightnessAdjustmentSettings settings = policy ?? new RuntimeBrightnessAdjustmentSettings(
            PortaExecutionConstants.BrightnessAdjustment.ReductionDelta,
            PortaExecutionConstants.BrightnessAdjustment.MinSafeBrightness,
            PortaExecutionConstants.BrightnessAdjustment.RampStepPerBucket,
            PortaExecutionConstants.BrightnessAdjustment.CooldownSeconds,
            PortaExecutionConstants.BrightnessAdjustment.FailureBackoffSeconds);
        long backoffUntilUtcMs = checked(nowUtcMs + (settings.FailureBackoffSeconds * 1000L));
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
