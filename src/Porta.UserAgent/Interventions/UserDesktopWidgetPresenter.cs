namespace Porta.UserAgent.Interventions;

using Porta.Agent.Common;
using Porta.Agent.LocalApi;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

internal sealed class UserDesktopWidgetPresenter : IUserInterventionPresenter, IDisposable
{
    private const int WidgetHealthUnavailable = 0;
    private const int WidgetHealthStarting = 1;
    private const int WidgetHealthReady = 2;
    private static readonly TimeSpan WidgetReadyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WidgetStartupGracePeriod = TimeSpan.FromSeconds(10);
    private readonly UserInterventionToastRenderer _fallbackRenderer = new();
    private readonly LocalApiClient _actionClient = new();
    private readonly UserDesktopWidgetPlacementStore _placementStore;
    private readonly UserDesktopWidgetVisibilityStore _visibilityStore;
    private readonly UserDesktopWidgetRollout _rollout;
    private readonly IUserBreakOverlayPresenter _breakOverlayPresenter;
    private readonly object _promptGate = new();
    private readonly object _widgetLifecycleGate = new();

    private Dispatcher? _widgetDispatcher;
    private UserDesktopWidgetWindow? _widgetWindow;
    private Action<UserInterventionPromptResponse>? _promptResponseHandler;
    private string? _promptInterventionId;
    private TaskCompletionSource<bool> _widgetReady = CreateWidgetReadySource();
    private Thread? _widgetThread;
    private DateTimeOffset _widgetStartRequestedUtc = DateTimeOffset.MinValue;
    private int _widgetHealthState = WidgetHealthUnavailable;
    private int _widgetNotReadyLogged;
    private int _widgetFailedLogged;
    private int _topmostNudgeCounter;
    // Backoff for the maintenance-loop forced restart. On a machine where the widget can
    // never become healthy (e.g. its desktop surface never reports visible), the 2s loop
    // would otherwise restart the widget thread on every tick — flooding the log and
    // churning the process without ever helping. Healthy machines never enter the restart
    // path at all, so this throttle is completely inert for them.
    private static readonly TimeSpan MaintenanceRestartBackoff = TimeSpan.FromSeconds(60);
    private DateTimeOffset _lastMaintenanceRestartUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public UserDesktopWidgetPresenter(UserDesktopWidgetRollout? rollout = null)
        : this(
            rollout ?? UserDesktopWidgetRollout.FromEnvironment(),
            placementStore: null,
            visibilityStore: null,
            breakOverlayPresenter: null,
            startWidgetThread: true)
    {
    }

    internal UserDesktopWidgetPresenter(
        UserDesktopWidgetRollout rollout,
        UserDesktopWidgetPlacementStore? placementStore,
        UserDesktopWidgetVisibilityStore? visibilityStore,
        IUserBreakOverlayPresenter? breakOverlayPresenter,
        bool startWidgetThread)
    {
        _rollout = rollout;
        _placementStore = placementStore ?? new UserDesktopWidgetPlacementStore();
        _visibilityStore = visibilityStore ?? new UserDesktopWidgetVisibilityStore();
        _breakOverlayPresenter = breakOverlayPresenter ?? new UserBreakOverlayPresenter();

        if (_rollout.SupportsWidgetSurface && startWidgetThread)
        {
            UserAgentFileLog.Info("desktop_widget_rollout mode=widget_enabled");
            EnsureWidgetThreadStarted(forceRestart: false);
            return;
        }

        UserAgentFileLog.Info(
            _rollout.SupportsWidgetSurface
                ? "desktop_widget_rollout mode=widget_deferred"
                : $"desktop_widget_rollout mode=toast_fallback reason={_rollout.UnavailableReasonCode}");
    }

    internal bool SupportsWidgetSurface => _rollout.SupportsWidgetSurface;

    internal string WidgetUnavailableReasonCode => _rollout.UnavailableReasonCode;

    internal bool IsWidgetHealthyForSessionHeartbeat
    {
        get
        {
            if (!_rollout.SupportsWidgetSurface || _visibilityStore.IsHidden())
            {
                return true;
            }

            return IsWidgetHealthyForHeartbeat();
        }
    }

