namespace Porta.UserAgent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Common.UserAgent;
using Porta.Agent.Core;
using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi;
using Porta.Agent.Runtime.Input;
using Porta.UserAgent.Interventions;
using Porta.UserAgent.Input;
using System.Diagnostics;
using System.IO;

public sealed class UserAgentHost : BackgroundService, IDisposable
{
    private static readonly TimeSpan PassiveSamplingPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SessionHeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WidgetMaintenanceInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LiveSignalPublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LiveSignalActiveWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LiveFocusIdleResetThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RecentEventsRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WidgetDetailRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AriContextRefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LabelPromptCheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LabelPromptDisplayWindow = TimeSpan.FromSeconds(30);
    // Phase 1c — signup nudge: rare by design (weekly-capped), so check occasionally and keep it up
    // a little longer than the label pill (it's a conversion moment, not a quick tap).
    private static readonly TimeSpan SignupNudgeCheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SignupNudgeDisplayWindow = TimeSpan.FromSeconds(90);
    private readonly ILogger<UserAgentHost> _logger;
    private readonly IUserAgentSessionContextStore _sessionContextStore;
    private readonly UserAgentStartupMetadata _startupMetadata;
    private readonly LocalApiClient _localApiClient = new();
    private readonly UserDesktopWidgetPresenter _toastRenderer = new();
    private readonly UserSessionWindowsNotificationController _notificationController = new();
    private readonly UserAgentNotificationSnapshotStore _notificationSnapshotStore = new();
    private readonly UserInterventionPreferenceStore _interventionPreferenceStore = new();
    // Workstream D — the always-on-top check-in card lives on its own STA thread (like the break
    // overlay) so it fires even when the widget is hidden. Owned here; passed into the executor's
    // gated check-in door and disposed on shutdown.
    private readonly UserCheckInCardPresenter _checkInCardPresenter = new();
    private readonly UserAgentInterventionExecutor _interventionExecutor;
    private readonly UserSessionSignalAggregator _signalAggregator = new();
    private readonly object _liveSignalGate = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly UserKeyboardActivityPoller _keyboardPoller = new();
    private readonly MouseHook _mouseHook = new();
    // Tracks the Environment.TickCount at the last keyboard HOOK event so the
    // concurrent polling fallback can suppress duplicate events when the hook is
    // working. When the hook goes silent (e.g. elevated-integrity foreground app),
    // elapsed ticks exceed the dedup window and poller events pass through.
    private int _lastHookKeyboardEventTick = 0;
    private readonly CaptureSelfTestCoordinator _captureSelfTest = new();
    private readonly string _agentInstanceId = BuildAgentInstanceId();
    private DateTimeOffset _sessionStartedUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastLiveSignalPublishUtc = DateTimeOffset.MinValue;
    private long _lastRecoveryEventId;
    private UserInterventionToastRenderer.UserTrayVisualState? _lastTrayState;
    private UserDesktopWidgetState? _lastWidgetState;
    private IReadOnlyList<LocalApiEventResponse> _recentEvents = [];
    private DateTimeOffset _lastRecentEventsRefreshUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<LocalApiEventResponse> _widgetDetailEvents = [];
    private LocalApiTimelineResponse? _widgetDetailTimeline;
    private LocalApiSettingsResponse? _widgetDetailSettings;
    private DateTimeOffset _lastWidgetDetailRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUiRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLabelPromptCheckUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _labelPromptShownUtc = DateTimeOffset.MinValue;
    private UserDesktopWidgetLabelPrompt? _activeLabelPrompt;
    private DateTimeOffset _lastSignupNudgeCheckUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _signupNudgeShownUtc = DateTimeOffset.MinValue;
    private UserDesktopWidgetSignupNudge? _activeSignupNudge;
    // Phase 2c — reminders: polled next reminder + the last one escalated to a system balloon.
    private static readonly TimeSpan ReminderCheckInterval = TimeSpan.FromSeconds(10);
    private DateTimeOffset _lastReminderCheckUtc = DateTimeOffset.MinValue;
    private LocalApiReminder? _nextReminder;
    private long _lastSystemNotifiedReminderId = -1;
    // Workstream 4 — today's timed tasks: same poll cadence as reminders. The expiry
    // nudge fires ONCE per (task, budget) — extending resets the budget and re-arms it.
    private static readonly TimeSpan TasksCheckInterval = TimeSpan.FromSeconds(10);
    private DateTimeOffset _lastTasksCheckUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<LocalApiTask> _todayTasks = Array.Empty<LocalApiTask>();
    private readonly HashSet<string> _taskExpiryToastedKeys = new(StringComparer.Ordinal);
    // Workstream D — conversational check-ins. Polled infrequently (the scheduler + per-day mute
    // do the real once-per-day gating); a cold-start grace keeps a morning card from popping while
    // the agent is still settling right after launch.
    private static readonly TimeSpan CheckInCheckInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInColdStartGrace = TimeSpan.FromSeconds(90);
    private DateTimeOffset _lastCheckInCheckUtc = DateTimeOffset.MinValue;
    // Part 1c — settle-in: when the user became continuously active (reset on idle/away). null = not
    // currently in a known-active run. Updated every poll so the 3-min window is measured accurately.
    private DateTimeOffset? _checkInActiveSinceUtc;
    private bool _widgetFallbackLogged;
    private bool _hooksInstalled;
    private bool _keyboardCaptureAvailable;
    private bool _mouseCaptureAvailable;
    private bool _localApiUnavailableLogged;
    private int _heartbeatWriteFailureCount;
    private int _consecutiveSignalDeliveryFailures;
    // Fleet §1a — throttle for the periodic gui_resources log line.
    private static readonly TimeSpan GuiResourceLogInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset _lastGuiResourceLogUtc = DateTimeOffset.MinValue;
    private string? _captureFailureCode;
    private long _lastCaptureLoopTickUtcTicks;
    private long _captureLoopFailureStreak;
    private volatile bool _shouldExit;
    private DateTimeOffset? _selfTestFailedSinceUtc;
    private bool _heartbeatReadyLogged;
    private bool _systemEventsSubscribed;
    private int _sessionId;
    private string _captureContext = UserAgentCaptureContexts.Unknown;
    private readonly string _binaryVersion = ResolveBinaryVersion();
    private long _heartbeatSequence;
    private DateTimeOffset? _lastLiveKeyboardUtc;
    private DateTimeOffset? _lastLiveMouseUtc;
    private DateTimeOffset? _lastLiveInputUtc;
    private string? _lastLiveFocusAppHash;
    private double _lastLiveIdleSeconds;
    private DateTimeOffset? _liveFocusStartedUtc;
    private UserAgentSessionContextSnapshot? _lastPublishedSessionContext;
    private readonly UserAgentSessionState _sessionState = new();
    private LocalApiAriContextResponse? _ariContext;
    private DateTimeOffset _lastAriContextRefreshUtc = DateTimeOffset.MinValue;

