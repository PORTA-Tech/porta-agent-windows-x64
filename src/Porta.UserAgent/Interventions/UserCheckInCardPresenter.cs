namespace Porta.UserAgent.Interventions;

using System.Windows.Threading;

internal interface IUserCheckInCardPresenter : IDisposable
{
    // Shows the compact bottom-right check-in card and routes the single reply to onResponse.
    // Returns true only when the card was actually dispatched to show; false (with errorCode) when
    // the surface is disposed, not ready, or already showing another card — so the caller can avoid
    // marking the check-in as offered when nothing reached the screen.
    // nowLocal is supplied rather than read here so the staleness rule stays a pure function the
    // presenter merely applies (UserCheckInStalenessPolicy).
    bool TryShow(
        UserCheckInPrompt prompt,
        Action<UserCheckInResponse> onResponse,
        DateTimeOffset nowLocal,
        out string errorCode);

    // Part 5 (async narrative) — swap the LLM line onto a card that is already on screen. Best
    // effort by contract: returns false when the card is gone, is showing another kind, or the
    // user has started answering. A false is a normal outcome, never an error.
    bool TryUpgradeMessage(UserCheckInKind kind, string line);
}

// Workstream D — the always-on-top presenter for the conversational check-in card. Mirrors
// UserBreakOverlayPresenter: an owned STA thread + dispatcher so the card renders independent of
// the widget surface (it fires even when the widget is hidden). Simpler than the break overlay —
// there is no countdown or crash-persisted state; the card asks one question and raises one reply.
internal sealed class UserCheckInCardPresenter : IUserCheckInCardPresenter
{
    private static readonly TimeSpan ThreadReadyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConfirmationDisplay = TimeSpan.FromSeconds(4.5);
    // A confirmation carrying an action button needs long enough to read AND click.
    private static readonly TimeSpan ConfirmationWithActionDisplay = TimeSpan.FromSeconds(12);
    // The upgrade is cosmetic; never let marshalling it block the caller for long.
    private static readonly TimeSpan UpgradeMarshalTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private UserCheckInCardWindow? _window;
    private DispatcherTimer? _confirmationTimer;
    private Action<UserCheckInResponse>? _activeResponse;
    // Step 2 — what is on screen and when it was shown, so a card that outlives its goal day can
    // be expired instead of blocking every later check-in behind it. Set and cleared with
    // _activeResponse, always under _gate.
    private OpenCheckInCard? _openCard;
    private bool _disposed;

    public UserCheckInCardPresenter(bool startThread = true)
    {
        if (startThread)
        {
            EnsureThreadStarted();
        }
    }

    public bool TryShow(
        UserCheckInPrompt prompt,
        Action<UserCheckInResponse> onResponse,
        DateTimeOffset nowLocal,
        out string errorCode)
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

        OpenCheckInCard? expired = null;
        lock (_gate)
        {
            // One card at a time. A card left open (unanswered) keeps this set until the user
            // replies or dismisses, so a second kind won't stack on top of it — EXCEPT when that
            // card has outlived its goal day, which is how last night's EOD card used to sit in
            // front of this morning's card until it was dismissed by hand (step 2).
            if (_activeResponse != null)
            {
                StaleCardDecision decision = UserCheckInStalenessPolicy.Evaluate(_openCard, nowLocal);
                if (!decision.Expire)
                {
                    UserAgentFileLog.Info(
                        $"checkin_card_busy kind={prompt.Kind} reason={decision.ReasonCode}");
                    errorCode = "checkin_card_busy";
                    return false;
                }

                expired = _openCard;
                _activeResponse = null;
                _openCard = null;
            }

            _activeResponse = onResponse;
            _openCard = new OpenCheckInCard(prompt.Kind, nowLocal);
        }

        if (expired is { } stale)
        {
            // The abandoned card is hidden, not answered: it raises no response, so nothing is
            // recorded as if the user had replied to it. Queued ahead of the new card's ShowPrompt
            // on the same dispatcher, so the two can never race.
            UserAgentFileLog.Info(
                $"checkin_card_expired kind={stale.Kind} shown={stale.ShownAtLocal:yyyy-MM-ddTHH:mm:ss} reason=stale_goal_day_rollover");
            TryHideExpiredCard();
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
                _openCard = null;
            }

            errorCode = "checkin_card_dispatch_failed";
            return false;
        }
    }

    /// <summary>
    /// Hides a card abandoned by the goal-day rollover. Best effort by design: if the dispatcher
    /// is gone the slot has already been cleared, so the due check-in still gets its turn — the
    /// point of the rule is that nothing can block the morning card, including this cleanup.
    /// </summary>
    private void TryHideExpiredCard()
    {
        Dispatcher? dispatcher;
        lock (_gate)
        {
            dispatcher = _dispatcher;
        }

        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _confirmationTimer?.Stop();
                    _window?.Hide();
                }),
                DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("checkin_card_expire_hide_failed", ex);
        }
    }

    /// <summary>
    /// Part 5 (async narrative) — applies the late LLM line to the card already on screen. The
    /// window owns the refusal rules; this marshals onto its STA thread and reports what it
    /// decided. Runs synchronously (Invoke, not BeginInvoke) so the caller can log whether the
    /// line actually reached the screen — the whole point of the async path is knowing that.
    /// </summary>
    public bool TryUpgradeMessage(UserCheckInKind kind, string line)
    {
        if (_disposed || string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        Dispatcher? dispatcher;
        lock (_gate)
        {
            // No active card means nothing to upgrade — the user already answered or dismissed.
            if (_activeResponse is null)
            {
                return false;
            }

            dispatcher = _dispatcher;
        }

        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            return dispatcher.Invoke(
                () => _window?.TryUpgradeMessage(kind, line) ?? false,
                DispatcherPriority.Send,
                CancellationToken.None,
                UpgradeMarshalTimeout);
        }
        catch (Exception ex)
        {
            // A busy or torn-down dispatcher must never surface as a check-in failure.
            UserAgentFileLog.Error("checkin_card_upgrade_failed", ex);
            return false;
        }
    }

    // Echoes a short acknowledgement on the card after the user replied, then auto-hides it. Best
    // effort: if the surface isn't ready this is simply skipped (the write-back already succeeded).
    // Part 5c — an optional single action button ("has power") rides along; the card shows longer
    // so there is time to click it.
    public void ShowConfirmation(UserCheckInKind kind, string message, string? actionLabel = null, Action? onAction = null)
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

        bool hasAction = !string.IsNullOrWhiteSpace(actionLabel) && onAction != null;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                EnsureWindowOnCurrentDispatcher();
                _window?.ShowConfirmation(kind, message, actionLabel, onAction);
                _confirmationTimer ??= CreateConfirmationTimer();
                _confirmationTimer.Stop();
                _confirmationTimer.Interval = hasAction ? ConfirmationWithActionDisplay : ConfirmationDisplay;
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
            _openCard = null;
        }

        handler?.Invoke(response);
    }
}
