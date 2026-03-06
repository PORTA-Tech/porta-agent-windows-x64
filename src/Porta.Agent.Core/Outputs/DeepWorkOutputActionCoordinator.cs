namespace Porta.Agent.Core.Outputs;

public readonly record struct DeepWorkTransitionContext(
    long BucketSeq,
    DateTimeOffset BucketEndUtc,
    bool PreviousDeepWorkActive,
    bool CurrentDeepWorkActive,
    bool BaselineReady,
    bool LowQualityPaused,
    double Confidence);

public readonly record struct DeepWorkActionOutcome(
    bool EnteringDeepWork,
    bool ExitingDeepWork,
    bool SuppressionAttempted,
    bool RestoreAttempted,
    bool Succeeded,
    string ErrorCode);

public interface IDeepWorkOutputActionCoordinator
{
    DeepWorkActionOutcome HandleDeepWorkTransition(DeepWorkTransitionContext context);
}

public sealed class DeepWorkOutputActionCoordinator : IDeepWorkOutputActionCoordinator
{
    private readonly INotificationController _notificationController;
    private NotificationControllerSnapshot? _previousSnapshot;

    public DeepWorkOutputActionCoordinator(INotificationController notificationController)
    {
        _notificationController = notificationController ?? throw new ArgumentNullException(nameof(notificationController));
    }

    public DeepWorkActionOutcome HandleDeepWorkTransition(DeepWorkTransitionContext context)
    {
        bool entering = !context.PreviousDeepWorkActive && context.CurrentDeepWorkActive;
        bool exiting = context.PreviousDeepWorkActive && !context.CurrentDeepWorkActive;
        if (!entering && !exiting)
        {
            return new DeepWorkActionOutcome(
                EnteringDeepWork: false,
                ExitingDeepWork: false,
                SuppressionAttempted: false,
                RestoreAttempted: false,
                Succeeded: true,
                ErrorCode: string.Empty);
        }

        try
        {
            if (entering)
            {
                NotificationControllerSnapshot snapshot = _notificationController.GetSnapshot();
                _notificationController.ApplySuppression(snapshot);
                _previousSnapshot = snapshot;
                return new DeepWorkActionOutcome(
                    EnteringDeepWork: true,
                    ExitingDeepWork: false,
                    SuppressionAttempted: true,
                    RestoreAttempted: false,
                    Succeeded: true,
                    ErrorCode: string.Empty);
            }

            bool restored = false;
            if (_previousSnapshot.HasValue)
            {
                _notificationController.Restore(_previousSnapshot.Value);
                _previousSnapshot = null;
                restored = true;
            }

            return new DeepWorkActionOutcome(
                EnteringDeepWork: false,
                ExitingDeepWork: true,
                SuppressionAttempted: false,
                RestoreAttempted: restored,
                Succeeded: true,
                ErrorCode: string.Empty);
        }
        catch (Exception ex)
        {
            return new DeepWorkActionOutcome(
                EnteringDeepWork: entering,
                ExitingDeepWork: exiting,
                SuppressionAttempted: entering,
                RestoreAttempted: exiting,
                Succeeded: false,
                ErrorCode: $"notification_controller_error:{ex.GetType().Name}");
        }
    }
}