    public UserAgentHost(
        ILogger<UserAgentHost> logger,
        IUserAgentSessionContextStore sessionContextStore,
        UserAgentStartupMetadata startupMetadata)
    {
        _logger = logger;
        _sessionContextStore = sessionContextStore;
        _startupMetadata = startupMetadata;
        _interventionExecutor = new UserAgentInterventionExecutor(
            _toastRenderer,
            new UserSessionPresentationGate(),
            _notificationController,
            _notificationSnapshotStore,
            _interventionPreferenceStore,
            checkInCardPresenter: _checkInCardPresenter);
        // Hook fires immediately but is bypassed by Windows for elevated-integrity
        // foreground apps. We track the last hook-tick so the poller can suppress
        // duplicates when the hook IS working and take over when it goes silent.
        _keyboardHook.KeyChanged += (isKeyDown, isBackspace, isInjected) =>
        {
            if (isKeyDown && !isInjected)
            {
                Volatile.Write(ref _lastHookKeyboardEventTick, Environment.TickCount);
            }

            OnKeyboardChanged(isKeyDown, isBackspace, isInjected);
        };
        // Poller (GetAsyncKeyState) works even for elevated-integrity foreground apps.
        // Suppress its events if the hook fired within the last 30 ms to avoid
        // double-counting the same keypress.
        _keyboardPoller.KeyChanged += (isKeyDown, isBackspace, isInjected) =>
        {
            if (isKeyDown && !isInjected)
            {
                int lastHookTick = Volatile.Read(ref _lastHookKeyboardEventTick);
                int elapsed = unchecked(Environment.TickCount - lastHookTick);
                if (elapsed >= 0 && elapsed < 30)
                {
                    return; // Hook is active — let it own the event
                }
            }

            OnKeyboardChanged(isKeyDown, isBackspace, isInjected);
        };
        _mouseHook.MouseMoved += OnMouseMoved;
        _mouseHook.ScrollDeltaChanged += OnMouseWheelChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Process currentProcess = Process.GetCurrentProcess();
        _sessionId = currentProcess.SessionId;
        _captureContext = IsInteractiveUserSession(_sessionId)
            ? UserAgentCaptureContexts.UserSession
            : UserAgentCaptureContexts.ServiceSession;
        _sessionStartedUtc = DateTimeOffset.UtcNow;
        _windowStartUtc = _sessionStartedUtc;

        _logger.LogInformation(
            "Porta.UserAgent running in process {ProcessId} with session {SessionId}, capture context {CaptureContext}, agent instance {AgentInstanceId}.",
            Environment.ProcessId,
            _sessionId,
            _captureContext,
            _agentInstanceId);
        UserAgentFileLog.Info(
            $"user_agent_host_started source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} capture_context={_captureContext} agent_instance={_agentInstanceId} heartbeat_path={ResolveSessionContextPath()}");

        if (_captureContext == UserAgentCaptureContexts.ServiceSession)
        {
            _logger.LogWarning("Porta.UserAgent started outside an interactive desktop session; capture stays in heartbeat-only mode.");
            UserAgentFileLog.Info(
                $"user_agent_non_interactive_session source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId}");
            _captureFailureCode = "service_session";
            PublishSessionContext();
            UserAgentFileLog.Info(
                $"user_agent_exiting_service_session pid={Environment.ProcessId} session={_sessionId}");
            return;
        }

        StartCaptureHooks();
        SubscribeToSessionSwitchNotifications();
        ArmCaptureSelfTest(_startupMetadata.EffectiveLaunchReason);

        Task heartbeatTask = RunHeartbeatLoopAsync(stoppingToken);
        Task widgetMaintenanceTask = RunWidgetMaintenanceLoopAsync(stoppingToken);
        _ = ShowUpdateToastIfVersionChangedAsync(stoppingToken);

        UpdateTrayState(new(
            UserInterventionToastRenderer.UserTrayMode.Normal,
            "PORTA | Starting"));

        try
        {
            await RefreshLocalUiSignalsAsync(stoppingToken).ConfigureAwait(false);
            _lastUiRefreshUtc = DateTimeOffset.UtcNow;

            using var timer = new PeriodicTimer(PassiveSamplingPeriod);

            while (!stoppingToken.IsCancellationRequested && !_shouldExit)
            {
                if (_captureContext == UserAgentCaptureContexts.UserSession)
                {
                    // CRITICAL: one unhandled exception in this body must never kill capture
                    // while the heartbeat loop keeps reporting the process as healthy.
                    // (Root cause of fleet-wide "Activity: No" — loop died, heartbeats lived.)
                    try
                    {
                        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                        Interlocked.Exchange(ref _lastCaptureLoopTickUtcTicks, nowUtc.UtcTicks);
                        SampleAmbientSignals(nowUtc);
                        await PublishLiveSessionStateIfDueAsync(nowUtc, stoppingToken).ConfigureAwait(false);
                        await EvaluateCaptureSelfTestAsync(nowUtc, stoppingToken).ConfigureAwait(false);
                        if ((nowUtc - _lastUiRefreshUtc) >= UiRefreshInterval)
                        {
                            await RefreshLocalUiSignalsAsync(stoppingToken).ConfigureAwait(false);
                            _lastUiRefreshUtc = nowUtc;
                        }

                        // Keep the durable input/analytics flush on the shared bucket cadence.
                        // Later live-session UI work should add a separate fast path instead of shrinking this window.
                        if ((nowUtc - _windowStartUtc) >= TimeSpan.FromSeconds(Time.bucketSeconds))
                        {
                            await FlushSignalsAsync(stoppingToken).ConfigureAwait(false);
                            await RefreshLocalUiSignalsAsync(stoppingToken).ConfigureAwait(false);
                            _lastUiRefreshUtc = DateTimeOffset.UtcNow;
                            await PollPendingInterventionsAsync(stoppingToken).ConfigureAwait(false);
                        }

                        Interlocked.Exchange(ref _captureLoopFailureStreak, 0);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        long streak = Interlocked.Increment(ref _captureLoopFailureStreak);
                        if (streak <= 3 || streak % 60 == 0)
                        {
                            UserAgentFileLog.Error(
                                $"capture_loop_iteration_failed pid={Environment.ProcessId} session={_sessionId} streak={streak}",
                                ex);
                            _logger.LogError(ex, "Capture loop iteration failed (streak {Streak}). Continuing.", streak);
                        }
                    }
                }

                try
                {
                    if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await widgetMaintenanceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Porta.UserAgent stopping.");
        UserAgentFileLog.Info(
            $"user_agent_host_stopping source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId}");
        UnsubscribeFromSessionSwitchNotifications();
        StopCaptureHooks();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        UnsubscribeFromSessionSwitchNotifications();
        StopCaptureHooks();
        _keyboardHook.Dispose();
        _keyboardPoller.Dispose();
        _mouseHook.Dispose();
        _toastRenderer.Dispose();
        _checkInCardPresenter.Dispose();
        _localApiClient.Dispose();
        base.Dispose();
    }

    private async Task RefreshLocalUiSignalsAsync(CancellationToken token)
    {
        if (!_localApiClient.IsConfigured)
        {
            UpdateTrayState(new(
                UserInterventionToastRenderer.UserTrayMode.Unavailable,
                "PORTA | Waiting for core service"));
            UpdateWidgetState(UserDesktopWidgetStateBuilder.Build(
                status: null,
                snapshot: null,
                liveSession: null,
                signals: new UserAgentVisualSignals(
                    Mode: UserAgentVisualMode.Unavailable,
                    TooltipText: "PORTA | Waiting for core service",
                    ShowDeepWorkStreak: false,
                    DeepWorkStreakText: string.Empty,
                    ShowRecoveryToast: false,
                    RecoveryEventId: 0,
                    RecoveryMessage: string.Empty),
                degradedToast: null,
                recentEvents: _widgetDetailEvents,
                timeline: _widgetDetailTimeline,
                settings: _widgetDetailSettings,
                statusHttpCode: null,
                nowUtc: DateTimeOffset.UtcNow));
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiStatusResponse? status = await _localApiClient.GetStatusAsync(token).ConfigureAwait(false);
        LocalApiCurrentSnapshotResponse? snapshot = await _localApiClient.GetCurrentSnapshotAsync(token).ConfigureAwait(false);
        LocalApiLiveSessionStateResponse? liveSession = await _localApiClient.GetLiveSessionStateAsync(token).ConfigureAwait(false);
        IReadOnlyList<LocalApiEventResponse> recentEvents = await RefreshRecentEventsAsync(nowUtc, token).ConfigureAwait(false);
        WidgetDetailSnapshot widgetDetails = _toastRenderer.SupportsWidgetSurface
            ? await RefreshWidgetDetailsAsync(nowUtc, token).ConfigureAwait(false)
            : WidgetDetailSnapshot.Empty;
        UserAgentVisualSignals signals = UserAgentVisualSignalsAdapter.Build(
            status,
            snapshot,
            recentEvents,
            nowUtc,
            _lastRecoveryEventId);

        UserCommunicationToast? captureDegradedToast = UserCommunicationRouter.ResolveCaptureDegradedState(status, snapshot);

        UpdateTrayState(new(
            MapTrayMode(signals.Mode),
            signals.TooltipText));
        await RefreshLabelPromptAsync(nowUtc, token).ConfigureAwait(false);
        await RefreshSignupNudgeAsync(nowUtc, token).ConfigureAwait(false);
        await RefreshReminderAsync(nowUtc, token).ConfigureAwait(false);
        await RefreshTasksAsync(nowUtc, snapshot, token).ConfigureAwait(false);
        await RefreshCheckInAsync(nowUtc, snapshot, liveSession, token).ConfigureAwait(false);
        UpdateWidgetState(UserDesktopWidgetStateBuilder.Build(
            status,
            snapshot,
            liveSession,
            signals,
            captureDegradedToast,
            widgetDetails.RecentEvents,
            widgetDetails.Timeline,
            widgetDetails.Settings,
            _localApiClient.LastStatusHttpCode,
            nowUtc) with
            {
                LabelPrompt = _activeLabelPrompt,
                SignupNudge = _activeSignupNudge,
                NextReminder = MapNextReminder(),
                Tasks = MapTodayTasks()
            });

        // Refresh Ari context from local API every 30 min
        if ((nowUtc - _lastAriContextRefreshUtc) >= AriContextRefreshInterval)
        {
            LocalApiAriContextResponse? freshContext = await _localApiClient.GetAriContextAsync(token).ConfigureAwait(false);
            if (freshContext is not null)
            {
                _ariContext = freshContext;
            }

            _lastAriContextRefreshUtc = nowUtc;
        }

        // Daily session state reset + distraction tracking + Ari behavioral toasts
        _sessionState.ResetIfNewDay(nowUtc);
        if (snapshot is not null)
        {
            if (snapshot.DistractionEpisodeActive && !_sessionState.InDistractionEpisode)
            {
                _sessionState.MarkDistractionStarted(snapshot.FocusEpisodeSeconds, nowUtc);
            }
            else if (!snapshot.DistractionEpisodeActive && _sessionState.InDistractionEpisode)
            {
                _sessionState.MarkDistractionEnded();
            }

            UserCommunicationToast? ariToast = UserCommunicationRouter.TryBuildAriToast(
                snapshot, _sessionState, _ariContext, nowUtc);
            if (ariToast is not null)
            {
                RouteBehavioralToast(ariToast, nowUtc);
            }
        }

        if (!signals.ShowRecoveryToast || signals.RecoveryEventId <= 0)
        {
            return;
        }

        _lastRecoveryEventId = signals.RecoveryEventId;

        // B1: the overload-recovery message is an interrupt of the ari:recovery class — route
        // it through the anti-spam gate like every other interrupt, never straight to the
        // renderer. (Presentation availability is checked inside the gated path.)
        _interventionExecutor.TryShowGatedInterruptToast(
            "ari:recovery",
            "PORTA",
            signals.RecoveryMessage,
            nowUtc,
            out _);
    }

    // Workstream B1 — behavioral-toast routing (LOCKED classification). Interrupt classes
    // (recovery, one-time first-win) go through the anti-spam gate; every other behavioral
    // message is demoted to the activity log and NEVER reaches the toast renderer. The widget
    // already surfaces live focus state, so nothing is lost. TryBuildAriToast maintains its own
    // cadence bookkeeping, so no extra RecordToastShown here.
    private void RouteBehavioralToast(UserCommunicationToast ariToast, DateTimeOffset nowUtc)
    {
        if (UserCommunicationRouter.IsInterruptToast(ariToast.Key))
        {
            _interventionExecutor.TryShowGatedInterruptToast(
                UserCommunicationRouter.ResolveAriGateKey(ariToast.Key),
                ariToast.Title,
                ariToast.Message,
                nowUtc,
                out _);
            return;
        }

        UserAgentFileLog.Info($"activity_behavioral key={ariToast.Key} message={ariToast.Message}");
    }

    /// <summary>
    /// The ONLY thing users see from silent auto-update: one small widget toast
    /// after the first start on a new version. The acknowledged-version file makes
    /// it fire exactly once per version; a fresh install records silently.
    /// </summary>
    private async Task ShowUpdateToastIfVersionChangedAsync(CancellationToken token)
    {
        try
        {
            // Let the widget surface finish starting before presenting anything.
            await Task.Delay(TimeSpan.FromSeconds(25), token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _shouldExit || string.IsNullOrWhiteSpace(_binaryVersion))
            {
                return;
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PortaAgent",
                "last-acknowledged-version.txt");

            string? previous = null;
            try
            {
                if (File.Exists(path))
                {
                    previous = File.ReadAllText(path).Trim();
                }
            }
            catch
            {
            }

            if (string.Equals(previous, _binaryVersion, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (previous is null)
            {
                // Fresh install - nothing to announce, just remember this version.
                TryWriteAcknowledgedVersion(path, _binaryVersion);
                return;
            }

            string display = _binaryVersion;
            if (Version.TryParse(_binaryVersion, out Version? parsed))
            {
                display = $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
            }

            // B1 (LOCKED classification): the update confirmation is NOT an interrupt — it goes
            // to the activity log as one quiet status, never a popup. Record the acknowledged
            // version so it is logged exactly once per version.
            UserAgentFileLog.Info($"activity_agent_updated version={_binaryVersion} display={display}");
            TryWriteAcknowledgedVersion(path, _binaryVersion);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("agent_update_toast_failed", ex);
        }
    }

    private static void TryWriteAcknowledgedVersion(string path, string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, version);
        }
        catch
        {
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken token)
    {
        PublishSessionContext();
        if (_shouldExit)
        {
            return;
        }

        using var timer = new PeriodicTimer(SessionHeartbeatInterval);
        while (!token.IsCancellationRequested && !_shouldExit)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            PublishSessionContext();
        }
    }

    private async Task RunWidgetMaintenanceLoopAsync(CancellationToken token)
    {
        MaintainWidgetSurface();

        using var timer = new PeriodicTimer(WidgetMaintenanceInterval);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            MaintainWidgetSurface();
        }
    }

    private async Task FlushSignalsAsync(CancellationToken token)
    {
        DateTimeOffset windowEndUtc = DateTimeOffset.UtcNow;
        DateTimeOffset windowStartUtc = _windowStartUtc;
        if (windowEndUtc <= windowStartUtc)
        {
            return;
        }

        SampleAmbientSignals(windowEndUtc);
        // 2d: refresh the server category overlay (cheap no-op unless the file changed) so newly
        // fleet-categorized tokens start counting toward the right category. Fills Unknowns only.
        AppCategoryOverlayStore.RefreshIfChanged();
        // Persist the coarse hash → label map so the service/core can name apps/sites in the
        // digest and console. Best-effort, coarse labels only; never touches capture.
        FocusAppLabelStore.Persist();
        UserSessionSignalSnapshot snapshot = _signalAggregator.SnapshotAndReset();
        string sessionId = BuildSessionId(_sessionId);
        double windowSeconds = Math.Max(0.001d, (windowEndUtc - windowStartUtc).TotalSeconds);
        string? fallbackFocusAppHash = UserSessionNativeSignals.TryGetForegroundAppHash();
        double fallbackIdleSeconds = UserSessionNativeSignals.ReadIdleDurationSeconds();
        UserSessionKeyboardTelemetrySummary keyboard = snapshot.BuildKeyboardSummary(windowSeconds);
        UserSessionMouseTelemetrySummary mouse = snapshot.BuildMouseSummary(windowSeconds);
        UserSessionFocusTelemetrySummary focus = snapshot.BuildFocusSummary(windowStartUtc, windowEndUtc, fallbackFocusAppHash);
        UserSessionIdleTelemetrySummary idle = snapshot.BuildIdleSummary(windowStartUtc, windowEndUtc, fallbackIdleSeconds);
        bool? notificationToastsEnabled = ReadNotificationToastsEnabled();

        UserAgentSessionContextSnapshot heartbeatSnapshot = PublishSessionContext();

        // Category bridge (v1.2.112): the service process's classifier registries are empty —
        // categories/labels/meeting flags are only ever learned HERE, in the user session.
        // Ship the metadata for the exact hash this request carries so the service can
        // register it before the bucket pipeline evaluates that hash. Coarse values only.
        string? sentFocusAppHash = focus.DominantAppHash ?? fallbackFocusAppHash;
        Porta.Agent.Common.Analysis.AppCategory sentFocusAppCategory =
            Porta.Agent.Common.Analysis.AppCategoryClassifier.GetCategoryForHash(sentFocusAppHash ?? string.Empty);
        bool sentFocusAppIsMeeting =
            Porta.Agent.Common.Analysis.AppCategoryClassifier.IsMeetingApp(sentFocusAppHash);
        string? sentFocusAppLabel =
            Porta.Agent.Common.Analysis.AppCategoryClassifier.GetLabelForHash(sentFocusAppHash);

        var request = new LocalApiInputSignalsRequest(
            SchemaVer: 2,
            AgentInstanceId: _agentInstanceId,
            SessionId: sessionId,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: snapshot.KeyboardKeydownCount,
            KeyboardBackspaceCount: snapshot.KeyboardBackspaceCount,
            MouseWheelDelta: snapshot.MouseWheelDelta,
            MouseMoveDx: snapshot.MouseMoveDx,
            MouseMoveDy: snapshot.MouseMoveDy,
            FocusAppHash: sentFocusAppHash,
            IdleSeconds: fallbackIdleSeconds,
            LastInputEventUtc: snapshot.LastInputEventUtc,
            KeyboardIkiMeanMs: keyboard.IkiMeanMs,
            KeyboardIkiStdMs: keyboard.IkiStdMs,
            KeyboardIkiCv: keyboard.IkiCv,
            KeyboardBurstCount: keyboard.BurstCount,
            KeyboardBurstRate: keyboard.BurstRate,
            KeyboardCorrectionSpikeCount: keyboard.CorrectionSpikeCount,
            KeyboardCorrectionSpikeRate: keyboard.CorrectionSpikeRate,
            MouseMoveSampleCount: mouse.MoveSampleCount,
            MousePathLengthPx: mouse.PathLengthPx,
            MouseSpeedMeanPxPerSec: mouse.SpeedMeanPxPerSec,
            MouseJerkP95: mouse.JerkP95,
            MouseMicroCorrectionCount: mouse.MicroCorrectionCount,
            MouseMicroCorrectionRate: mouse.MicroCorrectionRate,
            MouseWheelEventCount: mouse.WheelEventCount,
            MouseScrollBurstCount: mouse.ScrollBurstCount,
            MouseScrollBurstRate: mouse.ScrollBurstRate,
            MouseScrollSpeedMean: mouse.ScrollSpeedMean,
            FocusObservationCount: focus.ObservationCount,
            FocusSwitchCount: focus.SwitchCount,
            FocusActiveAppCount: focus.ActiveAppCount,
            FocusAppEntropy: focus.AppEntropy,
            IdleObservationCount: idle.ObservationCount,
            IdleSecondsWithinWindow: idle.IdleSecondsWithinWindow,
            MicroIdleCount: idle.MicroIdleCount,
            MicroIdleSeconds: idle.MicroIdleSeconds,
            BreakSecondsWithinWindow: idle.BreakSecondsWithinWindow,
            ShortIdleBoundaryObserved: idle.ShortIdleBoundaryObserved,
            NotificationToastsEnabled: notificationToastsEnabled,
            HeartbeatSequence: heartbeatSnapshot.HeartbeatSequence,
            HeartbeatUpdatedAtUtc: heartbeatSnapshot.UpdatedAtUtc,
            HeartbeatCaptureContext: heartbeatSnapshot.CaptureContext,
            FocusAppCategory: (int)sentFocusAppCategory,
            FocusAppIsMeeting: sentFocusAppIsMeeting,
            FocusAppLabel: sentFocusAppLabel);

        if (!_localApiClient.IsConfigured)
        {
            _signalAggregator.Requeue(snapshot);
            if (!_localApiUnavailableLogged)
            {
                _localApiUnavailableLogged = true;
                _logger.LogWarning("Porta.UserAgent could not find local API settings yet. Waiting for the core service.");
            }

            return;
        }

        LocalApiAckResponse? ack = await _localApiClient.SendInputSignalsAsync(request, token).ConfigureAwait(false);
        if (ack?.Success == true)
        {
            _windowStartUtc = windowEndUtc;
            _localApiUnavailableLogged = false;
            _captureSelfTest.RecordPublishAccepted(windowEndUtc);
            _consecutiveSignalDeliveryFailures = 0;
            if (_captureFailureCode == "signals_not_delivering")
            {
                _captureFailureCode = null;
            }

            return;
        }

        _signalAggregator.Requeue(snapshot);
        _consecutiveSignalDeliveryFailures++;
        if (_consecutiveSignalDeliveryFailures >= 5)
        {
            _captureFailureCode = "signals_not_delivering";
        }

        if (_consecutiveSignalDeliveryFailures >= 10)
        {
            UserAgentFileLog.Info(
                $"signals_not_delivering_exit pid={Environment.ProcessId} session={_sessionId} consecutive_failures={_consecutiveSignalDeliveryFailures}");
            _shouldExit = true;
        }

        _logger.LogWarning(
            "Porta.UserAgent failed to publish input signals for window {WindowStartUtc}..{WindowEndUtc}. Ack={AckMessage}.",
            windowStartUtc,
            windowEndUtc,
            ack?.Message ?? "null");
    }

    private async Task PollPendingInterventionsAsync(CancellationToken token)
    {
        if (!_localApiClient.IsConfigured)
        {
            return;
        }

        await FlushCompletedInterventionAcksAsync(token).ConfigureAwait(false);

        string sessionId = BuildSessionId(_sessionId);
        IReadOnlyList<LocalApiInterventionPendingResponse> pending =
            await _localApiClient.GetPendingInterventionsAsync(sessionId, limit: 12, token).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            await FlushCompletedInterventionAcksAsync(token).ConfigureAwait(false);
            return;
        }

        foreach (LocalApiInterventionPendingResponse intervention in pending)
        {
            await ExecuteInterventionAsync(intervention, token).ConfigureAwait(false);
        }

        await FlushCompletedInterventionAcksAsync(token).ConfigureAwait(false);
    }

    private async Task ExecuteInterventionAsync(LocalApiInterventionPendingResponse intervention, CancellationToken token)
    {
        LocalApiInterventionAckRequest? ack = _interventionExecutor.ExecuteOrReplay(
            intervention,
            BuildSessionId(_sessionId),
            _agentInstanceId);
        if (ack != null)
        {
            await SendInterventionAckAsync(ack, token).ConfigureAwait(false);
        }
    }

    private async Task FlushCompletedInterventionAcksAsync(CancellationToken token)
    {
        while (_interventionExecutor.TryDequeueCompletedAck(out LocalApiInterventionAckRequest ack))
        {
            await SendInterventionAckAsync(ack, token).ConfigureAwait(false);
        }
    }

    private async Task SendInterventionAckAsync(LocalApiInterventionAckRequest ack, CancellationToken token)
    {
        LocalApiAckResponse? response = await _localApiClient.AcknowledgeInterventionAsync(ack, token).ConfigureAwait(false);
        if (response?.Success == true)
        {
            return;
        }

        _logger.LogWarning(
            "Failed to acknowledge intervention {InterventionId}. Result={Result} Response={ResponseMessage}",
            ack.InterventionId,
            ack.Result,
            response?.Message ?? "null");
    }

    private string? ResolveHeartbeatCaptureFailureCode(DateTimeOffset nowUtc)
    {
        // Runs on the heartbeat loop thread. If the capture loop has stopped ticking
        // or keeps throwing, the heartbeat must say so instead of looking healthy.
        if (_captureContext == UserAgentCaptureContexts.UserSession)
        {
            long lastTickTicks = Interlocked.Read(ref _lastCaptureLoopTickUtcTicks);
            if (lastTickTicks > 0 && (nowUtc.UtcTicks - lastTickTicks) > TimeSpan.FromSeconds(60).Ticks)
            {
                return "capture_loop_stalled";
            }

            if (Interlocked.Read(ref _captureLoopFailureStreak) >= 5)
            {
                return "capture_loop_erroring";
            }
        }

        return _captureFailureCode;
    }

    private UserAgentSessionContextSnapshot PublishSessionContext()
    {
        bool widgetHealthyForHeartbeat = true;
        int heartbeatWriteFailureCount = _heartbeatWriteFailureCount;
        CaptureSelfTestHeartbeatProjection selfTest = _captureSelfTest.GetHeartbeatProjection();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        // Fleet §1a — periodic USER/GDI handle counts. A monotonic climb here is the widget
        // handle leak that killed capture on PEACH20/AYMENE (~10k per-process ceiling); a flat
        // line is the proof of the fix. One log line every 5 minutes, piggybacked on the
        // heartbeat so it costs nothing extra.
        if ((nowUtc - _lastGuiResourceLogUtc) >= GuiResourceLogInterval)
        {
            _lastGuiResourceLogUtc = nowUtc;
            UserAgentFileLog.Info($"gui_resources {Interventions.GuiResourceMeter.Describe()} pid={Environment.ProcessId}");
        }
        long heartbeatSequence = Interlocked.Increment(ref _heartbeatSequence);
        string? effectiveCaptureFailureCode = ResolveHeartbeatCaptureFailureCode(nowUtc);
        UserAgentSessionContextSnapshot snapshot = new(
            SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
            CaptureContext: _captureContext,
            ProcessId: Environment.ProcessId,
            SessionId: _sessionId,
            UpdatedAtUtc: nowUtc,
            AgentInstanceId: _agentInstanceId,
            BinaryVersion: _binaryVersion,
            StartupSource: _startupMetadata.Source,
            LaunchReason: _startupMetadata.EffectiveLaunchReason,
            HeartbeatSequence: heartbeatSequence,
            HeartbeatWriteFailureCount: heartbeatWriteFailureCount,
            DesktopWidgetReady: widgetHealthyForHeartbeat,
            CaptureSelfTestState: selfTest.State,
            CaptureSelfTestErrorCode: selfTest.ErrorCode,
            CaptureSelfTestRemediationCount: selfTest.RemediationCount,
            CaptureFailureCode: effectiveCaptureFailureCode,
            LabelPersistFailureCount: Input.FocusAppLabelStore.ReadPersistFailureCount(),
            LabelPersistLastError: Input.FocusAppLabelStore.ReadLastPersistErrorType());

        try
        {
            try
            {
                widgetHealthyForHeartbeat = _toastRenderer.IsWidgetHealthyForSessionHeartbeat;
            }
            catch (Exception ex)
            {
                widgetHealthyForHeartbeat = false;
                UserAgentFileLog.Error("widget_heartbeat_state_read_failed", ex);
                _logger.LogError(ex, "Failed to read desktop widget heartbeat state.");
            }

            snapshot = snapshot with { DesktopWidgetReady = widgetHealthyForHeartbeat };
            _lastPublishedSessionContext = snapshot;
            _sessionContextStore.WriteCurrent(snapshot);

            _captureSelfTest.RecordHeartbeatPublished(snapshot.UpdatedAtUtc);

            if (!_heartbeatReadyLogged)
            {
                _heartbeatReadyLogged = true;
                UserAgentFileLog.Info(
                    $"heartbeat_publishing_ready source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} path={ResolveSessionContextPath()}");
            }

            if (heartbeatWriteFailureCount > 0)
            {
                UserAgentFileLog.Info(
                    $"heartbeat_write_recovered source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} path={ResolveSessionContextPath()} failures={heartbeatWriteFailureCount}");
                _heartbeatWriteFailureCount = 0;
            }
        }
        catch (Exception ex)
        {
            _heartbeatWriteFailureCount++;
            _lastPublishedSessionContext = snapshot;
            if (ShouldLogHeartbeatWriteFailure(_heartbeatWriteFailureCount))
            {
                UserAgentFileLog.Error(
                    $"heartbeat_write_failed source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} path={ResolveSessionContextPath()} failure_count={_heartbeatWriteFailureCount}",
                    ex);
            }
            _logger.LogError(ex, "Failed to publish user-session capture context heartbeat.");

            if (_heartbeatWriteFailureCount >= 5)
            {
                UserAgentFileLog.Info(
                    $"user_agent_exiting_heartbeat_write_failed pid={Environment.ProcessId} session={_sessionId} consecutive_failures={_heartbeatWriteFailureCount}");
                _shouldExit = true;
            }
        }

        return snapshot;
    }

    private void MaintainWidgetSurface()
    {
        try
        {
            _toastRenderer.MaintainWidgetSurface();
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("widget_maintenance_failed", ex);
            _logger.LogError(ex, "Failed to maintain desktop widget surface.");
        }
    }

    private void StartCaptureHooks()
    {
        bool keyboardHookInstalled = _keyboardHook.Install();
        if (!keyboardHookInstalled)
        {
            _logger.LogWarning("Keyboard hook did not install.");
        }

        // Always start the poller alongside the hook. On Windows, WH_KEYBOARD_LL
        // hooks are silently bypassed when a high-integrity (elevated) app is in the
        // foreground, so the hook alone cannot guarantee coverage. The poller uses
        // GetAsyncKeyState, which reads raw hardware state regardless of foreground
        // integrity level. Duplicate events are suppressed in the KeyChanged handler
        // when the hook is also active (30 ms dedup window).
        bool pollerInstalled = _keyboardPoller.Install();
        bool keyboardInstalled = keyboardHookInstalled || pollerInstalled;

        bool mouseInstalled = _mouseHook.Install();
        _keyboardCaptureAvailable = keyboardInstalled;
        _mouseCaptureAvailable = mouseInstalled;
        _hooksInstalled = keyboardInstalled || mouseInstalled;

        if (!keyboardInstalled)
        {
            _logger.LogWarning("Keyboard capture is unavailable (hook and poller both failed). Keyboard capture will stay inactive.");
        }

        if (!mouseInstalled)
        {
            _logger.LogWarning("Mouse hook did not install. Mouse capture will stay inactive.");
        }

        if (_hooksInstalled)
        {
            _logger.LogInformation(
                "User-session input capture ready. keyboard={KeyboardInstalled} mouse={MouseInstalled}",
                keyboardInstalled,
                mouseInstalled);
            UserAgentFileLog.Info(
                $"capture_hooks_ready source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} keyboard={keyboardInstalled} mouse={mouseInstalled}");
            if (_captureFailureCode == "hooks_unavailable")
            {
                _captureFailureCode = null;
            }
        }
        else
        {
            _captureFailureCode = "hooks_unavailable";
            UserAgentFileLog.Info(
                $"capture_hooks_unavailable source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} pid={Environment.ProcessId} session={_sessionId} keyboard={keyboardInstalled} mouse={mouseInstalled}");
        }
    }

    private void StopCaptureHooks()
    {
        if (!_hooksInstalled)
        {
            _keyboardCaptureAvailable = false;
            _mouseCaptureAvailable = false;
            return;
        }

        _keyboardHook.Uninstall();
        _keyboardPoller.Uninstall();
        _mouseHook.Uninstall();
        _hooksInstalled = false;
        _keyboardCaptureAvailable = false;
        _mouseCaptureAvailable = false;
    }

    private void OnKeyboardChanged(bool isKeyDown, bool isBackspace, bool isInjected)
    {
        if (!isKeyDown || isInjected)
        {
            return;
        }

        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.RecordRawInput(observedAtUtc);
        _signalAggregator.RecordKeyboard(isBackspace, observedAtUtc);
        RecordLiveInput(observedAtUtc, keyboard: true, mouse: false);
    }

    private void OnMouseMoved(int dx, int dy)
    {
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.RecordRawInput(observedAtUtc);
        _signalAggregator.RecordMouseMove(dx, dy, observedAtUtc);
        RecordLiveInput(observedAtUtc, keyboard: false, mouse: true);
    }

    private void OnMouseWheelChanged(int delta, bool _)
    {
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.RecordRawInput(observedAtUtc);
        _signalAggregator.RecordMouseWheel(delta, observedAtUtc);
        RecordLiveInput(observedAtUtc, keyboard: false, mouse: true);
    }

    private void SampleAmbientSignals(DateTimeOffset sampleUtc)
    {
        string? focusAppHash = UserSessionNativeSignals.TryGetForegroundAppHash();
        double idleSeconds = UserSessionNativeSignals.ReadIdleDurationSeconds();
        _signalAggregator.RecordFocusObservation(focusAppHash, sampleUtc);
        _signalAggregator.RecordIdleObservation(idleSeconds, sampleUtc);
        lock (_liveSignalGate)
        {
            string? previousFocusAppHash = _lastLiveFocusAppHash;
            _lastLiveFocusAppHash = focusAppHash;
            _lastLiveIdleSeconds = idleSeconds;
            UpdateLiveFocusTimerLocked(sampleUtc, focusAppHash, idleSeconds, previousFocusAppHash);
        }
    }

    private void RecordLiveInput(DateTimeOffset observedAtUtc, bool keyboard, bool mouse)
    {
        lock (_liveSignalGate)
        {
            _lastLiveInputUtc = observedAtUtc;
            if (keyboard)
            {
                _lastLiveKeyboardUtc = observedAtUtc;
            }

            if (mouse)
            {
                _lastLiveMouseUtc = observedAtUtc;
            }

            if (!string.IsNullOrWhiteSpace(_lastLiveFocusAppHash)
                && _lastLiveIdleSeconds < LiveFocusIdleResetThreshold.TotalSeconds
                && !_liveFocusStartedUtc.HasValue)
            {
                _liveFocusStartedUtc = observedAtUtc;
            }
        }
    }

    private async Task PublishLiveSessionStateIfDueAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        if (_captureContext != UserAgentCaptureContexts.UserSession)
        {
            return;
        }

        if ((nowUtc - _lastLiveSignalPublishUtc) < LiveSignalPublishInterval)
        {
            return;
        }

        _lastLiveSignalPublishUtc = nowUtc;
        if (!_localApiClient.IsConfigured)
        {
            return;
        }

        LiveSessionSignalSnapshot snapshot = CaptureLiveSessionSignalSnapshot(nowUtc);
        UserAgentSessionContextSnapshot heartbeatSnapshot = _lastPublishedSessionContext ?? PublishSessionContext();
        LocalApiAckResponse? ack = await _localApiClient.SendLiveSessionStateAsync(
            new LocalApiLiveSessionStateRequest(
                SchemaVer: 1,
                AgentInstanceId: _agentInstanceId,
                SessionId: BuildSessionId(_sessionId),
                UpdatedAtUtc: nowUtc,
                SessionStartedAtUtc: _sessionStartedUtc,
                LastInputEventUtc: snapshot.LastInputEventUtc,
                KeyboardActive: snapshot.KeyboardActive,
                MouseActive: snapshot.MouseActive,
                IdleSeconds: snapshot.IdleSeconds,
                FocusAppHash: snapshot.FocusAppHash,
                FocusStartedAtUtc: snapshot.FocusStartedAtUtc,
                HeartbeatSequence: heartbeatSnapshot.HeartbeatSequence,
                HeartbeatUpdatedAtUtc: heartbeatSnapshot.UpdatedAtUtc,
                HeartbeatCaptureContext: heartbeatSnapshot.CaptureContext),
            token).ConfigureAwait(false);

        if (ack?.Success != true)
        {
            _logger.LogDebug(
                "Failed to publish live session state. Response={ResponseMessage}",
                ack?.Message ?? "null");
        }
    }

    private LiveSessionSignalSnapshot CaptureLiveSessionSignalSnapshot(DateTimeOffset nowUtc)
    {
        lock (_liveSignalGate)
        {
            bool keyboardActive = _lastLiveKeyboardUtc.HasValue
                && (nowUtc - _lastLiveKeyboardUtc.Value) <= LiveSignalActiveWindow;
            bool mouseActive = _lastLiveMouseUtc.HasValue
                && (nowUtc - _lastLiveMouseUtc.Value) <= LiveSignalActiveWindow;

            return new LiveSessionSignalSnapshot(
                LastInputEventUtc: _lastLiveInputUtc,
                KeyboardActive: keyboardActive,
                MouseActive: mouseActive,
                IdleSeconds: _lastLiveIdleSeconds,
                FocusAppHash: _lastLiveFocusAppHash,
                FocusStartedAtUtc: _liveFocusStartedUtc);
        }
    }

    private void UpdateLiveFocusTimerLocked(
        DateTimeOffset observedAtUtc,
        string? currentFocusAppHash,
        double idleSeconds,
        string? previousFocusAppHash)
    {
        bool focusEligible = idleSeconds < LiveFocusIdleResetThreshold.TotalSeconds
            && !string.IsNullOrWhiteSpace(currentFocusAppHash);

        if (!focusEligible)
        {
            _liveFocusStartedUtc = null;
            return;
        }

        if (!string.Equals(previousFocusAppHash, currentFocusAppHash, StringComparison.Ordinal))
        {
            _liveFocusStartedUtc = observedAtUtc;
            return;
        }

        _liveFocusStartedUtc ??= observedAtUtc;
    }

    private bool? ReadNotificationToastsEnabled()
    {
        NotificationControllerSnapshotResult snapshotResult = _notificationController.GetSnapshot();
        return snapshotResult.Succeeded
            ? snapshotResult.Snapshot.ToastBannersEnabled
            : null;
    }

    private async Task EvaluateCaptureSelfTestAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        double idleSeconds = UserSessionNativeSignals.ReadIdleDurationSeconds();
        CaptureSelfTestDecision decision = _captureSelfTest.Evaluate(
            nowUtc,
            heartbeatFresh: _heartbeatWriteFailureCount == 0,
            hooksAvailable: _keyboardCaptureAvailable || _mouseCaptureAvailable,
            idleSeconds);

        if (decision.LogMessage is not null)
        {
            _logger.LogInformation("{Message}", decision.LogMessage);
            UserAgentFileLog.Info(decision.LogMessage);
        }

        if (decision.RequestHookReset)
        {
            StopCaptureHooks();
            StartCaptureHooks();
        }

        if (decision.RequestImmediateFlush)
        {
            await FlushSignalsAsync(token).ConfigureAwait(false);
        }

        if (decision.NotifyUserFailure)
        {
            ShowCaptureSelfTestFailureToast(decision);
        }

        if (decision.State == CaptureSelfTestStates.Failed)
        {
            _captureFailureCode = "self_test_failed";
            _selfTestFailedSinceUtc ??= nowUtc;
            double selfTestFailedForSeconds = (nowUtc - _selfTestFailedSinceUtc.Value).TotalSeconds;
            if (selfTestFailedForSeconds > 60d)
            {
                UserAgentFileLog.Info(
                    $"user_agent_exiting_self_test_failed pid={Environment.ProcessId} session={_sessionId} failed_for_seconds={selfTestFailedForSeconds:F0}");
                _shouldExit = true;
            }
        }
        else if (decision.State == CaptureSelfTestStates.Healthy)
        {
            _selfTestFailedSinceUtc = null;
            if (_captureFailureCode == "self_test_failed")
            {
                _captureFailureCode = null;
            }
        }
    }

