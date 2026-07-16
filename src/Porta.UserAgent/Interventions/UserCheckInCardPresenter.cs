namespace Porta.UserAgent.Interventions;

using System.Windows.Threading;

internal interface IUserCheckInCardPresenter : IDisposable
{
    // Shows the compact bottom-right check-in card and routes the single reply to onResponse.
    // Returns true only when the card was actually dispatched to show; false (with errorCode) when
    // the surface is disposed, not ready, or already showing another card — so the caller can avoid
    // marking the check-in as offered when nothing reached the screen.
    bool TryShow(UserCheckInPrompt prompt, Action<UserCheckInResponse> onResponse, out string errorCode);
}

// Workstream D — the always-on-top presenter for the conversational check-in card. Mirrors
// UserBreakOverlayPresenter: an owned STA thread + dispatcher so the card renders independent of
// the widget surface (it fires even when the widget is hidden). Simpler than the break overlay —
// there is no countdown or crash-persisted state; the card asks one question and raises one reply.
internal sealed class UserCheckInCardPresenter : IUserCheckInCardPresenter
{
    private static readonly TimeSpan ThreadReadyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConfirmationDisplay = TimeSpan.FromSeconds(4.5);

    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private UserCheckInCardWindow? _window;
    private DispatcherTimer? _confirmationTimer;
    private Action<UserCheckInResponse>? _activeResponse;
    private bool _disposed;

    public UserCheckInCardPresenter(bool startThread = true)
    {
        if (startThread)
        {
            EnsureThreadStarted();
        }
    }

    public bool TryShow(UserCheckInPrompt prompt, Action<UserCheckInResponse> onResponse, out string errorCode)
    {
        errorCode = string.Empty;
        if (_disposed)
        {
            errorCode = "checkin_card_disposed";
            return false;
        }

        EnsureThreadStarted();
        if (!WaitForReady())
        {
            errorCode = "checkin_card_not_ready";
            return false;
        }

        lock (_gate)
        {
            // One card at a time. A card left open (unanswered) keeps this set until the user
            // replies or dismisses, so a second kind won't stack on top of it.
            if (_activeResponse != null)
            {
                errorCode = "checkin_card_busy";
                return false;
            }

            _activeResponse = onResponse;
        }

        try
        {
            _dispatcher!.BeginInvoke(new Action(() =>
            {
                EnsureWindowOnCurrentDispatcher();
                _window?.ShowPrompt(prompt);
                UserAgentFileLog.Info($"checkin_card_shown kind={prompt.Kind}");
            }), DispatcherPriority.Send);
            return true;
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("checkin_card_dispatch_failed", ex);
            lock (_gate)
            {
                _activeResponse = null;
            }

            errorCode = "checkin_card_dispatch_failed";
            return false;
        }
    }

    // Echoes a short acknowledgement on the card after the user replied, then auto-hides it. Best
    // effort: if the surface isn't ready this is simply skipped (the write-back already succeeded).
    public void ShowConfirmation(UserCheckInKind kind, string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Dispatcher? dispatcher;
        lock (_gate)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                EnsureWindowOnCurrentDispatcher();
                _window?.ShowConfirmation(kind, message);
                _confirmationTimer ??= CreateConfirmationTimer();
                _confirmationTimer.Stop();
                _confirmationTimer.Start();
            }), DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("checkin_card_confirmation_failed", ex);
        }
    }

    private DispatcherTimer CreateConfirmationTimer()
    {
        var timer = new DispatcherTimer { Interval = ConfirmationDisplay };
        timer.Tick += (_, _) =>
        {
            _confirmationTimer?.Stop();
            _window?.Hide();
        };
        return timer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Dispatcher? dispatcher;
        lock (_gate)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        _confirmationTimer?.Stop();
                        if (_window != null)
                        {
                            _window.Close();
                            _window = null;
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

        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
        }

        if (thread is { IsAlive: true })
        {
            try
            {
                thread.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }

    private void EnsureThreadStarted()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "PORTA.UserAgent.CheckInCard"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private bool WaitForReady() => _ready.Task.Wait(ThreadReadyTimeout) && _ready.Task.Result;

    private void ThreadMain()
    {
        try
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            lock (_gate)
            {
                _dispatcher = dispatcher;
            }

            EnsureWindowOnCurrentDispatcher();
            _ready.TrySetResult(true);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("checkin_card_thread_failed", ex);
            _ready.TrySetResult(false);
        }
        finally
        {
            lock (_gate)
            {
                _window = null;
                _dispatcher = null;
                _thread = null;
            }
        }
    }

    private void EnsureWindowOnCurrentDispatcher()
    {
        if (_window is { IsLoaded: true })
        {
            return;
        }

        var window = new UserCheckInCardWindow(OnCardResponse);
        window.Closed += (_, _) =>
        {
            if (!_disposed)
            {
                _window = null;
            }
        };
        _window = window;
    }

    // Runs on the STA thread when the card raises its single reply (answer or dismiss). Clears the
    // active slot first so the next check-in can show, then hands the reply to the host coordinator.
    private void OnCardResponse(UserCheckInResponse response)
    {
        Action<UserCheckInResponse>? handler;
        lock (_gate)
        {
            handler = _activeResponse;
            _activeResponse = null;
        }

        handler?.Invoke(response);
    }
}
