using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Runtime.Layer1;

internal sealed class Layer1TrayController : IDisposable
{
    // Step 17 guardrail:
    // Tray is visual-only and must never perform OS theme/input/app manipulation.
    private readonly ManualResetEventSlim _started = new(false);
    private readonly object _stateGate = new();
    private Thread? _thread;
    private TrayContext? _context;
    private Layer1TraySnapshot _snapshot = new(
        Status: "Starting",
        ConsentState: Layer1ConsentState.InstalledNotConsented,
        LastSignalAtUtc: null,
        Summary: "No signals emitted yet.",
        CanPause: false,
        PauseRemainingSeconds: null,
        RiskProbabilities: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        RiskSigma: 0,
        RecentPrimitives: Array.Empty<Layer1TrayPrimitiveView>(),
        RecentInterventions: Array.Empty<Layer1TrayInterventionView>(),
        OverloadActive: false,
        DeepWorkActive: false);
    private int _disposed;

    public event Action<int>? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? RevokeRequested;
    public event Action? ExitRequested;
    public event Func<string>? ExportJsonRequested;
    public event Func<string>? ExportCsvRequested;

    public bool Start()
    {
        if (_thread != null)
        {
            return true;
        }

        _thread = new Thread(TrayThreadMain)
        {
            Name = "Layer1TrayThread",
            IsBackground = true
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return _started.Wait(TimeSpan.FromSeconds(5));
    }

    public void Update(Layer1TraySnapshot snapshot)
    {
        lock (_stateGate)
        {
            _snapshot = snapshot;
        }

        _context?.UpdateFromController();
    }

    private void TrayThreadMain()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        _context = new TrayContext(this);
        _started.Set();
        Application.Run(_context);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _context?.RequestExit();
        if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _thread.Interrupt();
        }
        _started.Dispose();
    }

    private sealed class TrayContext : ApplicationContext
    {
        private enum TrayVisualMode
        {
            Normal,
            DeepWork,
            Overload
        }

        private static readonly int[] PausePresetsMinutes = [1, 5, 10, 15, 30, 60];
        private static readonly string[] RiskOrder = ["stress", "overload", "focus", "fatigue"];
        private static readonly IReadOnlyDictionary<TrayVisualMode, Icon> ModeIconMap =
            new Dictionary<TrayVisualMode, Icon>
            {
                [TrayVisualMode.Normal] = SystemIcons.Information,
                [TrayVisualMode.DeepWork] = SystemIcons.Shield,
                [TrayVisualMode.Overload] = SystemIcons.Warning
            };
        private readonly Layer1TrayController _owner;
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _lastSignalItem;
        private readonly ToolStripMenuItem _consentItem;
        private readonly ToolStripMenuItem _riskItem;
        private readonly ToolStripMenuItem _pauseCountdownItem;
        private readonly ToolStripMenuItem _pauseResumeItem;
        private readonly ToolStripMenuItem _recentInterventionsItem;
        private readonly ToolStripMenuItem _recentPrimitivesItem;
        private readonly System.Windows.Forms.Timer _menuRefreshTimer;
        private readonly System.Windows.Forms.Timer _tooltipTimer;
        private TrayVisualMode? _lastVisualMode;
        private DateTimeOffset? _stressBreachSinceUtc;
        private DateTimeOffset? _overloadBreachSinceUtc;
        private DateTimeOffset? _fatigueBreachSinceUtc;
        private DateTimeOffset _lastStressAlertUtc;
        private DateTimeOffset _lastOverloadAlertUtc;
        private DateTimeOffset _lastFatigueAlertUtc;

        public TrayContext(Layer1TrayController owner)
        {
            _owner = owner;
            _statusItem = new ToolStripMenuItem("Status: Starting") { Enabled = false };
            _lastSignalItem = new ToolStripMenuItem("Last signal: n/a") { Enabled = false };
            _consentItem = new ToolStripMenuItem("Consent: installed_not_consented") { Enabled = false };
            _riskItem = new ToolStripMenuItem("Risk: n/a") { Enabled = false };
            _pauseCountdownItem = new ToolStripMenuItem("Pause remaining: n/a") { Enabled = false };
            _pauseResumeItem = new ToolStripMenuItem("Pause / Resume");
            _recentInterventionsItem = new ToolStripMenuItem("Recent Interventions");
            _recentPrimitivesItem = new ToolStripMenuItem("Last Event Snapshot");
            var viewSummaryItem = new ToolStripMenuItem("View Data Collected", null, (_, _) =>
            {
                MessageBox.Show(_owner.ReadSummary(), "Layer 1 Data Summary", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            var exportJsonItem = new ToolStripMenuItem("Export Logs (JSON)", null, (_, _) =>
            {
                string path = _owner.ExportJsonRequested?.Invoke() ?? "Export failed";
                MessageBox.Show(path, "PORTA Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            var exportCsvItem = new ToolStripMenuItem("Export Logs (CSV)", null, (_, _) =>
            {
                string path = _owner.ExportCsvRequested?.Invoke() ?? "Export failed";
                MessageBox.Show(path, "PORTA Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            var dashboardItem = new ToolStripMenuItem("Open Dashboard", null, (_, _) => OpenDashboard());
            var disclosureItem = new ToolStripMenuItem("Open Disclosure", null, (_, _) =>
            {
                MessageBox.Show(Layer1Disclosure.Text, "Layer 1 Disclosure", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            var settingsItem = new ToolStripMenuItem("Settings / Help", null, (_, _) =>
            {
                MessageBox.Show(
                    "PORTA Tray Settings\r\n\r\n"
                    + "Pause duration: 1-60 minutes.\r\n"
                    + "Alerts: stress > 0.90 for 30s, overload > 0.85 for 60s, fatigue > 0.75 for 30s.\r\n"
                    + "Data retention: local summary only.",
                    "PORTA Settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            });
            var revokeItem = new ToolStripMenuItem("Revoke Consent", null, (_, _) => _owner.RevokeRequested?.Invoke());
            var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => _owner.ExitRequested?.Invoke());
            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(_lastSignalItem);
            menu.Items.Add(_consentItem);
            menu.Items.Add(_riskItem);
            menu.Items.Add(_pauseCountdownItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_pauseResumeItem);
            menu.Items.Add(_recentInterventionsItem);
            menu.Items.Add(_recentPrimitivesItem);
            menu.Items.Add(viewSummaryItem);
            menu.Items.Add(exportJsonItem);
            menu.Items.Add(exportCsvItem);
            menu.Items.Add(dashboardItem);
            menu.Items.Add(disclosureItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(revokeItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _icon = new NotifyIcon
            {
                Text = "PORTA Layer1",
                Icon = SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = menu
            };

            _menuRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _menuRefreshTimer.Tick += (_, _) => RefreshUi();
            _menuRefreshTimer.Start();
            _tooltipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _tooltipTimer.Tick += (_, _) => UpdateTooltip();
            _tooltipTimer.Start();
            UpdateFromController();
        }

        public void UpdateFromController()
        {
            if (_icon.ContextMenuStrip?.InvokeRequired == true)
            {
                _icon.ContextMenuStrip.BeginInvoke(new Action(UpdateFromController));
                return;
            }

            RefreshUi();
            UpdateTooltip();
        }

        public void RequestExit()
        {
            if (_icon.ContextMenuStrip?.InvokeRequired == true)
            {
                _icon.ContextMenuStrip.BeginInvoke(new Action(RequestExit));
                return;
            }

            _menuRefreshTimer.Stop();
            _tooltipTimer.Stop();
            _menuRefreshTimer.Dispose();
            _tooltipTimer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }

        private void RefreshUi()
        {
            Layer1TraySnapshot snapshot = _owner.ReadSnapshot();
            _statusItem.Text = $"Status: {snapshot.Status}";
            _lastSignalItem.Text = $"Last signal: {FormatTime(snapshot.LastSignalAtUtc)}";
            _consentItem.Text = $"Consent: {snapshot.ConsentState.ToString().ToLowerInvariant()}";
            _riskItem.Text = $"Risk: {FormatRisk(snapshot)}";
            _pauseCountdownItem.Text = $"Pause remaining: {FormatPauseRemaining(snapshot.PauseRemainingSeconds)}";
            _pauseResumeItem.DropDownItems.Clear();
            if (snapshot.CanPause)
            {
                foreach (int minutes in PausePresetsMinutes)
                {
                    var item = new ToolStripMenuItem($"Pause {minutes} min");
                    item.Click += (_, _) => _owner.PauseRequested?.Invoke(minutes);
                    _pauseResumeItem.DropDownItems.Add(item);
                }
            }
            else
            {
                var resume = new ToolStripMenuItem("Resume");
                resume.Click += (_, _) => _owner.ResumeRequested?.Invoke();
                _pauseResumeItem.DropDownItems.Add(resume);
            }

            UpdateRecentInterventions(snapshot.RecentInterventions);
            UpdateRecentPrimitives(snapshot.RecentPrimitives);
            TrayVisualMode mode = ResolveVisualMode(snapshot);
            if (_lastVisualMode != mode)
            {
                _icon.Icon = ModeIconMap[mode];
                _lastVisualMode = mode;
            }
            MaybeShowAlerts(snapshot);
        }

        private void UpdateRecentInterventions(IReadOnlyList<Layer1TrayInterventionView> interventions)
        {
            _recentInterventionsItem.DropDownItems.Clear();
            if (interventions.Count == 0)
            {
                _recentInterventionsItem.DropDownItems.Add(new ToolStripMenuItem("No interventions yet") { Enabled = false });
                return;
            }

            foreach (Layer1TrayInterventionView intervention in interventions.Take(5))
            {
                string line = $"{intervention.ActionType} ({intervention.Confidence:F2})";
                var item = new ToolStripMenuItem(line);
                item.Click += (_, _) => ShowInterventionDrilldown(intervention);
                item.ToolTipText =
                    $"{intervention.StartTimeUtc:O} -> {intervention.EndTimeUtc:O}\r\n"
                    + $"Policy: {intervention.PolicyId} v{intervention.PolicyVersion}\r\n"
                    + $"Signature verified: {intervention.SignatureVerified}\r\n"
                    + $"Correlation: {intervention.CorrelationId}\r\n"
                    + intervention.Reason;
                _recentInterventionsItem.DropDownItems.Add(item);
            }
        }

        private void UpdateRecentPrimitives(IReadOnlyList<Layer1TrayPrimitiveView> primitives)
        {
            _recentPrimitivesItem.DropDownItems.Clear();
            if (primitives.Count == 0)
            {
                _recentPrimitivesItem.DropDownItems.Add(new ToolStripMenuItem("No primitives yet") { Enabled = false });
                return;
            }

            foreach (Layer1TrayPrimitiveView primitive in primitives.Take(5))
            {
                string label = $"{primitive.Name}: {primitive.Value:F2} {primitive.Unit}";
                var item = new ToolStripMenuItem(label);
                item.ToolTipText = $"{primitive.TimestampUtc:O} | confidence {primitive.Confidence:F2}";
                _recentPrimitivesItem.DropDownItems.Add(item);
            }
        }

        private void MaybeShowAlerts(Layer1TraySnapshot snapshot)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            double stress = ReadRisk(snapshot, "stress");
            double overload = ReadRisk(snapshot, "overload");
            double fatigue = ReadRisk(snapshot, "fatigue");
            TrackBreach(stress > 0.90, ref _stressBreachSinceUtc, now);
            TrackBreach(overload > 0.85, ref _overloadBreachSinceUtc, now);
            TrackBreach(fatigue > 0.75, ref _fatigueBreachSinceUtc, now);

            if (_stressBreachSinceUtc.HasValue
                && (now - _stressBreachSinceUtc.Value) >= TimeSpan.FromSeconds(30)
                && now - _lastStressAlertUtc > TimeSpan.FromSeconds(30))
            {
                _icon.ShowBalloonTip(3000, "PORTA Alert", "Stress likelihood exceeded 0.90 for over 30s.", ToolTipIcon.Warning);
                _lastStressAlertUtc = now;
            }
            if (_overloadBreachSinceUtc.HasValue
                && (now - _overloadBreachSinceUtc.Value) >= TimeSpan.FromMinutes(1)
                && now - _lastOverloadAlertUtc > TimeSpan.FromSeconds(30))
            {
                _icon.ShowBalloonTip(3000, "PORTA Alert", "Overload likelihood exceeded 0.85 for over 60s.", ToolTipIcon.Warning);
                _lastOverloadAlertUtc = now;
            }
            if (_fatigueBreachSinceUtc.HasValue
                && (now - _fatigueBreachSinceUtc.Value) >= TimeSpan.FromSeconds(30)
                && now - _lastFatigueAlertUtc > TimeSpan.FromSeconds(30))
            {
                _icon.ShowBalloonTip(3000, "PORTA Alert", "Fatigue probability exceeded 0.75 for over 30s.", ToolTipIcon.Info);
                _lastFatigueAlertUtc = now;
            }
        }

        private void UpdateTooltip()
        {
            Layer1TraySnapshot snapshot = _owner.ReadSnapshot();
            string status = snapshot.Status;
            string lastSignal = FormatTime(snapshot.LastSignalAtUtc);
            string tooltip =
                $"PORTA {status} | "
                + $"Consent:{snapshot.ConsentState.ToString().ToLowerInvariant()} | "
                + $"Last:{lastSignal} | "
                + $"Risk: {FormatRisk(snapshot)}";
            _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        }

        private static TrayVisualMode ResolveVisualMode(Layer1TraySnapshot snapshot)
        {
            PresentedVisualMode mode = PresentedVisualModeMapper.Map(snapshot.OverloadActive, snapshot.DeepWorkActive);
            return mode switch
            {
                PresentedVisualMode.OVERLOAD => TrayVisualMode.Overload,
                PresentedVisualMode.DEEP_WORK => TrayVisualMode.DeepWork,
                _ => TrayVisualMode.Normal
            };
        }

        private static string FormatTime(DateTimeOffset? time) => time.HasValue ? time.Value.ToString("O") : "n/a";

        private static string FormatPauseRemaining(int? remainingSeconds)
        {
            if (!remainingSeconds.HasValue || remainingSeconds.Value <= 0)
            {
                return "n/a";
            }

            int minutes = (int)Math.Ceiling(remainingSeconds.Value / 60.0);
            return $"{minutes} min";
        }

        private static double ReadRisk(Layer1TraySnapshot snapshot, string key)
        {
            return snapshot.RiskProbabilities.TryGetValue(key, out double value) ? value : 0;
        }

        private static string FormatRisk(Layer1TraySnapshot snapshot)
        {
            if (snapshot.RiskProbabilities.Count == 0)
            {
                return "n/a";
            }

            return string.Join(", ", RiskOrder.Select(risk => $"{risk}:{ReadRisk(snapshot, risk):F2}")) + $" +/-{snapshot.RiskSigma:F2}";
        }

        private static void TrackBreach(bool breached, ref DateTimeOffset? breachedSinceUtc, DateTimeOffset now)
        {
            if (breached)
            {
                breachedSinceUtc ??= now;
                return;
            }

            breachedSinceUtc = null;
        }

        private static void ShowInterventionDrilldown(Layer1TrayInterventionView intervention)
        {
            string text =
                $"Correlation ID: {intervention.CorrelationId}\r\n"
                + $"Policy: {intervention.PolicyId} v{intervention.PolicyVersion}\r\n"
                + $"Signature verified: {intervention.SignatureVerified}\r\n"
                + $"Action: {intervention.ActionType}\r\n"
                + $"Window: {intervention.StartTimeUtc:O} -> {intervention.EndTimeUtc:O}\r\n"
                + $"Reason: {intervention.Reason}\r\n\r\n"
                + "Trace path:\r\n"
                + "signal.capture -> normalization -> inference -> policy.intervention -> tray.display";
            MessageBox.Show(text, "Intervention Trace", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
    }

    private Layer1TraySnapshot ReadSnapshot()
    {
        lock (_stateGate)
        {
            return new Layer1TraySnapshot(
                Status: _snapshot.Status,
                ConsentState: _snapshot.ConsentState,
                LastSignalAtUtc: _snapshot.LastSignalAtUtc,
                Summary: _snapshot.Summary,
                CanPause: _snapshot.CanPause,
                PauseRemainingSeconds: _snapshot.PauseRemainingSeconds,
                RiskProbabilities: new Dictionary<string, double>(_snapshot.RiskProbabilities, StringComparer.OrdinalIgnoreCase),
                RiskSigma: _snapshot.RiskSigma,
                RecentPrimitives: _snapshot.RecentPrimitives.ToArray(),
                RecentInterventions: _snapshot.RecentInterventions.ToArray(),
                OverloadActive: _snapshot.OverloadActive,
                DeepWorkActive: _snapshot.DeepWorkActive);
        }
    }

    private string ReadSummary()
    {
        lock (_stateGate)
        {
            return _snapshot.Summary;
        }
    }
}
