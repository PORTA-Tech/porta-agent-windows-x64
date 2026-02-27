namespace Porta.Tray;

internal sealed class BreakSuggestionOverlayHost : IDisposable
{
    private BreakSuggestionOverlayForm? _form;
    private bool _disposed;

    public void Show(string title, string message, TimeSpan duration)
    {
        if (_disposed)
        {
            return;
        }

        _form ??= new BreakSuggestionOverlayForm();
        _form.ShowOverlay(title, message, duration);
    }

    public void Hide()
    {
        if (_disposed || _form == null)
        {
            return;
        }

        _form.HideOverlay();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_form != null)
        {
            _form.HideOverlay();
            _form.Dispose();
            _form = null;
        }
    }
}

internal sealed class BreakSuggestionOverlayForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _messageLabel;
    private readonly Label _countdownLabel;
    private readonly Button _dismissButton;
    private readonly System.Windows.Forms.Timer _timer;
    private DateTimeOffset _endUtc = DateTimeOffset.MinValue;

    public BreakSuggestionOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(16, 24, 39);
        ForeColor = Color.White;
        Width = 420;
        Height = 190;
        Opacity = 0.97d;

        _titleLabel = new Label
        {
            Left = 20,
            Top = 18,
            Width = 380,
            Height = 28,
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
            Text = "PORTA Suggestion",
            ForeColor = Color.FromArgb(248, 250, 252)
        };

        _messageLabel = new Label
        {
            Left = 20,
            Top = 54,
            Width = 380,
            Height = 50,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular),
            Text = "Take a short break.",
            ForeColor = Color.FromArgb(226, 232, 240)
        };

        _countdownLabel = new Label
        {
            Left = 20,
            Top = 114,
            Width = 240,
            Height = 24,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
            Text = "Time remaining: 02:00",
            ForeColor = Color.FromArgb(191, 219, 254)
        };

        _dismissButton = new Button
        {
            Left = 280,
            Top = 112,
            Width = 120,
            Height = 32,
            Text = "Dismiss",
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _dismissButton.FlatAppearance.BorderColor = Color.FromArgb(147, 197, 253);
        _dismissButton.Click += (_, _) => HideOverlay();

        Controls.Add(_titleLabel);
        Controls.Add(_messageLabel);
        Controls.Add(_countdownLabel);
        Controls.Add(_dismissButton);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => OnTick();
    }

    public void ShowOverlay(string title, string message, TimeSpan duration)
    {
        _titleLabel.Text = title;
        _messageLabel.Text = message;
        _endUtc = DateTimeOffset.UtcNow.Add(duration);
        UpdateCountdown();
        PositionNearTopRight();
        if (!Visible)
        {
            Show();
        }

        _timer.Start();
    }

    public void HideOverlay()
    {
        _timer.Stop();
        Hide();
    }

    private void OnTick()
    {
        if (DateTimeOffset.UtcNow >= _endUtc)
        {
            HideOverlay();
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        TimeSpan remaining = _endUtc - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        _countdownLabel.Text = $"Time remaining: {remaining:mm\\:ss}";
    }

    private void PositionNearTopRight()
    {
        Rectangle bounds = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        Left = Math.Max(bounds.Left + 12, bounds.Right - Width - 20);
        Top = bounds.Top + 20;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleLabel.Dispose();
            _messageLabel.Dispose();
            _countdownLabel.Dispose();
            _dismissButton.Dispose();
        }

        base.Dispose(disposing);
    }
}
