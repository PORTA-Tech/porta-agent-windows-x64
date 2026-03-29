namespace Porta.UserAgent.Interventions;

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Porta.Agent.LocalApi;

internal sealed class UserDesktopWidgetPresenter : IUserInterventionPresenter, IDisposable
{
    private readonly UserInterventionToastRenderer _fallbackRenderer = new();
    private readonly LocalApiClient _actionClient = new();
    private readonly UserDesktopWidgetPlacementStore _placementStore = new();
    private readonly UserDesktopWidgetVisibilityStore _visibilityStore = new();
    private readonly UserDesktopWidgetRollout _rollout;
    private readonly object _promptGate = new();
    private readonly object _widgetLifecycleGate = new();

    private Application? _widgetApplication;
    private Dispatcher? _widgetDispatcher;
    private UserDesktopWidgetWindow? _widgetWindow;
    private Action<UserInterventionPromptResponse>? _promptResponseHandler;
    private string? _promptInterventionId;
    private TaskCompletionSource<bool> _widgetReady = CreateWidgetReadySource();
    private Thread? _widgetThread;
    private int _widgetNotReadyLogged;
    private int _widgetFailedLogged;
    private bool _disposed;

    public UserDesktopWidgetPresenter(UserDesktopWidgetRollout? rollout = null)
    {
        _rollout = rollout ?? UserDesktopWidgetRollout.FromEnvironment();

        if (_rollout.SupportsWidgetSurface)
        {
            UserAgentFileLog.Info("desktop_widget_rollout mode=widget_enabled");
            EnsureWidgetThreadStarted(forceRestart: false);
            return;
        }

        UserAgentFileLog.Info($"desktop_widget_rollout mode=toast_fallback reason={_rollout.UnavailableReasonCode}");
    }

    internal bool SupportsWidgetSurface => _rollout.SupportsWidgetSurface;

    internal string WidgetUnavailableReasonCode => _rollout.UnavailableReasonCode;

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

        Dispatcher? dispatcher;
        Application? widgetApplication;
        UserDesktopWidgetWindow? widgetWindow;
        Thread? widgetThread;
        lock (_widgetLifecycleGate)
        {
            dispatcher = _widgetDispatcher;
            widgetApplication = _widgetApplication;
            widgetWindow = _widgetWindow;
            widgetThread = _widgetThread;
        }

        if (dispatcher != null)
        {
            try
            {
                dispatcher.BeginInvoke(() =>
                {
                    widgetWindow?.Close();
                    widgetApplication?.Shutdown();
                });
            }
            catch
            {
            }
        }

        if (widgetThread is { IsAlive: true })
        {
            widgetThread.Join(TimeSpan.FromSeconds(2));
        }
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
            if (Interlocked.Exchange(ref _widgetNotReadyLogged, 1) == 0)
            {
                UserAgentFileLog.Info("desktop_widget_not_ready timeout_seconds=3");
            }

