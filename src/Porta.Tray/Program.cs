using System.Diagnostics;
using Porta.Agent.LocalApi;

namespace Porta.Tray;

static class Program
{
    private const string TrayMutexName = @"Global\PORTA_Tray_SingleInstance";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: TrayMutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new PortaTrayContext());
    }
}

internal sealed class PortaTrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _lastActionItem;
    private readonly LocalApiClient _apiClient = new();
    private readonly System.Windows.Forms.Timer _statusTimer;
    private string _statusText = "PORTA: starting";
    private string _lastActionText = "n/a";
    private double _focusStrength;
    private double _overloadLikelihood;
    private bool _consentPromptedThisSession;

    public PortaTrayContext()
    {
        _statusItem = new ToolStripMenuItem("PORTA: starting") { Enabled = false };
        _lastActionItem = new ToolStripMenuItem("Last Action: n/a") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastActionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Control Center", null, (_, _) => OpenControlCenter()));
        menu.Items.Add(new ToolStripMenuItem("Pause", null, async (_, _) => await _apiClient.PauseAsync("tray_pause").ConfigureAwait(false)));
        menu.Items.Add(new ToolStripMenuItem("Resume", null, async (_, _) => await _apiClient.ResumeAsync().ConfigureAwait(false)));
        menu.Items.Add(new ToolStripMenuItem("Privacy", null, (_, _) => ShowPrivacy()));
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

        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync().ConfigureAwait(false);
        _statusTimer.Start();
        _ = RefreshStatusAsync();
    }

    protected override void ExitThreadCore()
    {
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _apiClient.Dispose();
        base.ExitThreadCore();
    }

    private async Task RefreshStatusAsync()
    {
        LocalApiStatusResponse? status = await _apiClient.GetStatusAsync().ConfigureAwait(false);
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
            _lastActionText = status.LastAction;
            if (!status.ConsentActive && !_consentPromptedThisSession)
            {
                _consentPromptedThisSession = true;
                OpenControlCenter();
                _notifyIcon.ShowBalloonTip(
                    3500,
                    "PORTA Needs Consent",
                    "Open Control Center and click I Agree to enable collection and inference.",
                    ToolTipIcon.Info);
            }
        }
        else
        {
            _focusStrength = 0;
            _overloadLikelihood = 0;
            _lastActionText = "offline";
        }

        if (_notifyIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            _notifyIcon.ContextMenuStrip.BeginInvoke(new Action(() => ApplyStatusText(_statusText, _lastActionText)));
            return;
        }

        ApplyStatusText(_statusText, _lastActionText);
    }

    private void ApplyStatusText(string text, string lastAction)
    {
        _statusItem.Text = text;
        _lastActionItem.Text = $"Last Action: {lastAction}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowMiniPopover()
    {
        string message =
            $"Status: {_statusText}\r\n"
            + $"Focus: {_focusStrength:F2}\r\n"
            + $"Overload: {_overloadLikelihood:F2}\r\n"
            + $"Last Action: {_lastActionText}";
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
}