    private void ArmCaptureSelfTest(string triggerReason)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        _captureSelfTest.Arm(triggerReason, nowUtc);
        string reason = string.IsNullOrWhiteSpace(triggerReason) ? "unspecified" : triggerReason.Trim();
        UserAgentFileLog.Info(
            $"capture_self_test_armed source={_startupMetadata.Source} launch_reason={_startupMetadata.EffectiveLaunchReason} trigger={reason} pid={Environment.ProcessId} session={_sessionId}");
    }

    private void SubscribeToSessionSwitchNotifications()
    {
        if (_systemEventsSubscribed)
        {
            return;
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        _systemEventsSubscribed = true;
    }

    private void UnsubscribeFromSessionSwitchNotifications()
    {
        if (!_systemEventsSubscribed)
        {
            return;
        }

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch
        {
        }

        _systemEventsSubscribed = false;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not SessionSwitchReason.SessionLogon
            and not SessionSwitchReason.SessionUnlock
            and not SessionSwitchReason.ConsoleConnect
            and not SessionSwitchReason.RemoteConnect)
        {
            return;
        }

        ArmCaptureSelfTest(e.Reason.ToString());

        // Workstream 3: "closed the laptop yesterday, opened it today" — re-ask widget
        // visibility immediately on unlock instead of waiting for the next maintenance
        // tick. Same-day unlocks are a no-op (the store still reports hidden).
        _toastRenderer.ReevaluateVisibility();
    }

    private void ShowCaptureSelfTestFailureToast(CaptureSelfTestDecision decision)
    {
        if (!UserSessionNativeSignals.IsInterventionPresentationAvailable(out _))
        {
            return;
        }

        string message = decision.FailureCode switch
        {
            "heartbeat_not_fresh"
                => "Ari is connected, but the live session heartbeat is not updating yet. Ari will keep trying in the background. If this stays stuck, sign out and back in.",
            "hooks_unavailable"
                => "Ari could not attach live keyboard or mouse capture in this session. Sign out and back in if live activity stays stuck at zero.",
            "input_publish_not_confirmed"
                => "Ari saw live input locally, but the core did not confirm it yet. If activity stays stuck at zero, sign out and back in.",
            _
                => "Ari is not receiving live keyboard or mouse activity yet. If activity stays stuck at zero, sign out and back in."
        };

        // B1 (LOCKED classification): capture self-test failure is a degraded-state signal,
        // NOT an interrupt. It surfaces on the widget (the capture-degraded state is already
        // fed into the widget state) and the Health panel; here we only record it to the
        // activity log. It must never become a popup toast.
        UserAgentFileLog.Info($"activity_capture_self_test_warning code={decision.FailureCode} message={message}");
    }

    private void UpdateTrayState(UserInterventionToastRenderer.UserTrayVisualState state)
    {
        if (_lastTrayState.HasValue && _lastTrayState.Value.Equals(state))
        {
            return;
        }

        if (_toastRenderer.TryUpdateTrayState(state, out string errorCode))
        {
            _lastTrayState = state;
            return;
        }

        _logger.LogDebug("Failed to update PORTA tray state. Error={ErrorCode}", errorCode);
    }

    // Phase 1b — Step 3c: poll for a due ground-truth prompt at natural boundaries. The core
    // governor owns caps/cooldown/informative-band/meeting gating; the widget holds one prompt at a
    // time and only asks for a new one once the current is resolved (answered/dismissed/ignored).
    private async Task RefreshLabelPromptAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        if (_activeLabelPrompt is not null)
        {
            // Backstop: stop presenting a prompt after its display window so polling can resume.
            // The widget locally collapses the pill the moment the user answers/dismisses; this just
            // clears the host-held copy (also covers an ignored prompt the user never resolved).
            if ((nowUtc - _labelPromptShownUtc) >= LabelPromptDisplayWindow)
            {
                _activeLabelPrompt = null;
                _lastLabelPromptCheckUtc = nowUtc;
            }

            return;
        }

        if ((nowUtc - _lastLabelPromptCheckUtc) < LabelPromptCheckInterval)
        {
            return;
        }

        _lastLabelPromptCheckUtc = nowUtc;

        // Meeting suppression is decided server-side from the live foreground app (the service
        // owns that truth); this flag is advisory-only.
        LocalApiLabelPromptDueResponse? due = await _localApiClient
            .GetLabelPromptDueAsync(meetingInProgress: false, token)
            .ConfigureAwait(false);
        if (due is { ShouldPrompt: true })
        {
            _activeLabelPrompt = MapLabelPrompt(due);
            _labelPromptShownUtc = nowUtc;
        }
    }

    // Phase 2c — poll the next reminder. Polling IS the reminder clock: the store lazily fires
    // due reminders on each read, so no background timer exists anywhere. Also the escalation
    // fallback: a user-set alarm must reach the user even when the widget surface is hidden or
    // unhealthy, so those cases get a system NotifyIcon balloon (once per reminder id).
    private async Task RefreshReminderAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        if ((nowUtc - _lastReminderCheckUtc) < ReminderCheckInterval)
        {
            return;
        }

        _lastReminderCheckUtc = nowUtc;
        LocalApiNextReminderResponse? response = await _localApiClient
            .GetNextReminderAsync(token)
            .ConfigureAwait(false);
        if (response is null)
        {
            return;
        }

        _nextReminder = response.Next;
        if (_nextReminder is { State: "fired" } fired
            && fired.Id != _lastSystemNotifiedReminderId
            && (_toastRenderer.IsWidgetHiddenByUser || !_toastRenderer.IsWidgetHealthyForSessionHeartbeat))
        {
            _lastSystemNotifiedReminderId = fired.Id;
            _ = _toastRenderer.TryShowSystemNotification("Reminder", fired.Title);
        }
    }

    private UserDesktopWidgetReminderInfo? MapNextReminder()
    {
        return _nextReminder is null
            ? null
            : new UserDesktopWidgetReminderInfo(
                Id: _nextReminder.Id,
                Title: _nextReminder.Title,
                DueAtUtc: _nextReminder.DueAtUtc,
                State: _nextReminder.State,
                SnoozeCount: _nextReminder.SnoozeCount);
    }

    // Workstream 4 — poll today's tasks and surface the planned-time expiry ONCE per
    // (task id, planned+extended budget). Respects the meeting shield: while the snapshot
    // says in_meeting the toast is deferred (not consumed), so it lands after the call.
    private async Task RefreshTasksAsync(
        DateTimeOffset nowUtc,
        LocalApiCurrentSnapshotResponse? snapshot,
        CancellationToken token)
    {
        if ((nowUtc - _lastTasksCheckUtc) < TasksCheckInterval)
        {
            return;
        }

        _lastTasksCheckUtc = nowUtc;
        LocalApiTasksTodayResponse? response = await _localApiClient
            .GetTasksTodayAsync(token)
            .ConfigureAwait(false);
        if (response is null)
        {
            return;
        }

        _todayTasks = response.Tasks ?? Array.Empty<LocalApiTask>();

        foreach (LocalApiTask task in _todayTasks)
        {
            int budgetSeconds = task.PlannedSeconds + task.ExtendedSeconds;
            if (!string.Equals(task.State, "active", StringComparison.Ordinal)
                || task.ActiveSeconds < budgetSeconds)
            {
                continue;
            }

            string key = $"{task.Id}:{budgetSeconds}";
            if (_taskExpiryToastedKeys.Contains(key) || IsMeetingForeground(snapshot))
            {
                continue;
            }

            _taskExpiryToastedKeys.Add(key);
            // B1: task-expiry is an interrupt — route through the anti-spam gate like every
            // other interrupt. The per-(id,budget) key above already enforces once-per-expiry
            // and once-per-extension (budget includes extended_seconds).
            _ = _interventionExecutor.TryShowGatedInterruptToast(
                "task:expiry",
                "Ari",
                $"Time's up on \"{task.Title}\" — mark it done, add 15 minutes, or switch, from the widget.",
                nowUtc,
                out _);
            UserAgentFileLog.Info($"task_expiry_notified id={task.Id} budget_s={budgetSeconds}");
        }
    }

    private static bool IsMeetingForeground(LocalApiCurrentSnapshotResponse? snapshot)
    {
        try
        {
            string? flags = snapshot?.StateFlagsJson;
            if (string.IsNullOrWhiteSpace(flags))
            {
                return false;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(flags);
            return doc.RootElement.TryGetProperty("attention_segment_kind", out System.Text.Json.JsonElement kind)
                && kind.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(kind.GetString(), AttentionSegmentKinds.InMeeting, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<UserDesktopWidgetTaskInfo> MapTodayTasks()
    {
        if (_todayTasks.Count == 0)
        {
            return Array.Empty<UserDesktopWidgetTaskInfo>();
        }

        var mapped = new List<UserDesktopWidgetTaskInfo>(_todayTasks.Count);
        foreach (LocalApiTask task in _todayTasks)
        {
            mapped.Add(new UserDesktopWidgetTaskInfo(
                Id: task.Id,
                Title: task.Title,
                PlannedSeconds: task.PlannedSeconds,
                State: task.State,
                ActiveSeconds: task.ActiveSeconds,
                FocusedSeconds: task.FocusedSeconds,
                OfftaskSeconds: task.OfftaskSeconds,
                ExtendedSeconds: task.ExtendedSeconds));
        }

        return mapped;
    }

    // Workstream D — conversational check-ins. Reads the day's goal row, asks the pure scheduler
    // which check-in (if any) is due, and — when one is — routes it through the executor's gated
    // check-in door (per-day mute, critical-state suppression, presentation check) to the
    // always-on-top card. The per-day mute is BOTH the "already offered today" gate the scheduler
    // reads and the once-shown lock the door sets, so each check-in fires exactly once per day.
    private async Task RefreshCheckInAsync(
        DateTimeOffset nowUtc,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession,
        CancellationToken token)
    {
        // Part 1c — advance the settle-in tracker every poll (before the 2-min decision gate) so the
        // continuous-active window is measured accurately. fresh+active starts/continues the run; a
        // stale/idle/away signal resets it; an unavailable signal (null) leaves it untouched.
        bool? activeNow = liveSession is { } ls
            ? string.Equals(ls.freshness_state, "fresh", StringComparison.OrdinalIgnoreCase) && ls.UserActive && !ls.Idle
            : null;
        _checkInActiveSinceUtc = UserCheckInActivityGate.Advance(_checkInActiveSinceUtc, nowUtc, activeNow);

        if ((nowUtc - _lastCheckInCheckUtc) < CheckInCheckInterval)
        {
            return;
        }

        _lastCheckInCheckUtc = nowUtc;

        // Cold-start suppression: don't greet the moment the agent launches — let capture settle.
        if ((nowUtc - _sessionStartedUtc) < CheckInColdStartGrace)
        {
            return;
        }

        // Don't seize the screen mid-meeting (the same shield the task-expiry nudge honors).
        if (IsMeetingForeground(snapshot))
        {
            return;
        }

        LocalApiDailyGoalResponse? goalResponse = await _localApiClient
            .GetTodayGoalAsync(token)
            .ConfigureAwait(false);
        // Null = the core isn't reachable yet. Skip rather than risk a spurious morning card.
        if (goalResponse is null)
        {
            return;
        }

        LocalApiDailyGoal? goal = goalResponse.Goal;
        DateTimeOffset nowLocal = nowUtc.ToLocalTime();

        // Part 1a/1b — build the day's metrics up front so the midday trigger is driven by REAL
        // active work (tracked minus away) and the EOD wrap can see the away gap. Fail-soft: when the
        // timeline hasn't processed yet, active-work is null and the scheduler falls back to the old
        // wall-clock rule so midday can never become un-fireable.
        LocalTodaySummaryMetrics metrics = LocalTodaySummaryMetricsBuilder.Build(
            _widgetDetailTimeline, snapshot, liveSession, nowUtc);
        TimeSpan? activeWorkSinceMorning = metrics.HasProcessedTimeline
            ? TimeSpan.FromSeconds(Math.Max(0d, metrics.TrackedTodaySeconds - metrics.AwayTodaySeconds))
            : null;
        TimeSpan awayToday = TimeSpan.FromSeconds(Math.Max(0d, metrics.AwayTodaySeconds));

        DailyGoalSnapshot goalSnapshot = UserCheckInCoordinator.BuildSnapshot(goal, activeWorkSinceMorning, awayToday);

        UserCheckInKind? due = UserCheckInScheduler.DecideCheckIn(
            nowLocal,
            goalSnapshot,
            kind => _interventionExecutor.IsClassMutedForToday(UserCheckInScheduler.MuteClassFor(kind)));
        if (due is not UserCheckInKind kind)
        {
            return;
        }

        // Part 1c — settle-in: never pop the instant the session resumes. Hold any due check-in until
        // the user has been continuously active for SettleInDelay. Fail-soft: an unavailable activity
        // signal does not block (see UserCheckInActivityGate.WithinSettleWindow).
        if (UserCheckInActivityGate.WithinSettleWindow(_checkInActiveSinceUtc, nowUtc, UserCheckInActivityGate.SettleInDelay))
        {
            UserAgentFileLog.Info($"checkin_settle_hold kind={kind}");
            return;
        }

        // Part 5 — the gate above has already decided to SPEAK, so SPEAK NOW. The card renders
        // immediately with the honest template and the LLM line upgrades it in place when it
        // lands (decision locked 2026-08-06).
        //
        // This used to await the narrative before showing anything, against a 10s → 9s → 8s
        // timeout chain. The server-side 8s budget fires first, and roughly seven check-ins in
        // ten returned nothing — so the common case was the user waiting several seconds and
        // then getting the template anyway. Waiting spent the three seconds of willingness the
        // morning card depends on and bought a line that usually never came.
        string narrativeKind = kind switch
        {
            UserCheckInKind.Morning => "morning",
            UserCheckInKind.Midday => "midday",
            _ => "eod"
        };

        // Part 1b — when EOD is the single wrap after a skipped midday / away gap, surface those facts
        // so even the templated fallback copy acknowledges the whole day rather than reading generic.
        (bool middaySkipped, bool awayGap) = kind == UserCheckInKind.EndOfDay
            ? UserCheckInScheduler.DescribeEndOfDay(goalSnapshot)
            : (false, false);

        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            kind, goal, metrics, goalResponse.CarriedPlanTomorrow, narrativeLine: null, middaySkipped, awayGap);

        bool shown = _interventionExecutor.TryShowGatedCheckIn(
            UserCheckInScheduler.MuteClassFor(kind),
            prompt,
            OnCheckInResponse,
            nowUtc,
            out string reason);
        UserAgentFileLog.Info(shown
            ? $"checkin_offered kind={kind}"
            : $"checkin_suppressed kind={kind} reason={reason}");

        if (shown)
        {
            // Fire-and-forget: the card is already up, so nothing downstream waits on this.
            _ = UpgradeCheckInLineAsync(kind, narrativeKind, token);
        }
    }

    /// <summary>
    /// Part 5 (async narrative) — fetches the LLM line after the card is on screen and swaps it in.
    /// Every failure mode is a no-op: no line, a line that arrives after the user answered, or a
    /// card that has since been replaced all leave the honest template exactly where it is.
    /// </summary>
    private async Task UpgradeCheckInLineAsync(UserCheckInKind kind, string narrativeKind, CancellationToken token)
    {
        try
        {
            (string? line, string reason) = await GetNarrativeLineWithReasonAsync(narrativeKind, userText: null, token)
                .ConfigureAwait(false);
            UserAgentFileLog.Info(
                $"checkin_narrative kind={narrativeKind} used={line != null}"
                + (line != null ? string.Empty : $" reason={reason}"));
            if (line is null)
            {
                return;
            }

            bool applied = _interventionExecutor.TryUpgradeCheckInLine(kind, line);
            UserAgentFileLog.Info($"checkin_narrative_upgrade kind={narrativeKind} applied={applied}");
        }
        catch (OperationCanceledException)
        {
            // Shutdown or a cancelled tick — the template is already showing, nothing to report.
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error($"checkin_narrative_upgrade_failed kind={narrativeKind}", ex);
        }
    }

    // Runs on the check-in card's STA thread when the user answers or dismisses. Hand the durable
    // write-back off the UI thread so the card closes instantly and the HTTP round-trip can't block
    // the dispatcher.
    private void OnCheckInResponse(UserCheckInResponse response)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleCheckInResponseAsync(response).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UserAgentFileLog.Error($"checkin_response_failed kind={response.Kind}", ex);
            }
        });
    }

    // Workstream D — persist the reply through the LocalApi client (the core records it and, for
    // morning/midday, writes the Phase-1b ground-truth label) and echo a short confirmation.
    private async Task HandleCheckInResponseAsync(UserCheckInResponse response)
    {
        if (response.Dismissed)
        {
            UserAgentFileLog.Info($"checkin_dismissed kind={response.Kind}");
            return;
        }

        switch (response.Kind)
        {
            case UserCheckInKind.Morning:
            {
                string goal = (response.TextReply ?? string.Empty).Trim();
                if (goal.Length == 0)
                {
                    UserAgentFileLog.Info("checkin_morning_empty_goal");
                    return;
                }

                LocalApiDailyGoalResponse? result = await _localApiClient
                    .SetMorningGoalAsync(new LocalApiGoalMorningRequest(
                        SchemaVer: 1,
                        GoalText: goal,
                        TrackingPref: response.Choice))
                    .ConfigureAwait(false);
                UserAgentFileLog.Info($"checkin_morning_recorded ok={result?.Success == true} pref={response.Choice}");

                // Part 5b — the text box feeds the LLM: Ari's confirmation responds to the goal
                // the user actually typed. Fail-soft to the short honest template.
                string? morningReply = await TryGetNarrativeLineAsync("reply_morning", goal, CancellationToken.None).ConfigureAwait(false);
                _checkInCardPresenter.ShowConfirmation(
                    UserCheckInKind.Morning,
                    morningReply ?? "Locked in. I'll help you protect it today.");
                break;
            }

            case UserCheckInKind.Midday:
            {
                string answer = (response.Choice ?? string.Empty).Trim();
                if (answer.Length == 0)
                {
                    return;
                }

                LocalApiDailyGoalResponse? result = await _localApiClient
                    .RecordMiddayAnswerAsync(new LocalApiGoalMiddayRequest(SchemaVer: 1, Answer: answer))
                    .ConfigureAwait(false);
                UserAgentFileLog.Info($"checkin_midday_recorded ok={result?.Success == true} answer={answer}");

                string? middayReply = await TryGetNarrativeLineAsync("reply_midday", answer, CancellationToken.None).ConfigureAwait(false);
                string followUp = middayReply ?? answer switch
                {
                    "partly" => "Good — want me to keep it front of mind?",
                    "not_yet" => "No stress. One quiet nudge in an hour?",
                    _ => "Nice — keep the momentum."
                };

                // Part 5c — "has power": when the goal isn't landing yet, the card offers ONE
                // action wired to an existing capability — a real reminder in an hour.
                if (answer is "partly" or "not_yet")
                {
                    string goalText = (result?.Goal?.GoalText ?? string.Empty).Trim();
                    string reminderTitle = goalText.Length > 0 ? $"Back to: {goalText}" : "Back to today's goal";
                    _checkInCardPresenter.ShowConfirmation(
                        UserCheckInKind.Midday,
                        followUp,
                        actionLabel: "Remind me in 1h",
                        onAction: () => _ = Task.Run(async () =>
                        {
                            try
                            {
                                LocalApiReminderCreateResponse? reminder = await _localApiClient
                                    .CreateReminderAsync(new LocalApiReminderCreateRequest(
                                        SchemaVer: 1,
                                        Title: reminderTitle,
                                        DueAtUtc: DateTimeOffset.UtcNow.AddHours(1)))
                                    .ConfigureAwait(false);
                                UserAgentFileLog.Info($"checkin_midday_reminder ok={reminder?.Success == true}");
                            }
                            catch (Exception ex)
                            {
                                UserAgentFileLog.Error("checkin_midday_reminder_failed", ex);
                            }
                        }));
                }
                else
                {
                    _checkInCardPresenter.ShowConfirmation(UserCheckInKind.Midday, followUp);
                }

                break;
            }

            case UserCheckInKind.EndOfDay:
            {
                string plan = (response.TextReply ?? string.Empty).Trim();
                LocalApiDailyGoalResponse? result = await _localApiClient
                    .RecordEndOfDayAsync(new LocalApiGoalEndOfDayRequest(SchemaVer: 1, PlanTomorrow: plan))
                    .ConfigureAwait(false);
                UserAgentFileLog.Info($"checkin_eod_recorded ok={result?.Success == true} has_plan={plan.Length > 0}");
                if (plan.Length > 0)
                {
                    string? eodReply = await TryGetNarrativeLineAsync("reply_eod", plan, CancellationToken.None).ConfigureAwait(false);
                    _checkInCardPresenter.ShowConfirmation(
                        UserCheckInKind.EndOfDay,
                        eodReply ?? "Saved for tomorrow. Rest well.");
                }

                break;
            }
        }
    }

    // Part 5 — one narrative line from the local API (which forwards to the control plane where
    // the LLM + key live). Null on ANY failure inside the hard budget; callers fall back to their
    // honest template, so this can delay a line slightly but never lose a moment.
    private async Task<string?> TryGetNarrativeLineAsync(string kind, string? userText, CancellationToken token)
        => (await GetNarrativeLineWithReasonAsync(kind, userText, token).ConfigureAwait(false)).Line;

    /// <summary>
    /// Returns the line and, when there isn't one, the reason from whichever layer gave up. This
    /// is the only place in the chain that writes to a durable log — the agent service has none,
    /// it only writes to Console.Error, which goes nowhere for a Windows service. So every layer
    /// below hands its reason code up to here rather than logging it locally.
    /// </summary>
    private async Task<(string? Line, string Reason)> GetNarrativeLineWithReasonAsync(
        string kind,
        string? userText,
        CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            LocalApiNarrativeResponse? response = await _localApiClient
                .GetNarrativeAsync(new LocalApiNarrativeRequest(SchemaVer: 1, Kind: kind, UserText: userText), cts.Token)
                .ConfigureAwait(false);

            if (response is null)
            {
                // The local API itself did not answer — the service is down, the port moved, or
                // the token is stale. Nothing ever reached the control plane.
                return (null, "local_api_no_response");
            }

            string? line = response.Line?.Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return (line, string.Empty);
            }

            return (null, string.IsNullOrWhiteSpace(response.Reason) ? "empty_line_no_reason" : response.Reason);
        }
        catch (OperationCanceledException)
        {
            return (null, "agent_budget_exceeded:10s");
        }
        catch (Exception ex)
        {
            return (null, $"agent_exception:{ex.GetType().Name}");
        }
    }

    // Phase 1c — poll whether an unlinked device is due for a signup nudge, and hold it for the display
    // window. The core enforces the weekly + lifetime caps, so this just shows what it's told to.
    private async Task RefreshSignupNudgeAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        if (_activeSignupNudge is not null)
        {
            if ((nowUtc - _signupNudgeShownUtc) >= SignupNudgeDisplayWindow)
            {
                _activeSignupNudge = null;
                _lastSignupNudgeCheckUtc = nowUtc;
            }

            return;
        }

        if ((nowUtc - _lastSignupNudgeCheckUtc) < SignupNudgeCheckInterval)
        {
            return;
        }

        _lastSignupNudgeCheckUtc = nowUtc;

        LocalApiSignupNudgeDueResponse? due = await _localApiClient
            .GetSignupNudgeDueAsync(token)
            .ConfigureAwait(false);
        if (due is { ShouldNudge: true })
        {
            _activeSignupNudge = new UserDesktopWidgetSignupNudge(
                NudgeId: Guid.NewGuid().ToString("N"),
                Body: "You've tracked 2 days of focus. Sign in to keep it. Takes 30 seconds.",
                PrimaryLabel: "Sign in",
                SecondaryLabel: "Not now");
            _signupNudgeShownUtc = nowUtc;
        }
    }

    private static UserDesktopWidgetLabelPrompt? MapLabelPrompt(LocalApiLabelPromptDueResponse due)
    {
        string id = Guid.NewGuid().ToString("N");
        return due.LabelTarget switch
        {
            "focus_state" => new UserDesktopWidgetLabelPrompt(
                id, due.LabelTarget, due.PromptKind,
                "Were you focused just now?", "Yes", "yes", "No", "no"),
            "distraction" => new UserDesktopWidgetLabelPrompt(
                id, due.LabelTarget, due.PromptKind,
                "Were you working, or taking a break?", "Working", "working", "Break", "break"),
            "overload" => new UserDesktopWidgetLabelPrompt(
                id, due.LabelTarget, due.PromptKind,
                "Are you feeling overwhelmed?", "Yes", "yes", "No", "no"),
            _ => null,
        };
    }

    private void UpdateWidgetState(UserDesktopWidgetState state)
    {
        UserDesktopWidgetState observedState = _toastRenderer.SupportsWidgetSurface
            ? state
            : state with { CriticalState = null };
        _interventionExecutor.ObserveWidgetState(observedState);

        if (!_toastRenderer.SupportsWidgetSurface)
        {
            _lastWidgetState = null;
            if (_widgetFallbackLogged)
            {
                return;
            }

            _widgetFallbackLogged = true;
            _logger.LogInformation(
                "Desktop widget disabled for rollout. Keeping tray + urgent toast fallback active. Reason={ReasonCode}",
                _toastRenderer.WidgetUnavailableReasonCode);
            UserAgentFileLog.Info($"desktop_widget_rollout_active reason={_toastRenderer.WidgetUnavailableReasonCode}");
            return;
        }

        if (_lastWidgetState != null && _lastWidgetState.Equals(state))
        {
            return;
        }

        if (_toastRenderer.TryUpdateWidgetState(state, out string errorCode))
        {
            _lastWidgetState = state;
            return;
        }

        _logger.LogDebug("Failed to update desktop widget state. Error={ErrorCode}", errorCode);
    }

    private static bool IsInteractiveUserSession(int sessionId)
    {
        return Environment.UserInteractive && sessionId > 0;
    }

    private static DateTimeOffset ResolveWidgetTimelineStartUtc(DateTimeOffset nowUtc)
    {
        return LocalTimelineRangeResolver.ResolveLocalDayStartUtc(nowUtc);
    }

    private async Task<IReadOnlyList<LocalApiEventResponse>> RefreshRecentEventsAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        bool shouldRefresh = _recentEvents.Count == 0
            || (nowUtc - _lastRecentEventsRefreshUtc) >= RecentEventsRefreshInterval;
        if (!shouldRefresh || !_localApiClient.IsConfigured)
        {
            return _recentEvents;
        }

        IReadOnlyList<LocalApiEventResponse> events = await _localApiClient
            .GetRecentEventsAsync(limit: 40, token: token)
            .ConfigureAwait(false);

        if (events.Count > 0 || _recentEvents.Count == 0)
        {
            _recentEvents = events;
        }

        _lastRecentEventsRefreshUtc = nowUtc;
        return _recentEvents;
    }

    private async Task<WidgetDetailSnapshot> RefreshWidgetDetailsAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        IReadOnlyList<LocalApiEventResponse> recentEvents = await RefreshRecentEventsAsync(nowUtc, token).ConfigureAwait(false);
        bool shouldRefresh = _widgetDetailTimeline == null
            || _widgetDetailSettings == null
            || (nowUtc - _lastWidgetDetailRefreshUtc) >= WidgetDetailRefreshInterval;
        if (shouldRefresh && _localApiClient.IsConfigured)
        {
            DateTimeOffset widgetTimelineStartUtc = ResolveWidgetTimelineStartUtc(nowUtc);
            LocalApiTimelineResponse? timeline = await _localApiClient
                .GetTimelineAsync(widgetTimelineStartUtc, nowUtc, bucketSeconds: 60, token)
                .ConfigureAwait(false);
            LocalApiSettingsResponse? settings = await _localApiClient
                .GetEffectiveSettingsAsync(token)
                .ConfigureAwait(false);

            _widgetDetailTimeline = timeline ?? _widgetDetailTimeline;
            _widgetDetailSettings = settings ?? _widgetDetailSettings;
            _lastWidgetDetailRefreshUtc = nowUtc;
        }

        _widgetDetailEvents = recentEvents;

        return new WidgetDetailSnapshot(
            recentEvents,
            _widgetDetailTimeline,
            _widgetDetailSettings);
    }

    private static string BuildAgentInstanceId()
    {
        return $"useragent-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    private static string BuildSessionId(int sessionId)
    {
        return $"session-{Math.Max(0, sessionId)}";
    }

    private static bool ShouldLogHeartbeatWriteFailure(int failureCount)
    {
        return failureCount <= 3 || (failureCount % 30) == 0;
    }

    private string ResolveSessionContextPath()
    {
        return _sessionContextStore is UserAgentSessionContextStore fileStore
            ? fileStore.ContextPath
            : "unknown";
    }

    private static string ResolveBinaryVersion()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(processPath);
                if (!string.IsNullOrWhiteSpace(version.FileVersion))
                {
                    return version.FileVersion.Trim();
                }
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static UserInterventionToastRenderer.UserTrayMode MapTrayMode(UserAgentVisualMode mode)
    {
        return mode switch
        {
            UserAgentVisualMode.DeepWork => UserInterventionToastRenderer.UserTrayMode.DeepWork,
            UserAgentVisualMode.Overload => UserInterventionToastRenderer.UserTrayMode.Overload,
            UserAgentVisualMode.Unavailable => UserInterventionToastRenderer.UserTrayMode.Unavailable,
            _ => UserInterventionToastRenderer.UserTrayMode.Normal
        };
    }

    private sealed record WidgetDetailSnapshot(
        IReadOnlyList<LocalApiEventResponse> RecentEvents,
        LocalApiTimelineResponse? Timeline,
        LocalApiSettingsResponse? Settings)
    {
        public static WidgetDetailSnapshot Empty { get; } = new([], null, null);
    }

    private sealed record LiveSessionSignalSnapshot(
        DateTimeOffset? LastInputEventUtc,
        bool KeyboardActive,
        bool MouseActive,
        double IdleSeconds,
        string? FocusAppHash,
        DateTimeOffset? FocusStartedAtUtc);
}

