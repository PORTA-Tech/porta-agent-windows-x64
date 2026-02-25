using System.Drawing;
using System.Windows.Forms;

namespace Porta.Agent.Runtime.Layer1;

internal sealed class Layer1TrayController : IDisposable
{
    private readonly ManualResetEventSlim _started = new(false);
    private readonly object _stateGate = new();
    private Thread? _thread;
    private TrayContext? _context;
    private string _status = "Starting";
    private string _consent = Layer1ConsentState.InstalledNotConsented.ToString();
    private DateTimeOffset? _lastSignalAtUtc;
    private string _summary = "No signals emitted yet.";
    private int _disposed;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? RevokeRequested;
    public event Action? ExitRequested;

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

    public void Update(string status, Layer1ConsentState consentState, DateTimeOffset? lastSignalAtUtc, string summary, bool canPause)
    {
        lock (_stateGate)
        {
            _status = status;
            _consent = consentState.ToString();
            _lastSignalAtUtc = lastSignalAtUtc;
            _summary = summary;
        }

        _context?.UpdateFromController(canPause);
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
        private readonly Layer1TrayController _owner;
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _lastSignalItem;
        private readonly ToolStripMenuItem _consentItem;
        private readonly ToolStripMenuItem _pauseResumeItem;

        public TrayContext(Layer1TrayController owner)
        {
            _owner = owner;
            _statusItem = new ToolStripMenuItem("Status: Starting") { Enabled = false };
            _lastSignalItem = new ToolStripMenuItem("Last signal: n/a") { Enabled = false };
            _consentItem = new ToolStripMenuItem("Consent: installed_not_consented") { Enabled = false };
            _pauseResumeItem = new ToolStripMenuItem("Pause");
            var viewSummaryItem = new ToolStripMenuItem("View Data Collected", null, (_, _) =>
            {
                MessageBox.Show(_owner.ReadSummary(), "Layer 1 Data Summary", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            var disclosureItem = new ToolStripMenuItem("Open Disclosure", null, (_, _) =>
            {
                MessageBox.Show(Layer1Disclosure.Text, "Layer 1 Disclosure", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            var revokeItem = new ToolStripMenuItem("Revoke Consent", null, (_, _) => _owner.RevokeRequested?.Invoke());
            var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => _owner.ExitRequested?.Invoke());
            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(_lastSignalItem);
            menu.Items.Add(_consentItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_pauseResumeItem);
            menu.Items.Add(viewSummaryItem);
            menu.Items.Add(disclosureItem);
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

            UpdateFromController(canPause: false);
        }

        public void UpdateFromController(bool canPause)
        {
            if (_icon.ContextMenuStrip?.InvokeRequired == true)
            {
                _icon.ContextMenuStrip.BeginInvoke(new Action(() => UpdateFromController(canPause)));
                return;
            }

            _statusItem.Text = $"Status: {_owner.ReadStatus()}";
            _lastSignalItem.Text = $"Last signal: {_owner.ReadLastSignalText()}";
            _consentItem.Text = $"Consent: {_owner.ReadConsent()}";
            _pauseResumeItem.Text = canPause ? "Pause" : "Resume";
            _pauseResumeItem.Click -= PauseClick;
            _pauseResumeItem.Click -= ResumeClick;
            if (canPause)
            {
                _pauseResumeItem.Click += PauseClick;
            }
            else
            {
                _pauseResumeItem.Click += ResumeClick;
            }
        }

        public void RequestExit()
        {
            if (_icon.ContextMenuStrip?.InvokeRequired == true)
            {
                _icon.ContextMenuStrip.BeginInvoke(new Action(RequestExit));
                return;
            }

            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }

        private void PauseClick(object? sender, EventArgs e) => _owner.PauseRequested?.Invoke();
        private void ResumeClick(object? sender, EventArgs e) => _owner.ResumeRequested?.Invoke();
    }

    private string ReadStatus()
    {
        lock (_stateGate)
        {
            return _status;
        }
    }

    private string ReadConsent()
    {
        lock (_stateGate)
        {
            return _consent;
        }
    }

    private string ReadLastSignalText()
    {
        lock (_stateGate)
        {
            return _lastSignalAtUtc.HasValue
                ? _lastSignalAtUtc.Value.ToString("O")
                : "n/a";
        }
    }

    private string ReadSummary()
    {
        lock (_stateGate)
        {
            return _summary;
        }
    }
}
