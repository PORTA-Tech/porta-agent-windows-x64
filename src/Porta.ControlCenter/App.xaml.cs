using System.Threading;
using System.Windows;

namespace Porta.ControlCenter;

public partial class App : Application
{
    private const string AppMutexName = @"Global\PORTA_ControlCenter_SingleInstance";
    private const string FocusEventName = @"Global\PORTA_ControlCenter_FocusEvent";

    private Mutex? _mutex;
    private EventWaitHandle? _focusEvent;
    private Thread? _focusThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, name: AppMutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            TrySignalExistingWindow();
            Shutdown();
            return;
        }

        _focusEvent = new EventWaitHandle(false, EventResetMode.AutoReset, FocusEventName);
        _focusThread = new Thread(FocusLoop)
        {
            Name = "PortaControlCenterFocusThread",
            IsBackground = true
        };
        _focusThread.Start();

        MainWindow window = new();
        MainWindow = window;
        window.Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _focusEvent?.Set();
        }
        catch
        {
        }

        _focusEvent?.Dispose();

        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }

            _mutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void TrySignalExistingWindow()
    {
        try
        {
            using EventWaitHandle signal = EventWaitHandle.OpenExisting(FocusEventName);
            signal.Set();
        }
        catch
        {
        }
    }

    private void FocusLoop()
    {
        if (_focusEvent == null)
        {
            return;
        }

        while (true)
        {
            _focusEvent.WaitOne();
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is not { IsLoaded: true })
                {
                    return;
                }

                if (MainWindow.WindowState == WindowState.Minimized)
                {
                    MainWindow.WindowState = WindowState.Normal;
                }

                MainWindow.Activate();
                MainWindow.Topmost = true;
                MainWindow.Topmost = false;
                MainWindow.Focus();
            });
        }
    }
}
