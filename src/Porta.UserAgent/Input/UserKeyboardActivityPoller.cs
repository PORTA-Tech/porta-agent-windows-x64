namespace Porta.UserAgent.Input;

using System.Runtime.InteropServices;
using System.Threading;

internal interface IKeyboardStateReader
{
    short GetAsyncKeyState(int virtualKey);
}

internal sealed class Win32KeyboardStateReader : IKeyboardStateReader
{
    public short GetAsyncKeyState(int virtualKey)
    {
        return NativeGetAsyncKeyState(virtualKey);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern short NativeGetAsyncKeyState(int vKey);
}

internal sealed class UserKeyboardActivityPoller : IDisposable
{
    private const int VirtualKeyBackspace = 0x08;
    private static readonly int[] MonitoredVirtualKeys = BuildMonitoredVirtualKeys();
    private readonly IKeyboardStateReader _stateReader;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _installedSignal = new(false);
    private readonly bool[] _previousDown = new bool[256];
    private Thread? _thread;
    private volatile bool _installSucceeded;
    private volatile bool _stopRequested;

    public UserKeyboardActivityPoller()
        : this(new Win32KeyboardStateReader(), TimeSpan.FromMilliseconds(25))
    {
    }

    internal UserKeyboardActivityPoller(IKeyboardStateReader stateReader, TimeSpan pollInterval)
    {
        _stateReader = stateReader;
        _pollInterval = pollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(25) : pollInterval;
    }

    public event Action<bool, bool, bool> KeyChanged = delegate { };

    public bool Install(int maxAttempts = 1, int retryDelayMs = 0)
    {
        _ = maxAttempts;
        _ = retryDelayMs;

        lock (_gate)
        {
            if (_thread != null && _thread.IsAlive)
            {
                return true;
            }
        }

        _installedSignal.Reset();
        _installSucceeded = false;
        _stopRequested = false;

        var thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "UserKeyboardActivityPoller"
        };

        lock (_gate)
        {
            _thread = thread;
        }

        thread.Start();
        bool signaled = _installedSignal.Wait(TimeSpan.FromSeconds(2));
        if (signaled && _installSucceeded)
        {
            return true;
        }

        CleanupStoppedThread();
        return false;
    }

    public void Uninstall()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
        }

        if (thread == null)
        {
            return;
        }

        _stopRequested = true;
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
        }

        CleanupStoppedThread();
    }

    internal void PrimeSnapshot()
    {
        foreach (int virtualKey in MonitoredVirtualKeys)
        {
            _previousDown[virtualKey] = IsDown(_stateReader.GetAsyncKeyState(virtualKey));
        }
    }

    internal void PollOnce()
    {
        foreach (int virtualKey in MonitoredVirtualKeys)
        {
            short state = _stateReader.GetAsyncKeyState(virtualKey);
            bool currentDown = IsDown(state);
            bool previousDown = _previousDown[virtualKey];
            bool shortTapDetected = WasPressedSinceLastPoll(state) && !currentDown && !previousDown;

            if (currentDown == previousDown && !shortTapDetected)
            {
                continue;
            }

            _previousDown[virtualKey] = currentDown;
            try
            {
                KeyChanged(shortTapDetected || currentDown, virtualKey == VirtualKeyBackspace, false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"keyboard_poller_listener_failed error={ex.GetType().Name}:{ex.Message}");
            }
        }
    }

    private void ThreadMain()
    {
        try
        {
            PrimeSnapshot();
            _installSucceeded = true;
            _installedSignal.Set();

            while (!_stopRequested)
            {
                PollOnce();
                Thread.Sleep(_pollInterval);
            }
        }
        catch (Exception ex)
        {
            _installSucceeded = false;
            Console.Error.WriteLine($"keyboard_poller_thread_failed error={ex.GetType().Name}:{ex.Message}");
        }
        finally
        {
            _installedSignal.Set();
        }
    }

    private void CleanupStoppedThread()
    {
        lock (_gate)
        {
            _thread = null;
        }
    }

    private static bool IsDown(short state)
    {
        return (state & 0x8000) != 0;
    }

    private static bool WasPressedSinceLastPoll(short state)
    {
        return (state & 0x0001) != 0;
    }

    private static int[] BuildMonitoredVirtualKeys()
    {
        var keys = new List<int>(240);
        for (int virtualKey = 1; virtualKey < 256; virtualKey++)
        {
            if (virtualKey is 0x01 or 0x02 or 0x04 or 0x05 or 0x06)
            {
                continue;
            }

            keys.Add(virtualKey);
        }

        return keys.ToArray();
    }

    public void Dispose()
    {
        Uninstall();
    }
}
