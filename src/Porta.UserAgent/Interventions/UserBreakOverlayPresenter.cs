namespace Porta.UserAgent.Interventions;

using Microsoft.Win32;
using Porta.Agent.Common;
using System.Windows.Threading;

internal interface IUserBreakOverlayPresenter : IDisposable
{
    bool TryPresentPrompt(
        UserInterventionPrompt prompt,
        Action<UserInterventionPromptResponse> onResponse,
        out string errorCode);
}

internal sealed class UserBreakOverlayPresenter : IUserBreakOverlayPresenter
{
    private static readonly TimeSpan ThreadReadyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CountdownTickInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly UserBreakOverlayStateStore _stateStore;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private UserBreakOverlayWindow? _window;
    private DispatcherTimer? _countdownTimer;
    private ActiveBreakPromptState? _activePrompt;
    private ActiveBreakCountdownState? _activeCountdown;
    private bool _disposed;

    public UserBreakOverlayPresenter(
        UserBreakOverlayStateStore? stateStore = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        bool startThread = true)
    {
        _stateStore = stateStore ?? new UserBreakOverlayStateStore();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (startThread)
        {
            EnsureThreadStarted();
        }
    }

    public bool TryPresentPrompt(
        UserInterventionPrompt prompt,
        Action<UserInterventionPromptResponse> onResponse,
        out string errorCode)
    {
        errorCode = string.Empty;
        if (_disposed)
        {
            errorCode = "break_overlay_disposed";
            return false;
        }

        string? breakKind = PortaBreakSemantics.ResolveBreakKind(prompt.TipKey);
        if (breakKind == null)
        {
            errorCode = "break_overlay_unsupported_prompt";
            return false;
        }

        EnsureThreadStarted();
        if (!WaitForReady())
        {
            errorCode = "break_overlay_not_ready";
            return false;
        }

        try
        {
            _dispatcher!.BeginInvoke(new Action(() =>
            {
                EnsureWindowOnCurrentDispatcher();
                if (_activeCountdown != null
                    && string.Equals(_activeCountdown.Countdown.InterventionId, prompt.InterventionId, StringComparison.Ordinal))
                {
                    _activeCountdown.OnResponse = onResponse;
                    _window?.ShowCountdown(_activeCountdown.Countdown, _activeCountdown.Countdown.EndsAtUtc - _utcNowProvider());
                    if (_activeCountdown.RecoveryPending)
                    {
                        _activeCountdown.RecoveryPending = false;
                        onResponse(new UserInterventionPromptResponse(
                            prompt.InterventionId,
                            prompt.TipKey,
                            UserInterventionPromptResponseKind.OverlayRecovered));
                        UserAgentFileLog.Info($"break_overlay_countdown_recovered break_kind={_activeCountdown.Countdown.BreakKind} intervention={prompt.InterventionId}");
                    }

                    return;
                }

                if (_window == null)
                {
                    onResponse(new UserInterventionPromptResponse(
                        prompt.InterventionId,
                        prompt.TipKey,
                        UserInterventionPromptResponseKind.Snoozed));
                    return;
                }

                if (_activePrompt != null || _activeCountdown != null)
                {
                    UserAgentFileLog.Info($"break_overlay_prompt_rejected reason=overlay_active intervention={prompt.InterventionId}");
                    onResponse(new UserInterventionPromptResponse(
                        prompt.InterventionId,
                        prompt.TipKey,
                        UserInterventionPromptResponseKind.Snoozed));
                    return;
                }

                _activePrompt = new ActiveBreakPromptState(prompt, onResponse, breakKind);
                _window.ShowPrompt(prompt, PortaBreakSemantics.ResolveBreakLabel(breakKind));
                UserAgentFileLog.Info($"break_overlay_prompt_shown break_kind={breakKind} intervention={prompt.InterventionId}");
            }), DispatcherPriority.Send);
            return true;
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("break_overlay_dispatch_failed", ex);
            errorCode = "break_overlay_dispatch_failed";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;

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
                        _countdownTimer?.Stop();
                        if (_window != null)
                        {
                            _window.AllowCloseForShutdown();
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
                Name = "PORTA.UserAgent.BreakOverlay"
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

            _countdownTimer = new DispatcherTimer { Interval = CountdownTickInterval };
            _countdownTimer.Tick += (_, _) => OnCountdownTick();
            EnsureWindowOnCurrentDispatcher();
            RestorePersistedCountdownOnCurrentDispatcher();
            _ready.TrySetResult(true);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            UserAgentFileLog.Error("break_overlay_thread_failed", ex);
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

        var window = new UserBreakOverlayWindow(OnOverlayActionRequested);
        window.Closed += (_, _) =>
        {
            if (!_disposed)
            {
                _window = null;
            }
        };
        _window = window;
    }

    private void RestorePersistedCountdownOnCurrentDispatcher()
    {
        UserBreakOverlayCountdownState? restored = _stateStore.LoadActiveCountdown(_utcNowProvider());
        if (restored == null)
        {
            return;
        }

        EnsureWindowOnCurrentDispatcher();
        _activeCountdown = new ActiveBreakCountdownState(restored, onResponse: null, recoveryPending: true);
        _window?.ShowCountdown(restored, restored.EndsAtUtc - _utcNowProvider());
        _countdownTimer?.Start();
        UserAgentFileLog.Info($"break_overlay_countdown_restored break_kind={restored.BreakKind} intervention={restored.InterventionId}");
    }

    private void OnOverlayActionRequested(UserBreakOverlayAction action)
    {
        if (_activePrompt != null)
        {
            HandlePromptAction(action);
            return;
        }

        if (_activeCountdown != null && action == UserBreakOverlayAction.EmergencyExit)
        {
            UserAgentFileLog.Info($"break_overlay_emergency_exit break_kind={_activeCountdown.Countdown.BreakKind} intervention={_activeCountdown.Countdown.InterventionId}");
            _activeCountdown.OnResponse?.Invoke(new UserInterventionPromptResponse(
                _activeCountdown.Countdown.InterventionId,
                _activeCountdown.Countdown.TipKey,
                UserInterventionPromptResponseKind.OverlayAborted));
            ClearActiveOverlay();
        }
    }

    private void HandlePromptAction(UserBreakOverlayAction action)
    {
        ActiveBreakPromptState promptState = _activePrompt!;
        switch (action)
        {
            case UserBreakOverlayAction.Accept:
            {
                DateTimeOffset nowUtc = _utcNowProvider();
                TimeSpan duration = PortaBreakSemantics.ResolveBreakCountdownDuration(promptState.BreakKind);
                var countdown = new UserBreakOverlayCountdownState(
                    InterventionId: promptState.Prompt.InterventionId,
                    TipKey: promptState.Prompt.TipKey,
                    BreakKind: promptState.BreakKind,
                    Title: $"{PortaBreakSemantics.ResolveBreakLabel(promptState.BreakKind)} in progress",
                    Message: promptState.Prompt.Message,
                    AcceptedAtUtc: nowUtc,
                    EndsAtUtc: nowUtc.Add(duration));
                _stateStore.SaveCountdown(countdown);
                _activePrompt = null;
                _activeCountdown = new ActiveBreakCountdownState(countdown, promptState.OnResponse, recoveryPending: false);
                _window?.ShowCountdown(countdown, duration);
                _countdownTimer?.Start();
                promptState.OnResponse(new UserInterventionPromptResponse(
                    promptState.Prompt.InterventionId,
                    promptState.Prompt.TipKey,
                    UserInterventionPromptResponseKind.Accepted));
                UserAgentFileLog.Info($"break_overlay_countdown_started break_kind={promptState.BreakKind} intervention={promptState.Prompt.InterventionId} seconds={(int)duration.TotalSeconds}");
                break;
            }

            case UserBreakOverlayAction.Dismiss:
                _activePrompt = null;
                _window?.HideOverlay();
                promptState.OnResponse(new UserInterventionPromptResponse(
                    promptState.Prompt.InterventionId,
                    promptState.Prompt.TipKey,
                    UserInterventionPromptResponseKind.Skipped));
                break;

            case UserBreakOverlayAction.Snooze:
                _activePrompt = null;
                _window?.HideOverlay();
                promptState.OnResponse(new UserInterventionPromptResponse(
                    promptState.Prompt.InterventionId,
                    promptState.Prompt.TipKey,
                    UserInterventionPromptResponseKind.Snoozed));
                break;

            case UserBreakOverlayAction.Suppress:
                _activePrompt = null;
                _window?.HideOverlay();
                promptState.OnResponse(new UserInterventionPromptResponse(
                    promptState.Prompt.InterventionId,
                    promptState.Prompt.TipKey,
                    UserInterventionPromptResponseKind.SuppressedByPreference));
                break;

            case UserBreakOverlayAction.EmergencyExit:
                _activePrompt = null;
                _window?.HideOverlay();
                promptState.OnResponse(new UserInterventionPromptResponse(
                    promptState.Prompt.InterventionId,
                    promptState.Prompt.TipKey,
                    UserInterventionPromptResponseKind.Skipped));
                break;
        }
    }

    private void OnCountdownTick()
    {
        if (_activeCountdown == null)
        {
            _countdownTimer?.Stop();
            return;
        }

        TimeSpan remaining = _activeCountdown.Countdown.EndsAtUtc - _utcNowProvider();
        if (remaining <= TimeSpan.Zero)
        {
            UserAgentFileLog.Info($"break_overlay_countdown_completed break_kind={_activeCountdown.Countdown.BreakKind} intervention={_activeCountdown.Countdown.InterventionId}");
            _activeCountdown.OnResponse?.Invoke(new UserInterventionPromptResponse(
                _activeCountdown.Countdown.InterventionId,
                _activeCountdown.Countdown.TipKey,
                UserInterventionPromptResponseKind.OverlayCompleted));
            ClearActiveOverlay();
            return;
        }

        _window?.UpdateCountdown(remaining);
    }

    private void ClearActiveOverlay()
    {
        _countdownTimer?.Stop();
        _activePrompt = null;
        _activeCountdown = null;
        _stateStore.Clear();
        _window?.HideOverlay();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock)
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
                if (_activePrompt == null && _activeCountdown == null)
                {
                    return;
                }

                EnsureWindowOnCurrentDispatcher();
                _window?.RestoreAfterSessionUnlock();
                if (_activeCountdown != null)
                {
                    _window?.ShowCountdown(_activeCountdown.Countdown, _activeCountdown.Countdown.EndsAtUtc - _utcNowProvider());
                }
                else if (_activePrompt != null)
                {
                    _window?.ShowPrompt(_activePrompt.Prompt, PortaBreakSemantics.ResolveBreakLabel(_activePrompt.BreakKind));
                }
            }), DispatcherPriority.Send);
        }
        catch
        {
        }
    }

    private sealed record ActiveBreakPromptState(
        UserInterventionPrompt Prompt,
        Action<UserInterventionPromptResponse> OnResponse,
        string BreakKind);

    private sealed class ActiveBreakCountdownState
    {
        public ActiveBreakCountdownState(
            UserBreakOverlayCountdownState countdown,
            Action<UserInterventionPromptResponse>? onResponse,
            bool recoveryPending)
        {
            Countdown = countdown;
            OnResponse = onResponse;
            RecoveryPending = recoveryPending;
        }

        public UserBreakOverlayCountdownState Countdown { get; }

        public Action<UserInterventionPromptResponse>? OnResponse { get; set; }

        public bool RecoveryPending { get; set; }
    }
}
