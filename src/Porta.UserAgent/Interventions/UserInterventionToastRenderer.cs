namespace Porta.UserAgent.Interventions;

using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

internal sealed class UserInterventionToastRenderer : IUserInterventionPresenter, IDisposable
{
    private readonly BlockingCollection<RendererCommand> _queue = [];
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _uiThread;
    private bool _disposed;

    public UserInterventionToastRenderer()
    {
        _uiThread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "PORTA.UserAgent.ToastRenderer"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    public bool TryShow(string title, string message, out string errorCode)
    {
        errorCode = string.Empty;
        if (_disposed)
        {
            errorCode = "toast_renderer_disposed";
            return false;
        }

        if (!_ready.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            errorCode = "toast_renderer_not_ready";
            return false;
        }

        if (!_ready.Task.Result)
        {
            errorCode = "toast_renderer_failed";
            return false;
        }

        try
        {
            _queue.Add(RendererCommand.CreateToast(title, message), CancellationToken.None);
            return true;
        }
        catch (InvalidOperationException)
        {
            errorCode = "toast_renderer_closed";
            return false;
        }
    }

    public bool TryUpdateTrayState(UserTrayVisualState state, out string errorCode)
    {
        errorCode = string.Empty;
        if (_disposed)
        {
            errorCode = "toast_renderer_disposed";
            return false;
        }

        if (!_ready.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            errorCode = "toast_renderer_not_ready";
            return false;
        }

        if (!_ready.Task.Result)
        {
            errorCode = "toast_renderer_failed";
            return false;
        }

        try
        {
            _queue.Add(RendererCommand.CreateTrayState(state), CancellationToken.None);
            return true;
        }
        catch (InvalidOperationException)
        {
            errorCode = "toast_renderer_closed";
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
        try
        {
            _queue.Add(RendererCommand.Exit, CancellationToken.None);
        }
        catch
        {
        }

        _queue.CompleteAdding();
        if (_uiThread.IsAlive)
        {
            _uiThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void ThreadMain()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var context = new ToastApplicationContext(_queue, _ready);
            Application.Run(context);
        }
        catch
        {
            _ready.TrySetResult(false);
        }
    }

    public readonly record struct UserTrayVisualState(UserTrayMode Mode, string TooltipText);

    public enum UserTrayMode
    {
        Unavailable,
        Normal,
        DeepWork,
        Overload
    }

    private readonly record struct RendererCommand(
        RendererCommandKind Kind,
        string Title,
        string Message,
        UserTrayVisualState TrayState)
    {
        public static RendererCommand Exit => new(
            RendererCommandKind.Exit,
            string.Empty,
            string.Empty,
            new UserTrayVisualState(UserTrayMode.Unavailable, "PORTA"));

        public static RendererCommand CreateToast(string title, string message) => new(
            RendererCommandKind.Toast,
            title,
            message,
            new UserTrayVisualState(UserTrayMode.Unavailable, "PORTA"));

        public static RendererCommand CreateTrayState(UserTrayVisualState state) => new(
            RendererCommandKind.TrayState,
            string.Empty,
            string.Empty,
            state);
    }

    private enum RendererCommandKind
    {
        Toast,
        TrayState,
        Exit
    }

    private sealed class ToastApplicationContext : ApplicationContext
    {
        private readonly BlockingCollection<RendererCommand> _queue;
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private ToastPopupForm? _activeToast;

        public ToastApplicationContext(BlockingCollection<RendererCommand> queue, TaskCompletionSource<bool> ready)
        {
            _queue = queue;
            _notifyIcon = new NotifyIcon
            {
                Icon = ResolveTrayIcon(UserTrayMode.Normal),
                Text = "PORTA | Normal",
                Visible = true
            };
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (_, _) => DrainQueue();
            _timer.Start();
            ready.TrySetResult(true);
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _activeToast?.Close();
            _activeToast?.Dispose();
            _activeToast = null;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _timer.Dispose();
            base.ExitThreadCore();
        }

        private void DrainQueue()
        {
            while (_queue.TryTake(out RendererCommand command))
            {
                if (command.Kind == RendererCommandKind.Exit)
                {
                    ExitThread();
                    return;
                }

                if (command.Kind == RendererCommandKind.TrayState)
                {
                    ApplyTrayState(command.TrayState);
                    continue;
                }

                string title = string.IsNullOrWhiteSpace(command.Title) ? "PORTA" : command.Title.Trim();
                string message = string.IsNullOrWhiteSpace(command.Message)
                    ? "PORTA has a suggestion for you."
                    : command.Message.Trim();
                ShowDesktopToast(title, message);
            }
        }

        private void ApplyTrayState(UserTrayVisualState state)
        {
            _notifyIcon.Icon = ResolveTrayIcon(state.Mode);
            _notifyIcon.Text = NormalizeNotifyIconText(state.TooltipText);
        }

        private void ShowDesktopToast(string title, string message)
        {
            _activeToast?.Close();
            _activeToast?.Dispose();
            _activeToast = new ToastPopupForm(title, message);
            _activeToast.Show();
        }

        private static Icon ResolveTrayIcon(UserTrayMode mode)
        {
            return mode switch
            {
                UserTrayMode.DeepWork => SystemIcons.Information,
                UserTrayMode.Overload => SystemIcons.Warning,
                UserTrayMode.Unavailable => SystemIcons.Error,
                _ => SystemIcons.Application
            };
        }

        private static string NormalizeNotifyIconText(string? tooltip)
        {
            string text = string.IsNullOrWhiteSpace(tooltip)
                ? "PORTA"
                : tooltip.Replace(Environment.NewLine, " ").Trim();
            if (text.Length <= 63)
            {
                return text;
            }

            return text[..63];
        }
    }

    private sealed class ToastPopupForm : Form
    {
        private static readonly Color ToastBackgroundColor = Color.FromArgb(248, 250, 252);
        private static readonly Color ToastBorderColor = Color.FromArgb(203, 213, 225);
        private static readonly Color ToastAccentColor = Color.FromArgb(14, 116, 144);
        private static readonly Color ToastTitleColor = Color.FromArgb(15, 23, 42);
        private static readonly Color ToastMessageColor = Color.FromArgb(30, 41, 59);
        private const int ToastCornerRadius = 18;
        private const int ToastWidth = 420;
        private const int ToastHeight = 154;
        private const int ToastScreenMargin = 18;
        private const int ToastDisplayDurationMs = 6500;

        private readonly System.Windows.Forms.Timer _closeTimer;

        public ToastPopupForm(string title, string message)
        {
            Font baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = ToastBackgroundColor;
            ForeColor = ToastMessageColor;
            ClientSize = new Size(ToastWidth, ToastHeight);
            Padding = new Padding(18, 16, 18, 16);
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);

            var accentBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 6,
                BackColor = ToastAccentColor
            };

            var contentPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = ToastBackgroundColor,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(18, 16, 18, 16)
            };
            contentPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            contentPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            contentPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var subtitleLabel = new Label
            {
                AutoSize = true,
                Text = "Suggestion",
                Font = new Font(
                    baseFont.FontFamily,
                    Math.Max(9f, baseFont.Size - 0.5f),
                    FontStyle.Bold),
                ForeColor = ToastAccentColor,
                Margin = new Padding(0, 0, 0, 6)
            };

            var titleLabel = new Label
            {
                AutoSize = true,
                Text = string.IsNullOrWhiteSpace(title) ? "PORTA" : title.Trim(),
                Font = new Font(
                    baseFont.FontFamily,
                    Math.Max(13f, baseFont.Size + 1.5f),
                    FontStyle.Bold),
                ForeColor = ToastTitleColor,
                Margin = new Padding(0, 0, 0, 8)
            };

            var messageLabel = new Label
            {
                AutoSize = true,
                Text = string.IsNullOrWhiteSpace(message) ? "PORTA has a suggestion for you." : message.Trim(),
                Font = new Font(
                    baseFont.FontFamily,
                    Math.Max(10.5f, baseFont.Size + 0.25f),
                    FontStyle.Regular),
                ForeColor = ToastMessageColor,
                MaximumSize = new Size(ToastWidth - 64, 0),
                Margin = new Padding(0)
            };

            contentPanel.Controls.Add(subtitleLabel, 0, 0);
            contentPanel.Controls.Add(titleLabel, 0, 1);
            contentPanel.Controls.Add(messageLabel, 0, 2);

            Controls.Add(contentPanel);
            Controls.Add(accentBar);

            ApplyRoundedChrome();
            PositionTopRight();

            _closeTimer = new System.Windows.Forms.Timer { Interval = ToastDisplayDurationMs };
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer.Stop();
                Close();
            };
            Shown += (_, _) =>
            {
                PositionTopRight();
                _closeTimer.Start();
            };
            SizeChanged += (_, _) =>
            {
                ApplyRoundedChrome();
                PositionTopRight();
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WsExToolWindow = 0x00000080;
                const int WsExNoActivate = 0x08000000;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closeTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(ToastBorderColor, 1.25f);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = CreateRoundedRectanglePath(
                new RectangleF(0.6f, 0.6f, Width - 1.2f, Height - 1.2f),
                ToastCornerRadius);
            e.Graphics.DrawPath(pen, path);
        }

        private void ApplyRoundedChrome()
        {
            using GraphicsPath path = CreateRoundedRectanglePath(
                new RectangleF(0f, 0f, Width, Height),
                ToastCornerRadius);
            Region?.Dispose();
            Region = new Region(path);
        }

        private void PositionTopRight()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                area.Right - Width - ToastScreenMargin,
                area.Top + ToastScreenMargin);
        }

        private static GraphicsPath CreateRoundedRectanglePath(RectangleF bounds, float radius)
        {
            float diameter = radius * 2f;
            var path = new GraphicsPath();
            path.StartFigure();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
