using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests.Harness;

public readonly record struct OutputActionStep(
    bool DeepWorkActive,
    bool OverloadActive,
    bool BaselineReady = true,
    bool LowQualityPaused = false,
    double DeepWorkConfidence = 0.80d,
    double OverloadLikelihood = 0.85d,
    double OverloadConfidence = 0.80d);

public readonly record struct OutputActionTickResult(
    long BucketSeq,
    DateTimeOffset BucketEndUtc,
    DeepWorkTransitionExecution DeepWorkExecution,
    OverloadBrightnessTransitionExecution OverloadExecution,
    NotificationSuppressionRuntimeState NotificationRuntimeState,
    BrightnessActionRuntimeState BrightnessRuntimeState);

public readonly record struct OutputActionRestartResult(
    NotificationSuppressionStartupReconciliation NotificationReconciliation,
    BrightnessStartupReconciliation BrightnessReconciliation);

public sealed class OutputActionScenarioRunner
{
    private readonly INotificationController _notificationController;
    private readonly IBrightnessController _brightnessController;
    private IDeepWorkOutputActionCoordinator _deepWorkCoordinator;
    private IOverloadBrightnessActionCoordinator _overloadCoordinator;
    private bool _previousDeepWorkActive;
    private bool _previousOverloadActive;
    private long _bucketSeq;

    public OutputActionScenarioRunner(
        DeterministicTestClock clock,
        INotificationController notificationController,
        IBrightnessController brightnessController,
        NotificationSuppressionRuntimeState? notificationRuntimeState = null,
        BrightnessActionRuntimeState? brightnessRuntimeState = null,
        bool initialDeepWorkActive = false,
        bool initialOverloadActive = false,
        long initialBucketSeq = 0)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _notificationController = notificationController ?? throw new ArgumentNullException(nameof(notificationController));
        _brightnessController = brightnessController ?? throw new ArgumentNullException(nameof(brightnessController));
        _deepWorkCoordinator = new DeepWorkOutputActionCoordinator(_notificationController);
        _overloadCoordinator = new OverloadBrightnessActionCoordinator(_brightnessController);

        NotificationRuntimeState = notificationRuntimeState ?? NotificationSuppressionRuntimeState.CreateDefault();
        BrightnessRuntimeState = brightnessRuntimeState ?? BrightnessActionRuntimeState.CreateDefault();
        _previousDeepWorkActive = initialDeepWorkActive;
        _previousOverloadActive = initialOverloadActive;
        _bucketSeq = initialBucketSeq;
    }

    public DeterministicTestClock Clock { get; }

    public NotificationSuppressionRuntimeState NotificationRuntimeState { get; private set; }

    public BrightnessActionRuntimeState BrightnessRuntimeState { get; private set; }

    public OutputActionTickResult Tick(OutputActionStep step)
    {
        _bucketSeq++;
        DateTimeOffset nowUtc = Clock.UtcNow;

        DeepWorkTransitionContext deepContext = new(
            BucketSeq: _bucketSeq,
            BucketEndUtc: nowUtc,
            PreviousDeepWorkActive: _previousDeepWorkActive,
            CurrentDeepWorkActive: step.DeepWorkActive,
            BaselineReady: step.BaselineReady,
            LowQualityPaused: step.LowQualityPaused,
            Confidence: step.DeepWorkConfidence);
        DeepWorkTransitionExecution deepExecution = _deepWorkCoordinator.HandleDeepWorkTransition(
            deepContext,
            NotificationRuntimeState);
        NotificationRuntimeState = deepExecution.RuntimeState;

        OverloadTransitionContext overloadContext = new(
            BucketSeq: _bucketSeq,
            BucketEndUtc: nowUtc,
            PreviousOverloadActive: _previousOverloadActive,
            CurrentOverloadActive: step.OverloadActive,
            LowQualityPaused: step.LowQualityPaused,
            OverloadLikelihood: step.OverloadLikelihood,
            Confidence: step.OverloadConfidence);
        OverloadBrightnessTransitionExecution overloadExecution = _overloadCoordinator.HandleOverloadTransition(
            overloadContext,
            BrightnessRuntimeState);
        BrightnessRuntimeState = overloadExecution.RuntimeState;

        _previousDeepWorkActive = step.DeepWorkActive;
        _previousOverloadActive = step.OverloadActive;
        Clock.AdvanceBuckets(1);

        return new OutputActionTickResult(
            BucketSeq: _bucketSeq,
            BucketEndUtc: nowUtc,
            DeepWorkExecution: deepExecution,
            OverloadExecution: overloadExecution,
            NotificationRuntimeState: NotificationRuntimeState,
            BrightnessRuntimeState: BrightnessRuntimeState);
    }

    public OutputActionRestartResult Restart()
    {
        _deepWorkCoordinator = new DeepWorkOutputActionCoordinator(_notificationController);
        _overloadCoordinator = new OverloadBrightnessActionCoordinator(_brightnessController);

        DateTimeOffset nowUtc = Clock.UtcNow;
        NotificationSuppressionStartupReconciliation notification = _deepWorkCoordinator.ReconcileOnStartup(
            NotificationRuntimeState,
            nowUtc);
        NotificationRuntimeState = notification.RuntimeState;

        BrightnessStartupReconciliation brightness = _overloadCoordinator.ReconcileOnStartup(
            BrightnessRuntimeState,
            nowUtc);
        BrightnessRuntimeState = brightness.RuntimeState;

        return new OutputActionRestartResult(
            NotificationReconciliation: notification,
            BrightnessReconciliation: brightness);
    }
}
