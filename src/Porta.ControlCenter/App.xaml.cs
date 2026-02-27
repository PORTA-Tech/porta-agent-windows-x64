using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

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
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Exception? ex = args.ExceptionObject as Exception;
            ControlCenterFileLog.Error("unhandled_exception", ex);
        };
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _mutex = new Mutex(initiallyOwned: true, name: AppMutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            ControlCenterFileLog.Info("second_instance_signal_existing");
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
        ControlCenterFileLog.Info("startup");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ControlCenterFileLog.Info("shutdown");

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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ControlCenterFileLog.Error("dispatcher_unhandled_exception", e.Exception);
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

internal static class ControlCenterFileLog
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
        return Path.Combine(root, "PortaAgent", "control-center.log");
    }
}
