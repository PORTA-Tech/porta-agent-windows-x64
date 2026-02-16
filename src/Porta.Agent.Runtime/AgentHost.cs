using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Porta.Agent.Runtime.Input;

namespace Porta.Agent.Runtime;

public sealed class AgentHost : IDisposable
{
    private readonly Channel<RawScrollEvent> _queue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;
    private MouseHook? _mouseHook;
    private Thread? _mouseHookThread;

    public AgentHost()
    {
        _queue = Channel.CreateUnbounded<RawScrollEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public Task StartAsync()
    {
        // Start processing loop
        _processingTask = Task.Run(() => ProcessingLoop(_cts.Token));

        // Start mouse hook on dedicated STA thread
        _mouseHookThread = new Thread(MouseHookThreadProc)
        {
            IsBackground = false,
            Name = "MouseHookThread"
        };
        _mouseHookThread.SetApartmentState(ApartmentState.STA);
        _mouseHookThread.Start();

        return Task.CompletedTask;
    }

    private void MouseHookThreadProc()
    {
        _mouseHook = new MouseHook(OnScrollCaptured);
        _mouseHook.Install();

        // Simple message pump to keep the hook alive
        NativeMethods.MSG msg;
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private void OnScrollCaptured(int delta)
    {
        var evt = new RawScrollEvent(Stopwatch.GetTimestamp(), delta);
        _queue.Writer.TryWrite(evt);
    }

    private async Task ProcessingLoop(CancellationToken token)
    {
        var reader = _queue.Reader;
        long? lastTimestamp = null;

        try
        {
            while (await reader.WaitToReadAsync(token))
            {
                while (reader.TryRead(out var evt))
                {
                    Process(evt, ref lastTimestamp);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private void Process(RawScrollEvent evt, ref long? lastTimestamp)
    {
        double velocity = 0;
        if (lastTimestamp.HasValue)
        {
            long deltaTicks = evt.Timestamp - lastTimestamp.Value;
            if (deltaTicks > 0)
                velocity = evt.Delta / (double)deltaTicks;
        }

        lastTimestamp = evt.Timestamp;

        Console.WriteLine($"Scroll delta: {evt.Delta}, velocity: {velocity:F4}, timestamp: {evt.Timestamp}");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();

        if (_mouseHookThread != null && _mouseHook != null)
        {
            _mouseHook.Uninstall();
            NativeMethods.PostThreadMessage((uint)_mouseHookThread.ManagedThreadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            _mouseHookThread.Join();
        }

        if (_processingTask != null)
            await _processingTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mouseHook?.Dispose();
    }

    // --- Native interop ---
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

        public const uint WM_QUIT = 0x0012;
    }
}