    internal void MaintainWidgetSurface()
    {
        if (_disposed || !_rollout.SupportsWidgetSurface || _visibilityStore.IsHidden())
        {
            return;
        }

        bool isStarting = IsWidgetStartingForHeartbeat();
        bool needsRestart = !IsWidgetHealthyForHeartbeat();

        if (!needsRestart && !isStarting)
        {
            // Periodically re-assert HWND_TOPMOST so apps like Electron/CEF can't bury the widget.
            _topmostNudgeCounter++;
            if (_topmostNudgeCounter >= 5)
            {
                _topmostNudgeCounter = 0;
                TryDispatchToWidget(static window => window.NudgeTopmost(), out _);
            }

            return;
        }

        if (isStarting)
        {
            return;
        }

        if (TryRecoverWindowOnExistingDispatcher())
        {
            UserAgentFileLog.Info("desktop_widget_window_recovery_requested");
            return;
        }

        // Backoff: cap forced restarts so a widget that can't stay healthy on this machine
        // doesn't thrash the thread (and flood the log) on every 2s maintenance tick. The
        // first restart still fires immediately; only repeats within the window are skipped.
        // Healthy machines never reach this path, so this changes nothing for them.
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if ((nowUtc - _lastMaintenanceRestartUtc) < MaintenanceRestartBackoff)
        {
            return;
        }

        _lastMaintenanceRestartUtc = nowUtc;
        UserAgentFileLog.Info("desktop_widget_recovery_requested");
        EnsureWidgetThreadStarted(forceRestart: true);
    }

    public bool TryShow(string title, string message, out string errorCode)
    {
        if (!_rollout.SupportsWidgetSurface)
        {
            return _fallbackRenderer.TryShow(title, message, out errorCode);
        }

        if (_visibilityStore.IsHidden())
        {
            errorCode = string.Empty;
            return true;
        }

        if (TryDispatchToWidget(window => window.PresentAttention(title, message), out errorCode))
        {
            return true;
        }

        return _fallbackRenderer.TryShow(title, message, out errorCode);
    }

    public bool TryPresentPrompt(
        UserInterventionPrompt prompt,
        Action<UserInterventionPromptResponse> onResponse,
        out string errorCode)
    {
        if (PortaBreakSemantics.IsBreakSuggestionAction(prompt.TipKey))
        {
            return _breakOverlayPresenter.TryPresentPrompt(prompt, onResponse, out errorCode);
        }

        if (!_rollout.SupportsWidgetSurface)
        {
            errorCode = _rollout.UnavailableReasonCode;
            return false;
        }

        if (_visibilityStore.IsHidden())
        {
            errorCode = "widget_hidden_by_user";
            return false;
        }

        lock (_promptGate)
        {
            _promptInterventionId = prompt.InterventionId;
            _promptResponseHandler = onResponse;
        }

        bool dispatched = TryDispatchToWidget(window => window.PresentPrompt(prompt), out errorCode);
        if (dispatched)
        {
            return true;
        }

        lock (_promptGate)
        {
            _promptInterventionId = null;
            _promptResponseHandler = null;
        }

        return false;
    }

    public bool TryUpdateTrayState(UserInterventionToastRenderer.UserTrayVisualState state, out string errorCode)
    {
        return _fallbackRenderer.TryUpdateTrayState(state, out errorCode);
    }

