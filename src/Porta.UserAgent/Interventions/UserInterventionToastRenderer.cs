namespace Porta.UserAgent.Interventions;

using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

internal sealed class UserInterventionToastRenderer : IDisposable
{
    private readonly BlockingCollection<ToastCommand> _queue = [];
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
            _queue.Add(new ToastCommand(title, message), CancellationToken.None);
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
            _queue.Add(ToastCommand.Exit, CancellationToken.None);
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

    private readonly record struct ToastCommand(string Title, string Message, bool IsExit = false)
    {
        public static ToastCommand Exit => new(string.Empty, string.Empty, true);
    }

    private sealed class ToastApplicationContext : ApplicationContext
    {
        private readonly BlockingCollection<ToastCommand> _queue;
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _timer;

        public ToastApplicationContext(BlockingCollection<ToastCommand> queue, TaskCompletionSource<bool> ready)
        {
            _queue = queue;
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = "PORTA",
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
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _timer.Dispose();
            base.ExitThreadCore();
        }

        private void DrainQueue()
        {
            while (_queue.TryTake(out ToastCommand command))
            {
                if (command.IsExit)
                {
                    ExitThread();
                    return;
                }

                string title = string.IsNullOrWhiteSpace(command.Title) ? "PORTA" : command.Title.Trim();
                string message = string.IsNullOrWhiteSpace(command.Message)
                    ? "PORTA has a suggestion for you."
                    : command.Message.Trim();
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(4000);
            }
        }
    }
}