internal sealed class CaptureSelfTestCoordinator
{
    private static readonly TimeSpan HeartbeatGrace = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HooksReadyGrace = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RawInputGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PublishConfirmationGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FlushTriggerDelay = TimeSpan.FromSeconds(1);
    private const int MaxRemediationAttempts = 2;
    private const double RecentUserInputIdleThresholdSeconds = 2.5d;
    private readonly object _gate = new();
    private DateTimeOffset? _armedAtUtc;
    private DateTimeOffset? _observedUserInputUtc;
    private DateTimeOffset? _lastHeartbeatPublishedUtc;
    private DateTimeOffset? _lastRawInputUtc;
    private DateTimeOffset? _lastPublishAcceptedUtc;
    private double? _lastObservedIdleSeconds;
    private string _state = CaptureSelfTestStates.Healthy;
    private string? _errorCode;
    private string _triggerReason = "startup";
    private int _remediationAttempts;
    private bool _failureNotified;
    private bool _flushRequested;

    public void Arm(string triggerReason, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _armedAtUtc = nowUtc;
            _observedUserInputUtc = null;
            _lastObservedIdleSeconds = null;
            _flushRequested = false;
            _failureNotified = false;
            _state = CaptureSelfTestStates.Pending;
            _errorCode = null;
            _triggerReason = string.IsNullOrWhiteSpace(triggerReason) ? "startup" : triggerReason.Trim();
            _remediationAttempts = 0;
        }
    }

    public void RecordHeartbeatPublished(DateTimeOffset publishedAtUtc)
    {
        lock (_gate)
        {
            _lastHeartbeatPublishedUtc = publishedAtUtc;
        }
    }

    public void RecordRawInput(DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            _lastRawInputUtc = observedAtUtc;
        }
    }

    public void RecordPublishAccepted(DateTimeOffset acceptedAtUtc)
    {
        lock (_gate)
        {
            _lastPublishAcceptedUtc = acceptedAtUtc;
        }
    }

    public CaptureSelfTestHeartbeatProjection GetHeartbeatProjection()
    {
        lock (_gate)
        {
            string? state = _state == CaptureSelfTestStates.Healthy ? null : _state;
            string? errorCode = _state == CaptureSelfTestStates.Failed ? _errorCode : null;
            int? remediationCount = _remediationAttempts > 0 ? _remediationAttempts : null;
            return new CaptureSelfTestHeartbeatProjection(state, errorCode, remediationCount);
        }
    }

    public CaptureSelfTestDecision Evaluate(
        DateTimeOffset nowUtc,
        bool heartbeatFresh,
        bool hooksAvailable,
        double idleSeconds)
    {
        lock (_gate)
        {
            if (!_armedAtUtc.HasValue)
            {
                return CaptureSelfTestDecision.None;
            }

            ObserveUserInput(nowUtc, idleSeconds);

            if (HasPublishConfirmationSinceObservedInput())
            {
                return SetHealthy("capture_self_test_passed");
            }

            if (!heartbeatFresh || !IsHeartbeatFresh(nowUtc))
            {
                if ((nowUtc - _armedAtUtc.Value) >= HeartbeatGrace)
                {
                    return SetFailed("heartbeat_not_fresh", "capture_self_test_failed reason=heartbeat_not_fresh");
                }

                return SetPending();
            }

            if (!hooksAvailable)
            {
                if ((nowUtc - _armedAtUtc.Value) >= HooksReadyGrace)
                {
                    return HandleRecoverableFailure(nowUtc, "hooks_unavailable");
                }

                return SetPending();
            }

            if (!_observedUserInputUtc.HasValue)
            {
                return SetPending();
            }

            if (!HasRawInputSinceObservedUserInput())
            {
                if ((nowUtc - _observedUserInputUtc.Value) >= RawInputGrace)
                {
                    return HandleRecoverableFailure(nowUtc, "no_live_input_after_user_activity");
                }

                return SetPending();
            }

            if (!_flushRequested
                && _lastRawInputUtc.HasValue
                && (nowUtc - _lastRawInputUtc.Value) >= FlushTriggerDelay)
            {
                _flushRequested = true;
                return new CaptureSelfTestDecision(
                    State: _state,
                    FailureCode: _errorCode,
                    RequestHookReset: false,
                    RequestImmediateFlush: true,
                    NotifyUserFailure: false,
                    LogMessage: "capture_self_test_force_flush_requested");
            }

            if ((nowUtc - _observedUserInputUtc.Value) >= PublishConfirmationGrace)
            {
                return HandleRecoverableFailure(nowUtc, "input_publish_not_confirmed");
            }

            return SetPending();
        }
    }

    private void ObserveUserInput(DateTimeOffset nowUtc, double idleSeconds)
    {
        bool recentInput = idleSeconds <= RecentUserInputIdleThresholdSeconds;
        bool idleReset = _lastObservedIdleSeconds.HasValue
            && idleSeconds + 0.5d < _lastObservedIdleSeconds.Value;
        _lastObservedIdleSeconds = idleSeconds;

        if ((recentInput || idleReset) && !_observedUserInputUtc.HasValue)
        {
            _observedUserInputUtc = nowUtc;
        }
    }

    private bool HasRawInputSinceObservedUserInput()
    {
        return _observedUserInputUtc.HasValue
            && _lastRawInputUtc.HasValue
            && _lastRawInputUtc.Value >= _observedUserInputUtc.Value.AddSeconds(-1);
    }

    private bool HasPublishConfirmationSinceObservedInput()
    {
        return _observedUserInputUtc.HasValue
            && _lastPublishAcceptedUtc.HasValue
            && _lastPublishAcceptedUtc.Value >= _observedUserInputUtc.Value.AddSeconds(-1);
    }

    private bool IsHeartbeatFresh(DateTimeOffset nowUtc)
    {
        return _lastHeartbeatPublishedUtc.HasValue
            && (nowUtc - _lastHeartbeatPublishedUtc.Value) <= HeartbeatGrace;
    }

    private CaptureSelfTestDecision HandleRecoverableFailure(DateTimeOffset nowUtc, string errorCode)
    {
        if (_remediationAttempts < MaxRemediationAttempts)
        {
            _remediationAttempts++;
            _state = CaptureSelfTestStates.Recovering;
            _errorCode = errorCode;
            _armedAtUtc = nowUtc;
            _observedUserInputUtc = null;
            _lastObservedIdleSeconds = null;
            _flushRequested = false;
            _failureNotified = false;
            return new CaptureSelfTestDecision(
                State: _state,
                FailureCode: _errorCode,
                RequestHookReset: true,
                RequestImmediateFlush: false,
                NotifyUserFailure: false,
                LogMessage: $"capture_self_test_recovery_requested trigger={_triggerReason} error={errorCode} attempt={_remediationAttempts}");
        }

        return SetFailed(errorCode, $"capture_self_test_failed trigger={_triggerReason} error={errorCode} attempts={_remediationAttempts}");
    }

    private CaptureSelfTestDecision SetHealthy(string logMessage)
    {
        bool changed = _state != CaptureSelfTestStates.Healthy || _errorCode is not null || _armedAtUtc.HasValue;
        _state = CaptureSelfTestStates.Healthy;
        _errorCode = null;
        _armedAtUtc = null;
        _observedUserInputUtc = null;
        _lastObservedIdleSeconds = null;
        _flushRequested = false;
        _remediationAttempts = 0;
        _failureNotified = false;
        return changed
            ? new CaptureSelfTestDecision(
                State: _state,
                FailureCode: null,
                RequestHookReset: false,
                RequestImmediateFlush: false,
                NotifyUserFailure: false,
                LogMessage: logMessage)
            : CaptureSelfTestDecision.None;
    }

    private CaptureSelfTestDecision SetPending()
    {
        _state = CaptureSelfTestStates.Pending;
        _errorCode = null;
        return CaptureSelfTestDecision.None;
    }

    private CaptureSelfTestDecision SetFailed(string errorCode, string logMessage)
    {
        bool notify = !_failureNotified;
        _state = CaptureSelfTestStates.Failed;
        _errorCode = errorCode;
        _failureNotified = true;
        return new CaptureSelfTestDecision(
            State: _state,
            FailureCode: _errorCode,
            RequestHookReset: false,
            RequestImmediateFlush: false,
            NotifyUserFailure: notify,
            LogMessage: logMessage);
    }
}

internal static class CaptureSelfTestStates
{
    public const string Pending = "pending";
    public const string Healthy = "healthy";
    public const string Recovering = "recovering";
    public const string Failed = "failed";
}

internal sealed record CaptureSelfTestHeartbeatProjection(
    string? State,
    string? ErrorCode,
    int? RemediationCount);

internal sealed record CaptureSelfTestDecision(
    string State,
    string? FailureCode,
    bool RequestHookReset,
    bool RequestImmediateFlush,
    bool NotifyUserFailure,
    string? LogMessage)
{
    public static CaptureSelfTestDecision None { get; } = new(
        State: CaptureSelfTestStates.Healthy,
        FailureCode: null,
        RequestHookReset: false,
        RequestImmediateFlush: false,
        NotifyUserFailure: false,
        LogMessage: null);
}
