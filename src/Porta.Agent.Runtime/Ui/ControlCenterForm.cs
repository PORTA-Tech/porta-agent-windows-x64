using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Forms;

namespace Porta.Agent.Runtime.Ui;

internal sealed class ControlCenterForm : Form
{
    private readonly Label _modeLabel;
    private readonly Label _agentProcessLabel;
    private readonly Label _serviceLabel;
    private readonly Label _consentLabel;
    private readonly Label _logDbLabel;
    private readonly Label _lastUpdatedLabel;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly string _dataDir;
    private readonly string _consentFile;
    private readonly string _logDbFile;

    public ControlCenterForm(bool alreadyRunning)
    {
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Porta", "Layer1");
        _consentFile = Path.Combine(_dataDir, "consent-state.json");
        _logDbFile = Path.Combine(_dataDir, "local-logs.db");

        Text = "Ari Agent Control Center";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 700;
        Height = 420;
        MinimizeBox = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(12),
            AutoSize = true
        };

        _modeLabel = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = alreadyRunning
                ? "Ari Agent is already running. This window is your user UI."
                : "Ari Agent started. This window is your user UI."
        };

        _agentProcessLabel = NewStatusLabel("Agent process: checking...");
        _serviceLabel = NewStatusLabel("Windows service: checking...");
        _consentLabel = NewStatusLabel("Consent state: checking...");
        _logDbLabel = NewStatusLabel("Local logs: checking...");
        _lastUpdatedLabel = NewStatusLabel("Last updated: n/a");

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        var refreshButton = new Button { Text = "Refresh", AutoSize = true };
        refreshButton.Click += (_, _) => RefreshStatus();

        var openDashboardButton = new Button { Text = "Open Dashboard", AutoSize = true };
        openDashboardButton.Click += (_, _) => OpenDashboard();

        var openDataFolderButton = new Button { Text = "Open Data Folder", AutoSize = true };
        openDataFolderButton.Click += (_, _) => OpenDataFolder();

        var closeButton = new Button { Text = "Close Window", AutoSize = true };
        closeButton.Click += (_, _) => Close();

        buttonRow.Controls.Add(refreshButton);
        buttonRow.Controls.Add(openDashboardButton);
        buttonRow.Controls.Add(openDataFolderButton);
        buttonRow.Controls.Add(closeButton);

        var infoText = new Label
        {
            AutoSize = true,
            Text =
                "If tray icon is hidden, click ^ near the clock.\r\n"
                + "Right-click PORTA tray icon for live status, pause/resume, exports, and disclosure."
        };

        root.Controls.Add(_modeLabel);
        root.Controls.Add(_agentProcessLabel);
        root.Controls.Add(_serviceLabel);
        root.Controls.Add(_consentLabel);
        root.Controls.Add(_logDbLabel);
        root.Controls.Add(_lastUpdatedLabel);
        root.Controls.Add(buttonRow);
        root.Controls.Add(infoText);

        Controls.Add(root);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();

        FormClosed += (_, _) => _refreshTimer.Dispose();
        Shown += (_, _) => RefreshStatus();
    }

    private static Label NewStatusLabel(string text)
    {
        return new Label
        {
            AutoSize = true,
            Text = text
        };
    }

    private void RefreshStatus()
    {
        int trayCount = Process.GetProcessesByName("AriTray").Length;
        _agentProcessLabel.Text = trayCount > 0
            ? $"Agent process: running ({trayCount})"
            : "Agent process: not running";

        _serviceLabel.Text = $"Windows service (AriAgent): {ReadServiceStatus()}";
        _consentLabel.Text = $"Consent state: {ReadConsentState()}";
        _logDbLabel.Text = $"Local logs: {ReadLogDbStatus()}";
        _lastUpdatedLabel.Text = $"Last updated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}";
    }

    private static string ReadServiceStatus()
    {
        try
        {
            using var service = new ServiceController("AriAgent");
            return service.Status.ToString();
        }
        catch
        {
            return "NotInstalledOrInaccessible";
        }
    }

    private string ReadConsentState()
    {
        try
        {
            if (!File.Exists(_consentFile))
            {
                return $"Missing ({_consentFile})";
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_consentFile));
            if (doc.RootElement.TryGetProperty("state", out JsonElement state))
            {
                return state.GetString() ?? "Unknown";
            }

            return "Unknown";
        }
        catch (Exception ex)
        {
            return $"Error ({ex.GetType().Name})";
        }
    }

    private string ReadLogDbStatus()
    {
        try
        {
            if (!File.Exists(_logDbFile))
            {
                return $"Missing ({_logDbFile})";
            }

            var info = new FileInfo(_logDbFile);
            return $"{info.Length} bytes, last write {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            return $"Error ({ex.GetType().Name})";
        }
    }

    private static void OpenDashboard()
    {
        string url = Environment.GetEnvironmentVariable("PORTA_CONSOLE_URL") ?? "http://localhost:3000";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            Process.Start(new ProcessStartInfo(_dataDir) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