            errorCode = "widget_presenter_not_ready";
            return false;
        }

        if (!_widgetReady.Task.Result || _widgetDispatcher == null || _widgetWindow == null)
        {
            EnsureWidgetThreadStarted(forceRestart: true);
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
                if (_widgetWindow != null)
                {
                    action(_widgetWindow);
                }
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
        return _widgetReady.Task.Wait(TimeSpan.FromSeconds(3));
    }

    private void EnsureWidgetThreadStarted(bool forceRestart)
    {
        if (_disposed || !_rollout.SupportsWidgetSurface)
        {
            return;
        }

        Thread? priorThread = null;
        Dispatcher? priorDispatcher = null;
        Application? priorApplication = null;
        UserDesktopWidgetWindow? priorWindow = null;
        lock (_widgetLifecycleGate)
        {
            bool threadAlive = _widgetThread is { IsAlive: true };
            if (!forceRestart && (threadAlive || (_widgetDispatcher != null && _widgetWindow != null)))
            {
                return;
            }

            priorThread = forceRestart ? _widgetThread : null;
            priorDispatcher = forceRestart ? _widgetDispatcher : null;
            priorApplication = forceRestart ? _widgetApplication : null;
            priorWindow = forceRestart ? _widgetWindow : null;
            _widgetReady = CreateWidgetReadySource();
            _widgetNotReadyLogged = 0;
            _widgetFailedLogged = 0;
            _widgetThread = new Thread(WidgetThreadMain)
            {
                IsBackground = true,
                Name = "PORTA.UserAgent.DesktopWidget"
            };
            _widgetThread.SetApartmentState(ApartmentState.STA);
            _widgetThread.Start();
        }

        if (priorDispatcher != null)
        {
            try
            {
                priorDispatcher.BeginInvoke(() =>
                {
                    priorWindow?.Close();
                    priorApplication?.Shutdown();
                });
            }
            catch
            {
            }
        }

        if (priorThread is { IsAlive: true })
        {
            try
            {
                priorThread.Join(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }
    }

    private void WidgetThreadMain()
    {
        Thread currentThread = Thread.CurrentThread;
        try
        {
            UserAgentFileLog.Info("desktop_widget_thread_starting");
            _widgetApplication = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            _widgetApplication.DispatcherUnhandledException += (_, eventArgs) =>
            {
                UserAgentFileLog.Error("widget_dispatcher_unhandled_exception", eventArgs.Exception);
            };
            _widgetApplication.Exit += (_, _) => UserAgentFileLog.Info("desktop_widget_thread_exit");
            _widgetApplication.Startup += (_, _) =>
            {
                try
                {
                    UserAgentFileLog.Info("desktop_widget_window_initializing");
                    _widgetDispatcher = _widgetApplication.Dispatcher;
                    _widgetWindow = new UserDesktopWidgetWindow(OnWidgetActionRequested, OnPromptResponseRequested, _placementStore);
                    _widgetWindow.Show();
                    UserAgentFileLog.Info("desktop_widget_window_shown");
                    _widgetReady.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    UserAgentFileLog.Error("widget_window_show_failed", ex);
                    _widgetReady.TrySetResult(false);
                    _widgetApplication.Shutdown(-1);
                }
            };
            _widgetApplication.Run();
        }
        catch (Exception ex)
        {
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
                    _widgetApplication = null;
                    if (!_disposed)
                    {
                        _widgetReady = CreateWidgetReadySource();
                    }
                }
            }
        }
    }

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
                _ = TryShow("Ari limited", "The widget action failed. Try again from Control Center.", out _);
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
                if (!TryOpenControlCenter("now"))
                {
                    _ = TryShow("Ari limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenActions:
                if (!TryOpenControlCenter("actions"))
                {
                    _ = TryShow("Ari limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenSettings:
                if (!TryOpenControlCenter("settings"))
                {
                    _ = TryShow("Ari limited", "Control Center is not available on this device.", out _);
                }

                break;

            case UserDesktopWidgetAction.OpenHealth:
                if (!TryOpenControlCenter("health"))
                {
                    _ = TryShow("Ari limited", "Control Center is not available on this device.", out _);
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
                    _ = TryShow("Ari limited", "Ari could not pause right now.", out _);
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
                    _ = TryShow("Ari limited", "Ari could not pause right now.", out _);
                }

                break;

            case UserDesktopWidgetAction.Resume:
                if (await _actionClient.ResumeAsync().ConfigureAwait(false))
                {
                    _ = TryShow("Ari", "Resumed.", out _);
                }
                else
                {
                    _ = TryShow("Ari limited", "Ari could not resume right now.", out _);
                }

                break;

            case UserDesktopWidgetAction.Recalibrate:
                if (await _actionClient.RecalibrateBaselineAsync().ConfigureAwait(false) != null)
                {
                    _ = TryShow("Ari", "Calibration restart requested.", out _);
                }
                else
                {
                    _ = TryShow("Ari limited", "Ari could not start recalibration right now.", out _);
                }

                break;

            case UserDesktopWidgetAction.ResetPosition:
                if (TryDispatchToWidget(window => window.ResetToDefaultPosition(), out _))
                {
                    _ = TryShow("Ari", "Widget reset to the top-right corner.", out _);
                }
                else
                {
                    _ = TryShow("Ari limited", "The widget could not reset its position right now.", out _);
                }

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
                _ = TryShow("Ari", "Runtime restart requested. Check Health if recovery takes longer than expected.", out _);
                return;
            }
        }

        _ = TryShow("Ari limited", "Ari retried the runtime check, but the issue is still active. Open Health for details.", out _);
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
