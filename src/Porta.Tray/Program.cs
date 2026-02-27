using System.Diagnostics;
using System.Reflection;
using Porta.Agent.Common.Contracts;
using Porta.Agent.LocalApi;

namespace Porta.Tray;

static class Program
{
    private const string TrayMutexName = @"Global\PORTA_Tray_SingleInstance";

    [STAThread]
    static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Exception? ex = e.ExceptionObject as Exception;
            TrayFileLog.Error("unhandled_exception", ex);
        };
        Application.ThreadException += (_, e) =>
        {
            TrayFileLog.Error("thread_exception", e.Exception);
        };

        using var mutex = new Mutex(initiallyOwned: true, name: TrayMutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            TrayFileLog.Info("second_instance_exit");
            return;
        }

        TrayFileLog.Info("startup");
        ApplicationConfiguration.Initialize();
        Application.Run(new PortaTrayContext());
    }
}

internal sealed class PortaTrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _lastActionItem;
    private readonly ToolStripMenuItem _actuationItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly LocalApiClient _apiClient = new();
    private readonly RegulationActuationEngine _actuationEngine = new();
    private readonly BreakSuggestionOverlayHost _breakOverlay = new();
    private readonly System.Windows.Forms.Timer _statusTimer;
    private string _statusText = "PORTA: starting";
    private string _lastActionText = "n/a";
    private string _lastActuationText = "Actuation: none";
    private string _lastNotifiedAction = "n/a";
    private double _focusStrength;
    private double _overloadLikelihood;
    private bool _consentPromptedThisSession;
    private int _refreshInFlight;
    private bool _shuttingDown;
    private DateTimeOffset? _manualOverrideUntilUtc;
    private string? _manualOverrideAction;

    public PortaTrayContext()
    {
        _statusItem = new ToolStripMenuItem("PORTA: starting") { Enabled = false };
        _lastActionItem = new ToolStripMenuItem("Last Action: n/a") { Enabled = false };
        _actuationItem = new ToolStripMenuItem("Actuation: none") { Enabled = false };
        _versionItem = new ToolStripMenuItem($"Build: {ResolveBuildVersion()}") { Enabled = false };
        var diagnosticsMenu = new ToolStripMenuItem("Diagnostics");
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Inform (15s)", null, (_, _) => _ = StartManualOverrideAsync("inform_overload_risk")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Suggest (15s)", null, (_, _) => _ = StartManualOverrideAsync("break_suggestion")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Nudge (15s)", null, (_, _) => _ = StartManualOverrideAsync("focus_mode_enabled")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Focus Suggest (15s)", null, (_, _) => _ = StartManualOverrideAsync("focus_lock_in_suggestion")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Focus Nudge (15s)", null, (_, _) => _ = StartManualOverrideAsync("focus_assist_enabled")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Fatigue Suggest (15s)", null, (_, _) => _ = StartManualOverrideAsync("stretch_break_suggestion")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Fatigue Nudge (15s)", null, (_, _) => _ = StartManualOverrideAsync("fatigue_relief_enabled")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Test Restrict (15s)", null, (_, _) => _ = StartManualOverrideAsync("temporary_app_restriction_enabled")));
        diagnosticsMenu.DropDownItems.Add(new ToolStripSeparator());
        diagnosticsMenu.DropDownItems.Add(new ToolStripMenuItem("Clear Test Override", null, (_, _) => ClearManualOverride()));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastActionItem);
        menu.Items.Add(_actuationItem);
        menu.Items.Add(_versionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Control Center", null, (_, _) => OpenControlCenter()));
        menu.Items.Add(new ToolStripMenuItem("Pause", null, (_, _) => _ = RunSafeAsync(() => _apiClient.PauseAsync("tray_pause"))));
        menu.Items.Add(new ToolStripMenuItem("Resume", null, (_, _) => _ = RunSafeAsync(() => _apiClient.ResumeAsync())));
        menu.Items.Add(new ToolStripMenuItem("Privacy", null, (_, _) => ShowPrivacy()));
        menu.Items.Add(diagnosticsMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit Tray", null, (_, _) => ExitThread()));

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Visible = true,
            Text = "PORTA Tray",
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenControlCenter();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMiniPopover();
            }
        };

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => _ = SafeRefreshStatusAsync();
        _statusTimer.Start();
        _ = SafeRefreshStatusAsync();
    }

    protected override void ExitThreadCore()
    {
        _shuttingDown = true;
        TrayFileLog.Info("shutdown");
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _breakOverlay.Dispose();
        _actuationEngine.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _apiClient.Dispose();
        base.ExitThreadCore();
    }

    private async Task SafeRefreshStatusAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TrayFileLog.Error("safe_refresh_failed", ex);
            _statusText = "PORTA: error";
            _lastActionText = "refresh_failed";
            _lastActuationText = "Actuation: unavailable";
            ApplyStatusOnUiThread();
            if (!_shuttingDown)
            {
                RunOnUiThread(() => _notifyIcon.ShowBalloonTip(
                    3500,
                    "PORTA Tray Error",
                    $"Non-fatal tray error: {ex.GetType().Name}",
                    ToolTipIcon.Warning));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_manualOverrideUntilUtc.HasValue && DateTimeOffset.UtcNow >= _manualOverrideUntilUtc.Value)
        {
            ClearManualOverride();
        }

        LocalApiStatusResponse? status = await _apiClient.GetStatusAsync().ConfigureAwait(true);
        LocalApiCurrentSnapshotResponse? snapshot = status != null
            ? await _apiClient.GetCurrentSnapshotAsync().ConfigureAwait(true)
            : null;
        LocalApiSettingsResponse? settings = status != null
            ? await _apiClient.GetEffectiveSettingsAsync().ConfigureAwait(true)
            : null;

        if (_manualOverrideUntilUtc.HasValue && !string.IsNullOrWhiteSpace(_manualOverrideAction))
        {
            LocalApiStatusResponse simulatedStatus = BuildSimulatedStatus(status, _manualOverrideAction);
            LocalApiSettingsResponse simulatedSettings = BuildSimulatedSettings(settings);
            _statusText = "PORTA: running (demo)";
            _focusStrength = simulatedStatus.FocusStrength;
            _overloadLikelihood = simulatedStatus.OverloadLikelihood;
            string previousAction = _lastActionText;
            _lastActionText = $"{_manualOverrideAction} (demo)";
            MaybeNotifyRegulationAction(previousAction, _manualOverrideAction, simulatedStatus, snapshot);
            _actuationEngine.Apply(simulatedStatus, simulatedSettings);
            _lastActuationText = _actuationEngine.LastSummary;
            ApplyStatusOnUiThread();
            return;
        }

        _statusText = status switch
        {
            null => "PORTA: offline",
            { ConsentActive: false } => "PORTA: needs consent",
            { Paused: true } => "PORTA: paused",
            _ => "PORTA: running"
        };
        if (status != null)
        {
            _focusStrength = status.FocusStrength;
            _overloadLikelihood = status.OverloadLikelihood;
            string previousAction = _lastActionText;
            _lastActionText = status.LastAction;
            MaybeNotifyRegulationAction(previousAction, _lastActionText, status, snapshot);
            _actuationEngine.Apply(status, settings);
            _lastActuationText = _actuationEngine.LastSummary;
            if (!status.ConsentActive && !_consentPromptedThisSession)
            {
                _consentPromptedThisSession = true;
                OpenControlCenter();
                RunOnUiThread(() => _notifyIcon.ShowBalloonTip(
                    3500,
                    "PORTA Needs Consent",
                    "Open Control Center and click I Agree to enable collection and inference.",
                    ToolTipIcon.Info));
            }
        }
        else
        {
            _focusStrength = 0;
            _overloadLikelihood = 0;
            _lastActionText = "offline";
            _actuationEngine.Reset("Actuation: offline");
            _lastActuationText = _actuationEngine.LastSummary;
        }

        ApplyStatusOnUiThread();
    }

    private void MaybeNotifyRegulationAction(
        string previousAction,
        string currentAction,
        LocalApiStatusResponse status,
        LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (string.IsNullOrWhiteSpace(currentAction)
            || string.Equals(currentAction, previousAction, StringComparison.Ordinal)
            || string.Equals(currentAction, _lastNotifiedAction, StringComparison.Ordinal))
        {
            return;
        }

        double overload = snapshot?.OverloadLikelihood ?? status.OverloadLikelihood;
        double focus = snapshot?.FocusStrength ?? status.FocusStrength;
        double fatigue = snapshot?.FatigueProbability ?? 0d;
        string? title = null;
        string? body = null;
        ToolTipIcon icon = ToolTipIcon.Info;
        switch (currentAction)
        {
            case "inform_overload_risk":
                // Inform level is log-only by policy; no user toast.
                break;
            case "inform_focus_protection":
                // Inform level is log-only by policy; no user toast.
                break;
            case "inform_fatigue_prevention":
                // Inform level is log-only by policy; no user toast.
                break;
            case "break_suggestion":
                title = "PORTA Suggest";
                body = "Break suggestion (2 min) - recover before overload rises.";
                icon = ToolTipIcon.Info;
                RunOnUiThread(() => _breakOverlay.Show("Break Suggestion", "Take a 2-minute recovery break.", TimeSpan.FromMinutes(2)));
                break;
            case "stretch_break_suggestion":
                title = "PORTA Suggest";
                body = "Stretch break suggestion (2 min) - fatigue prevention.";
                icon = ToolTipIcon.Info;
                RunOnUiThread(() => _breakOverlay.Show("Stretch Break", "Take a 2-minute stretch break.", TimeSpan.FromMinutes(2)));
                break;
            case "focus_lock_in_suggestion":
                title = "PORTA Suggest";
                body = $"Focus lock-in suggestion triggered (focus protection).";
                icon = ToolTipIcon.Info;
                break;
            case "focus_mode_enabled":
                title = "PORTA Nudge";
                body = $"Focus Assist enabled (10 min) - overload {overload:F2}";
                icon = ToolTipIcon.Info;
                break;
            case "focus_assist_enabled":
                title = "PORTA Nudge";
                body = $"Focus Assist enabled (10 min) - focus {focus:F2}";
                icon = ToolTipIcon.Info;
                break;
            case "fatigue_relief_enabled":
                title = "PORTA Nudge";
                body = $"Fatigue relief enabled (10 min) - fatigue {fatigue:F2}";
                icon = ToolTipIcon.Info;
                break;
            case "temporary_app_restriction_enabled":
                title = "PORTA Restrict";
                body = $"Temporary restriction enabled (3 min) - overload {overload:F2}";
                icon = ToolTipIcon.Warning;
                break;
        }

        if (currentAction is not "break_suggestion" and not "stretch_break_suggestion")
        {
            RunOnUiThread(() => _breakOverlay.Hide());
        }

        if (title == null || body == null)
        {
            return;
        }

        _lastNotifiedAction = currentAction;
        RunOnUiThread(() => _notifyIcon.ShowBalloonTip(3500, title, body, icon));
    }

    private void ApplyStatusText(string text, string lastAction, string actuation)
    {
        _statusItem.Text = text;
        _lastActionItem.Text = $"Last Action: {lastAction}";
        _actuationItem.Text = actuation;
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowMiniPopover()
    {
        string message =
            $"Status: {_statusText}\r\n"
            + $"Focus: {_focusStrength:F2}\r\n"
            + $"Overload: {_overloadLikelihood:F2}\r\n"
            + $"Last Action: {_lastActionText}\r\n"
            + $"{_lastActuationText}";
        _notifyIcon.ShowBalloonTip(2500, "PORTA", message, ToolTipIcon.Info);
    }

    private static void ShowPrivacy()
    {
        MessageBox.Show(
            "PORTA privacy mode:\r\n"
            + "- Collected: aggregated interaction/system metrics only.\r\n"
            + "- Never collected: typed text, clipboard, file contents, screenshots.",
            "PORTA Privacy",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void OpenControlCenter()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Porta.ControlCenter.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show(
                "Control Center executable was not found next to tray app.",
                "PORTA Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    private async Task StartManualOverrideAsync(string action)
    {
        try
        {
            LocalApiAckResponse? ack = await _apiClient.TriggerTestInterventionAsync(action).ConfigureAwait(true);
            if (ack is { Success: false })
            {
                RunOnUiThread(() => _notifyIcon.ShowBalloonTip(
                    3000,
                    "PORTA Diagnostics",
                    $"Test trigger blocked: {ack.Message}",
                    ToolTipIcon.Warning));
                return;
            }

            bool trayOnlyFallback = ack == null;
            _manualOverrideAction = action;
            _manualOverrideUntilUtc = DateTimeOffset.UtcNow.AddSeconds(15);
            RunOnUiThread(() => _notifyIcon.ShowBalloonTip(
                2500,
                "PORTA Diagnostics",
                trayOnlyFallback
                    ? $"Tray-only demo: {action} active for 15 seconds (service API unavailable)."
                    : $"Manual test override: {action} active for 15 seconds.",
                ToolTipIcon.Info));
            await SafeRefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TrayFileLog.Error("manual_override_failed", ex);
            RunOnUiThread(() => _notifyIcon.ShowBalloonTip(
                3000,
                "PORTA Diagnostics",
                $"Test override failed: {ex.GetType().Name}",
                ToolTipIcon.Warning));
        }
    }

    private void ClearManualOverride()
    {
        _manualOverrideAction = null;
        _manualOverrideUntilUtc = null;
    }

    private void ApplyStatusOnUiThread()
    {
        if (_notifyIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            _notifyIcon.ContextMenuStrip.BeginInvoke(new Action(() => ApplyStatusText(_statusText, _lastActionText, _lastActuationText)));
            return;
        }

        ApplyStatusText(_statusText, _lastActionText, _lastActuationText);
    }

    private void RunOnUiThread(Action action)
    {
        if (_notifyIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            _notifyIcon.ContextMenuStrip.BeginInvoke(action);
            return;
        }

        action();
    }

    private static string ResolveBuildVersion()
    {
        string? version =
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }

    private static LocalApiStatusResponse BuildSimulatedStatus(LocalApiStatusResponse? baseline, string action)
    {
        LocalApiStatusResponse source = baseline ?? new LocalApiStatusResponse(
            Status: AgentStatus.Running,
            ServiceRunning: true,
            Paused: false,
            ConsentActive: true,
            ConsentVersion: "v1",
            ConsentTimestampUtc: DateTimeOffset.UtcNow,
            ConsentDisclosureHash: "demo",
            BackendConnected: false,
            FocusStrength: 0.52,
            OverloadLikelihood: 0.89,
            LastAction: action,
            AgentVersion: ResolveBuildVersion(),
            SchemaVersion: "v1",
            PolicyVersion: "local",
            LastEventTimestampUtc: DateTimeOffset.UtcNow,
            LastError: null,
            LastUpdatedUtc: DateTimeOffset.UtcNow);

        return source with
        {
            Status = AgentStatus.Running,
            ServiceRunning = true,
            Paused = false,
            ConsentActive = true,
            FocusStrength = action is "focus_assist_enabled" or "focus_lock_in_suggestion" or "inform_focus_protection" ? 0.88 : 0.55,
            OverloadLikelihood = action switch
            {
                "temporary_app_restriction_enabled" => 0.95,
                "focus_assist_enabled" => 0.35,
                "focus_lock_in_suggestion" => 0.38,
                "inform_focus_protection" => 0.40,
                "fatigue_relief_enabled" => 0.32,
                "stretch_break_suggestion" => 0.34,
                "inform_fatigue_prevention" => 0.36,
                _ => 0.86
            },
            LastAction = action,
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
    }

    private static LocalApiSettingsResponse BuildSimulatedSettings(LocalApiSettingsResponse? baseline)
    {
        LocalApiSettingsResponse source = baseline ?? new LocalApiSettingsResponse(
            RegulationIntensity: "Strong",
            PrivacyLevel: "Standard",
            NotificationReductionEnabled: true,
            FocusModeEnabled: true,
            AnimationReductionEnabled: true,
            TemporaryAppRestrictionEnabled: true,
            BaselineRecalibrationActive: false,
            BaselineRecalibrationStartedUtc: null,
            BaselineRecalibrationUntilUtc: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        return source with
        {
            RegulationIntensity = "Strong",
            NotificationReductionEnabled = true,
            FocusModeEnabled = true,
            AnimationReductionEnabled = true,
            TemporaryAppRestrictionEnabled = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task RunSafeAsync(Func<Task<bool>> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
            await SafeRefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (_shuttingDown)
            {
                return;
            }

            TrayFileLog.Error("tray_action_failed", ex);
            RunOnUiThread(() => _notifyIcon.ShowBalloonTip(
                2500,
                "PORTA Tray Error",
                $"Action failed: {ex.GetType().Name}",
                ToolTipIcon.Warning));
        }
    }
}

internal static class TrayFileLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = BuildPath();

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Error(string message, Exception? ex)
    {
        Write("ERROR", message, ex);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string line = $"[{DateTimeOffset.UtcNow:O}] [{level}] {message}";
                if (ex != null)
                {
                    line += $" :: {ex.GetType().Name}: {ex.Message}";
                }

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private static string BuildPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "tray.log");
    }
}
