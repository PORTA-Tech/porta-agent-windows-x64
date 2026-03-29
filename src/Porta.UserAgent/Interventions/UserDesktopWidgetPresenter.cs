namespace Porta.UserAgent.Interventions;

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Porta.Agent.LocalApi;

internal sealed class UserDesktopWidgetPresenter : IUserInterventionPresenter, IDisposable
{
    private readonly UserInterventionToastRenderer _fallbackRenderer = new();
    private readonly LocalApiClient _actionClient = new();
    private readonly UserDesktopWidgetPlacementStore _placementStore = new();
    private readonly TaskCompletionSource<bool> _widgetReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _widgetThread;

    private Dispatcher? _widgetDispatcher;
    private UserDesktopWidgetWindow? _widgetWindow;
    private bool _disposed;

    public UserDesktopWidgetPresenter()
    {
        _widgetThread = new Thread(WidgetThreadMain)
        {
            IsBackground = true,
            Name = "PORTA.UserAgent.DesktopWidget"
        };
        _widgetThread.SetApartmentState(ApartmentState.STA);
        _widgetThread.Start();
    }

    public bool TryShow(string title, string message, out string errorCode)
    {
        if (TryDispatchToWidget(window => window.PresentAttention(title, message), out errorCode))
        {
            return true;
        }

        return _fallbackRenderer.TryShow(title, message, out errorCode);
    }

    public bool TryUpdateTrayState(UserInterventionToastRenderer.UserTrayVisualState state, out string errorCode)
    {
        return _fallbackRenderer.TryUpdateTrayState(state, out errorCode);
    }

    public bool TryUpdateWidgetState(UserDesktopWidgetState state, out string errorCode)
    {
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

        if (_widgetDispatcher != null)
        {
            try
            {
                _widgetDispatcher.BeginInvoke(() =>
                {
                    _widgetWindow?.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                });
            }
            catch
            {
            }
        }

        if (_widgetThread.IsAlive)
        {
            _widgetThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private bool TryDispatchToWidget(Action<UserDesktopWidgetWindow> action, out string errorCode)
    {
        errorCode = string.Empty;
        if (_disposed)
        {
            errorCode = "widget_presenter_disposed";
            return false;
        }

        if (!_widgetReady.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            errorCode = "widget_presenter_not_ready";
            return false;
        }

        if (!_widgetReady.Task.Result || _widgetDispatcher == null)
        {
            errorCode = "widget_presenter_failed";
            return false;
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

    private void WidgetThreadMain()
    {
        try
        {
            _widgetDispatcher = Dispatcher.CurrentDispatcher;
            _widgetWindow = new UserDesktopWidgetWindow(OnWidgetActionRequested, _placementStore);
            _widgetWindow.Show();
            _widgetReady.TrySetResult(true);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("widget_thread_failed", ex);
            _widgetReady.TrySetResult(false);
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
