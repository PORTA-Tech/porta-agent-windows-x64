using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Porta.Agent.Runtime.Input;

namespace Porta.Agent.Runtime;

public sealed class AgentHost : IDisposable
{
    private readonly Channel<RawScrollEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;

    private MouseHook? _mouseHook;
    private Thread? _mouseHookThread;

    public AgentHost()
    {
        _channel = Channel.CreateUnbounded<RawScrollEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public Task StartAsync()
    {
        _processingTask = Task.Run(ProcessLoop, _cts.Token);

        _mouseHookThread = new Thread(MouseHookThreadProc)
        {
            IsBackground = true,
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

        // Win32 message loop
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
        _channel.Writer.TryWrite(evt);
    }

    private async Task ProcessLoop()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var evt))
                    Process(evt);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Process(RawScrollEvent evt)
    {
        Console.WriteLine($"Scroll delta: {evt.Delta}");
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
}