    public bool TryUpdateWidgetState(UserDesktopWidgetState state, out string errorCode)
    {
        if (!_rollout.SupportsWidgetSurface)
        {
            errorCode = _rollout.UnavailableReasonCode;
            return false;
        }

        if (_visibilityStore.IsHidden())
        {
            _ = TryDispatchToWidget(static window => window.HideWidget(), out _);
            errorCode = string.Empty;
            return true;
        }

        _ = TryDispatchToWidget(static window => window.ShowWidgetIfHidden(), out _);

        return TryDispatchToWidget(window => window.ApplyState(state), out errorCode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fallbackRenderer.Dispose();
        _actionClient.Dispose();
        _breakOverlayPresenter.Dispose();
        ShutdownWidgetThread(TimeSpan.FromSeconds(2));
    }

    private bool TryDispatchToWidget(Action<UserDesktopWidgetWindow> action, out string errorCode)
    {
        errorCode = string.Empty;
        if (!_rollout.SupportsWidgetSurface)
        {
            errorCode = _rollout.UnavailableReasonCode;
            return false;
        }

        if (_disposed)
        {
            errorCode = "widget_presenter_disposed";
            return false;
        }

        EnsureWidgetThreadStarted(forceRestart: false);
        if (!WaitForWidgetReady())
        {
            TryRecoverWindowOnExistingDispatcher();
            if (Interlocked.Exchange(ref _widgetNotReadyLogged, 1) == 0)
            {
                UserAgentFileLog.Info("desktop_widget_not_ready timeout_seconds=3");
            }

            errorCode = "widget_presenter_not_ready";
            return false;
        }

        if (!_widgetReady.Task.Result || _widgetDispatcher == null || _widgetWindow == null)
        {
            if (!TryRecoverWindowOnExistingDispatcher())
            {
                EnsureWidgetThreadStarted(forceRestart: true);
            }

            if (!WaitForWidgetReady() || !_widgetReady.Task.Result || _widgetDispatcher == null || _widgetWindow == null)
            {
                if (Interlocked.Exchange(ref _widgetFailedLogged, 1) == 0)
                {
                    UserAgentFileLog.Info($"desktop_widget_failed ready={_widgetReady.Task.Result} dispatcher_available={(_widgetDispatcher != null)}");
                }

                errorCode = "widget_presenter_failed";
                return false;
            }
        }

        try
        {
            _widgetDispatcher.BeginInvoke(() =>
            {
                if (_widgetWindow is not { } activeWindow)
                {
                    return;
                }

                if (!ReferenceEquals(activeWindow.Dispatcher, Dispatcher.CurrentDispatcher)
                    || !activeWindow.CanAcceptUiActions)
                {
                    return;
                }

                action(activeWindow);
            });
            return true;
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("widget_dispatch_failed", ex);
            errorCode = "widget_dispatch_failed";
            return false;
        }
    }

    private static TaskCompletionSource<bool> CreateWidgetReadySource()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private bool WaitForWidgetReady()
    {
        return _widgetReady.Task.Wait(WidgetReadyTimeout);
    }

    private Thread CreateWidgetThread()
    {
        var thread = new Thread(WidgetThreadMain)
        {
            IsBackground = true,
            Name = "PORTA.UserAgent.DesktopWidget"
        };
        thread.SetApartmentState(ApartmentState.STA);
        return thread;
    }

    private bool TryRecoverWindowOnExistingDispatcher()
    {
        if (_disposed || !_rollout.SupportsWidgetSurface)
        {
            return false;
        }

        Dispatcher? dispatcher;
        lock (_widgetLifecycleGate)
        {
            if (_widgetDispatcher is null
                || _widgetDispatcher.HasShutdownStarted
                || _widgetDispatcher.HasShutdownFinished
                || _widgetThread is not { IsAlive: true })
            {
                return false;
            }

            if (IsWidgetOperationalLocked() || IsWidgetStartingLocked(DateTimeOffset.UtcNow))
            {
                return true;
            }

            dispatcher = _widgetDispatcher;
            _widgetReady = CreateWidgetReadySource();
            _widgetNotReadyLogged = 0;
            _widgetFailedLogged = 0;
            _widgetStartRequestedUtc = DateTimeOffset.UtcNow;
        }

        try
        {
            dispatcher.BeginInvoke(new Action(EnsureWidgetWindowOnCurrentDispatcher), DispatcherPriority.Send);
            return true;
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("widget_recovery_dispatch_failed", ex);
            _widgetReady.TrySetResult(false);
            return false;
        }
    }

    private void EnsureWidgetWindowOnCurrentDispatcher()
    {
        if (_disposed)
        {
            SetWidgetHealthStateOnWidgetThread(WidgetHealthUnavailable);
            _widgetReady.TrySetResult(false);
            return;
        }

        Dispatcher currentDispatcher = Dispatcher.CurrentDispatcher;
        UserDesktopWidgetWindow? currentWindow;
        lock (_widgetLifecycleGate)
        {
            currentWindow = _widgetWindow;
        }

        if (currentWindow is not null
            && !ReferenceEquals(currentWindow.Dispatcher, currentDispatcher))
        {
            lock (_widgetLifecycleGate)
            {
                if (ReferenceEquals(_widgetWindow, currentWindow))
                {
                    _widgetWindow = null;
                }
            }

            currentWindow = null;
        }

        if (currentWindow is { IsLoaded: true })
        {
            if (currentWindow.IsClosingOrClosed)
            {
                lock (_widgetLifecycleGate)
                {
                    if (ReferenceEquals(_widgetWindow, currentWindow))
                    {
                        _widgetWindow = null;
                    }
                }
            }
            else
            {
                try
                {
                    if (_visibilityStore.IsHidden())
                    {
                        currentWindow.HideWidget();
                    }
                    else
                    {
                        currentWindow.ShowWidgetIfHidden();
                        currentWindow.EnsureVisibleForRecovery();
                    }

                    bool ready = _visibilityStore.IsHidden() || currentWindow.IsSurfaceVisibleForHeartbeat;
                    SetWidgetHealthStateOnWidgetThread(ready ? WidgetHealthReady : WidgetHealthUnavailable);
                    _widgetReady.TrySetResult(ready);
                    return;
                }
                catch (Exception ex)
                {
                    SetWidgetHealthStateOnWidgetThread(WidgetHealthUnavailable);
                    UserAgentFileLog.Error("widget_window_reuse_failed", ex);
                }
            }
        }

        try
        {
            SetWidgetHealthStateOnWidgetThread(WidgetHealthStarting);
            UserAgentFileLog.Info("desktop_widget_window_initializing");
            var window = new UserDesktopWidgetWindow(OnWidgetActionRequested, OnPromptResponseRequested, _placementStore);
            window.Closed += OnWidgetWindowClosed;

            lock (_widgetLifecycleGate)
            {
                if (_widgetDispatcher is null || _widgetDispatcher.HasShutdownStarted || _widgetDispatcher.HasShutdownFinished)
                {
                    _widgetDispatcher = Dispatcher.CurrentDispatcher;
                }

                _widgetWindow = window;
            }

            // Read the hidden flag once, BEFORE Show(), so the window is never rendered
            // visible and then hidden — that sequence caused a 1-frame flicker on startup.
            bool startHidden = _visibilityStore.IsHidden();
            if (startHidden)
            {
                window.StartHidden(); // HWND initialised, hidden flag set — no Show() call
            }
            else
            {
                window.Show();
                window.EnsureVisibleForRecovery();
            }

            bool ready = startHidden || window.IsSurfaceVisibleForHeartbeat;
            SetWidgetHealthStateOnWidgetThread(ready ? WidgetHealthReady : WidgetHealthUnavailable);
            UserAgentFileLog.Info("desktop_widget_window_shown");
            _widgetReady.TrySetResult(ready);
        }
        catch (Exception ex)
        {
            SetWidgetHealthStateOnWidgetThread(WidgetHealthUnavailable);
            UserAgentFileLog.Error("widget_window_show_failed", ex);
            _widgetReady.TrySetResult(false);
            try
            {
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
            catch
            {
            }
        }
    }

    private void OnWidgetWindowClosed(object? sender, EventArgs e)
    {
        lock (_widgetLifecycleGate)
        {
            if (ReferenceEquals(_widgetWindow, sender))
            {
                _widgetWindow = null;
            }

            SetWidgetHealthStateOnWidgetThread(WidgetHealthUnavailable);
            if (!_disposed)
            {
                _widgetReady = CreateWidgetReadySource();
            }
        }
    }

    private void ShutdownWidgetThread(TimeSpan joinTimeout)
    {
        Dispatcher? dispatcher;
        UserDesktopWidgetWindow? widgetWindow;
        Thread? widgetThread;
        lock (_widgetLifecycleGate)
        {
            dispatcher = _widgetDispatcher;
            widgetWindow = _widgetWindow;
            widgetThread = _widgetThread;
        }

        if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            bool canCloseWindow = widgetWindow is not null
                && ReferenceEquals(widgetWindow.Dispatcher, dispatcher);
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (canCloseWindow)
                        {
                            widgetWindow!.AllowCloseForShutdown();
                            widgetWindow.Close();
                        }
                    }
                    catch
                    {
                    }

                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }));
            }
            catch
            {
            }
        }

        if (widgetThread is { IsAlive: true })
        {
            try
            {
                widgetThread.Join(joinTimeout);
            }
            catch
            {
            }
        }
    }

    private void EnsureWidgetThreadStarted(bool forceRestart)
    {
        if (_disposed || !_rollout.SupportsWidgetSurface)
        {
            return;
        }

        if (forceRestart && TryRecoverWindowOnExistingDispatcher())
        {
            return;
        }

        Thread? priorThread = null;
        lock (_widgetLifecycleGate)
        {
            if (!forceRestart && (IsWidgetOperationalLocked() || IsWidgetStartingLocked(DateTimeOffset.UtcNow)))
            {
                return;
            }

            if (forceRestart)
            {
                priorThread = _widgetThread;
            }
        }

        if (priorThread is { IsAlive: true })
        {
            ShutdownWidgetThread(TimeSpan.FromSeconds(2));
            lock (_widgetLifecycleGate)
            {
                if (_widgetThread is { IsAlive: true })
                {
                    UserAgentFileLog.Info("desktop_widget_recovery_deferred reason=prior_thread_still_alive");
                    return;
                }
            }
        }

        Thread widgetThread = CreateWidgetThread();
        lock (_widgetLifecycleGate)
        {
            if (!forceRestart && (IsWidgetOperationalLocked() || IsWidgetStartingLocked(DateTimeOffset.UtcNow)))
            {
                return;
            }

            _widgetReady = CreateWidgetReadySource();
            _widgetNotReadyLogged = 0;
            _widgetFailedLogged = 0;
            _widgetStartRequestedUtc = DateTimeOffset.UtcNow;
            _widgetThread = widgetThread;
        }

        widgetThread.Start();
    }

    private void WidgetThreadMain()
    {
        Thread currentThread = Thread.CurrentThread;
        try
        {
            UserAgentFileLog.Info("desktop_widget_thread_starting");
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.CurrentDispatcher.UnhandledException += (_, eventArgs) =>
            {
                UserAgentFileLog.Error("widget_dispatcher_unhandled_exception", eventArgs.Exception);
            };

            lock (_widgetLifecycleGate)
            {
                if (ReferenceEquals(_widgetThread, currentThread))
                {
                    _widgetDispatcher = dispatcher;
                }
            }

            SetWidgetHealthStateOnWidgetThread(WidgetHealthStarting);
            EnsureWidgetWindowOnCurrentDispatcher();
            Dispatcher.Run();
            UserAgentFileLog.Info("desktop_widget_thread_exit");
        }
        catch (Exception ex)
        {
            SetWidgetHealthStateOnWidgetThread(WidgetHealthUnavailable);
            UserAgentFileLog.Error("widget_thread_failed", ex);
            _widgetReady.TrySetResult(false);
        }
        finally
        {
            lock (_widgetLifecycleGate)
            {
                if (ReferenceEquals(_widgetThread, currentThread))
                {
                    _widgetWindow = null;
                    _widgetDispatcher = null;
                    _widgetThread = null;
                    SetWidgetHealthStateOnWidgetThread(WidgetHealthUnavailable);
                    if (!_disposed)
                    {
                        _widgetReady = CreateWidgetReadySource();
                    }
                }
            }
        }
    }

    private bool IsWidgetOperationalLocked()
    {
        if (_widgetDispatcher is not { HasShutdownStarted: false, HasShutdownFinished: false } dispatcher)
        {
            return false;
        }

        return _widgetThread is { IsAlive: true }
            && _widgetWindow is { CanAcceptUiActions: true, IsSurfaceVisibleForHeartbeat: true } window
            && ReferenceEquals(window.Dispatcher, dispatcher)
            && GetWidgetHealthState() == WidgetHealthReady;
    }

    private bool IsWidgetStartingLocked(DateTimeOffset nowUtc)
    {
        return _widgetThread is { IsAlive: true }
            && !_widgetReady.Task.IsCompleted
            && (nowUtc - _widgetStartRequestedUtc) < WidgetStartupGracePeriod;
    }

    private bool IsWidgetHealthyForHeartbeat()
    {
        lock (_widgetLifecycleGate)
        {
            int state = GetWidgetHealthState();
            if (state == WidgetHealthStarting)
            {
                return IsWidgetStartingLocked(DateTimeOffset.UtcNow);
            }

            return state == WidgetHealthReady
                && IsWidgetOperationalLocked();
        }
    }

    private bool IsWidgetStartingForHeartbeat()
    {
        lock (_widgetLifecycleGate)
        {
            return IsWidgetStartingLocked(DateTimeOffset.UtcNow);
        }
    }

    private int GetWidgetHealthState() => Volatile.Read(ref _widgetHealthState);

    private void SetWidgetHealthStateOnWidgetThread(int state) => Interlocked.Exchange(ref _widgetHealthState, state);

    private void OnWidgetActionRequested(UserDesktopWidgetAction action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteWidgetActionAsync(action).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UserAgentFileLog.Error($"widget_action_failed action={action}", ex);
                _ = TryShow("Tracking limited", "The widget action failed. Try again from Control Center.", out _);
            }
        });
    }

    private void OnPromptResponseRequested(UserInterventionPromptResponse response)
    {
        Action<UserInterventionPromptResponse>? handler = null;

        lock (_promptGate)
        {
            if (!string.Equals(_promptInterventionId, response.InterventionId, StringComparison.Ordinal))
            {
                return;
            }

            handler = _promptResponseHandler;
            _promptInterventionId = null;
            _promptResponseHandler = null;
        }

        handler?.Invoke(response);
    }

    private async Task ExecuteWidgetActionAsync(UserDesktopWidgetAction action)
    {
        switch (action)
        {
            case UserDesktopWidgetAction.OpenNow:
                if (!TryOpenControlCenter("today"))
                {
                    _ = TryShow("Tracking limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenHistory:
                if (!TryOpenControlCenter("history"))
                {
                    _ = TryShow("Tracking limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenActions:
                if (!TryOpenControlCenter("actions"))
                {
                    _ = TryShow("Tracking limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenTracking:
                if (!TryOpenControlCenter("tracking"))
                {
                    _ = TryShow("Tracking limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenSettings:
                if (!TryOpenControlCenter("settings"))
                {
                    _ = TryShow("Tracking limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenHealth:
                if (!TryOpenControlCenter("system"))
                {
                    _ = TryShow("Tracking limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.HideWidget:
                _visibilityStore.Hide();
                _ = TryDispatchToWidget(static window => window.HideWidget(), out _);
                break;

            case UserDesktopWidgetAction.RetryCritical:
                await ExecuteCriticalRetryAsync().ConfigureAwait(false);
                break;

            case UserDesktopWidgetAction.PauseFifteenMinutes:
                if (await _actionClient
                    .PauseAsync("widget_pause_15m", DateTimeOffset.UtcNow.AddMinutes(15))
                    .ConfigureAwait(false))
                {
                    _ = TryShow("Ari", "Paused for 15 minutes.", out _);
                }
                else
                {
                    _ = TryShow("Tracking limited", "Ari could not pause right now.", out _);
                }

                break;

            case UserDesktopWidgetAction.PauseOneHour:
                if (await _actionClient
                    .PauseAsync("widget_pause_1h", DateTimeOffset.UtcNow.AddHours(1))
                    .ConfigureAwait(false))
                {
                    _ = TryShow("Ari", "Paused for 1 hour.", out _);
                }
                else
                {
                    _ = TryShow("Tracking limited", "Ari could not pause right now.", out _);
                }

                break;

            case UserDesktopWidgetAction.Resume:
                if (await _actionClient.ResumeAsync().ConfigureAwait(false))
                {
                    _ = TryShow("Ari", "Resumed.", out _);
                }
                else
                {
                    _ = TryShow("Tracking limited", "Ari could not resume right now.", out _);
                }

                break;

            case UserDesktopWidgetAction.Recalibrate:
                if (await _actionClient.RecalibrateBaselineAsync().ConfigureAwait(false) != null)
                {
                    _ = TryShow("Ari", "Calibration restart requested.", out _);
                }
                else
                {
                    _ = TryShow("Tracking limited", "Ari could not start recalibration right now.", out _);
                }

                break;

            case UserDesktopWidgetAction.ResetPosition:
                TryDispatchToWidget(window => window.ResetToDefaultPosition(), out _);
                break;
        }
    }

    private async Task ExecuteCriticalRetryAsync()
    {
        LocalApiStatusResponse? status = await _actionClient.GetStatusAsync().ConfigureAwait(false);
        if (status?.ServiceRunning == true
            && status.LocalApiAuthReady
            && status.ConsentActive
            && string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(status.CaptureFailureReason)
            && string.IsNullOrWhiteSpace(status.OwnershipWarning))
        {
            _ = TryShow("Ari", "Ari rechecked the runtime. Core services are reachable again.", out _);
            return;
        }

        if (status == null || !status.ServiceRunning)
        {
            LocalApiAckResponse? restart = await _actionClient.RestartServiceAsync().ConfigureAwait(false);
            if (restart?.Success == true)
            {
                _ = TryShow("Ari", "Runtime restart requested. Open System if recovery takes longer than expected.", out _);
                return;
            }
        }

        _ = TryShow("Tracking limited", "Ari retried the runtime check, but the issue is still active. Open System for details.", out _);
    }

    private static bool TryOpenControlCenter(string? section)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Porta.ControlCenter.exe");
            if (!File.Exists(path))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = string.IsNullOrWhiteSpace(section) ? string.Empty : $"--section={section}",
                WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("widget_open_control_center_failed", ex);
            return false;
        }
    }
}
