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
    private volatile bool _focusLoopStopping;

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
        _focusLoopStopping = true;

        try
        {
            _focusEvent?.Set();
        }
        catch
        {
        }

        if (_focusThread is { IsAlive: true })
        {
            try
            {
                _focusThread.Join(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
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

        while (!_focusLoopStopping)
        {
            try
            {
                _focusEvent.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (_focusLoopStopping)
            {
                break;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (_focusLoopStopping || MainWindow is not { IsLoaded: true })
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
    private static readonly string PrimaryLogPath = BuildCommonLogPath();
    private static readonly string FallbackLogPath = BuildUserLogPath();

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
                string line = $"[{DateTimeOffset.UtcNow:O}] [{level}] {message}";
                if (ex != null)
                {
                    line += $" :: {ex.GetType().Name}: {ex.Message}";
                }

                string payload = line + Environment.NewLine;
                if (!TryAppend(PrimaryLogPath, payload))
                {
                    _ = TryAppend(FallbackLogPath, payload);
                }
            }
        }
        catch
        {
        }
    }

    private static bool TryAppend(string path, string payload)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(path, payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCommonLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "control-center.log");
    }

    private static string BuildUserLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "control-center.log");
    }
}
