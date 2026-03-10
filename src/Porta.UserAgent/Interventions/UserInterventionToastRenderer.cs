namespace Porta.UserAgent.Interventions;

using System.Collections.Concurrent;
using System.Drawing;
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
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(4000);
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
        private readonly System.Windows.Forms.Timer _closeTimer;

        public ToastPopupForm(string title, string message)
        {
            Font baseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(21, 24, 28);
            ForeColor = Color.FromArgb(226, 232, 240);
            ClientSize = new Size(360, 112);
            Padding = new Padding(16);

            var titleLabel = new Label
            {
                AutoSize = false,
                Text = string.IsNullOrWhiteSpace(title) ? "PORTA" : title.Trim(),
                Font = new Font(
                    baseFont.FontFamily,
                    baseFont.Size,
                    FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 250, 252),
                Dock = DockStyle.Top,
                Height = 24
            };

            var messageLabel = new Label
            {
                AutoSize = false,
                Text = string.IsNullOrWhiteSpace(message) ? "PORTA has a suggestion for you." : message.Trim(),
                Font = baseFont,
                ForeColor = Color.FromArgb(203, 213, 225),
                Dock = DockStyle.Fill
            };

            var accentPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 6,
                BackColor = Color.FromArgb(59, 130, 246)
            };

            Controls.Add(messageLabel);
            Controls.Add(titleLabel);
            Controls.Add(accentPanel);

            PositionBottomRight();

            _closeTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer.Stop();
                Close();
            };
            Shown += (_, _) => _closeTimer.Start();
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

        private void PositionBottomRight()
        {
            Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(
                area.Right - Width - 16,
                area.Bottom - Height - 16);
        }
    }
}
