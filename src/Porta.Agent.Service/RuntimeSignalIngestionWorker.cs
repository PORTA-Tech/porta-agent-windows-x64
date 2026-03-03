using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Core;
using Porta.Agent.Core.Security;
using Porta.Agent.Runtime.Input;

namespace Porta.Agent.Service;

internal sealed class RuntimeSignalIngestionWorker(
    IBrainStateStore store,
    IAgentCoreController controller) : BackgroundService
{
    private const int FlushIntervalMs = 100;
    private const int HeartbeatTicksPerSecond = 10;
    private const int RawInputQueueCapacity = 4096;

    private readonly KeyboardHook _keyboardHook = new();
    private readonly MouseHook _mouseHook = new();
    private readonly Channel<RawInputSample> _samples = Channel.CreateBounded<RawInputSample>(new BoundedChannelOptions(RawInputQueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly object _mouseGate = new();
    private long _pendingMouseDx;
    private long _pendingMouseDy;
    private int _pendingMouseMoveCount;
    private int _collectionEnabled = 1;
    private string _lastForegroundAppHash = string.Empty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _keyboardHook.KeyChanged += OnKeyChanged;
        _mouseHook.MouseMoved += OnMouseMoved;
        _mouseHook.ScrollDeltaChanged += OnScrollDeltaChanged;

        try
        {
            if (!_keyboardHook.Install())
            {
                Console.WriteLine("runtime_input_bridge keyboard_hook_unavailable=true");
            }

            if (!_mouseHook.Install())
            {
                Console.WriteLine("runtime_input_bridge mouse_hook_unavailable=true");
            }

            RefreshCollectionGate();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FlushIntervalMs));
            int tick = 0;

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                FlushPendingMouseMoves();
                ProcessQueuedSamples();

                tick++;
                if (tick >= HeartbeatTicksPerSecond)
                {
                    tick = 0;
                    RefreshCollectionGate();
                    CaptureForegroundHeartbeat();
                    CaptureIdleHeartbeat();
                    ProcessQueuedSamples();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _keyboardHook.KeyChanged -= OnKeyChanged;
            _mouseHook.MouseMoved -= OnMouseMoved;
            _mouseHook.ScrollDeltaChanged -= OnScrollDeltaChanged;
            _keyboardHook.Uninstall();
            _mouseHook.Uninstall();
        }
    }

    private void OnKeyChanged(bool isKeyDown, bool isBackspace, bool isInjected)
    {
        if (!isKeyDown || isInjected || !IsCollectionEnabled())
        {
            return;
        }

        long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string contributor = RawInputContributors.KeyboardKeydown(isBackspace);
        Enqueue(new RawInputSample(tsUtcMs, contributor));
    }

    private void OnMouseMoved(int dx, int dy)
    {
        if (!IsCollectionEnabled())
        {
            return;
        }

        lock (_mouseGate)
        {
            _pendingMouseDx += dx;
            _pendingMouseDy += dy;
            _pendingMouseMoveCount++;
        }
    }

    private void OnScrollDeltaChanged(int delta, bool _)
    {
        if (!IsCollectionEnabled())
        {
            return;
        }

        long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string contributor = RawInputContributors.MouseWheel(delta);
        Enqueue(new RawInputSample(tsUtcMs, contributor));
    }

    private void FlushPendingMouseMoves()
    {
        if (!IsCollectionEnabled())
        {
            lock (_mouseGate)
            {
                _pendingMouseDx = 0;
                _pendingMouseDy = 0;
                _pendingMouseMoveCount = 0;
            }

            return;
        }

        long dx;
        long dy;
        int count;
        lock (_mouseGate)
        {
            dx = _pendingMouseDx;
            dy = _pendingMouseDy;
            count = _pendingMouseMoveCount;
            _pendingMouseDx = 0;
            _pendingMouseDy = 0;
            _pendingMouseMoveCount = 0;
        }

        if (count <= 0)
        {
            return;
        }

        int clampedDx = (int)Math.Clamp(dx, int.MinValue, int.MaxValue);
        int clampedDy = (int)Math.Clamp(dy, int.MinValue, int.MaxValue);
        if (clampedDx == 0 && clampedDy == 0)
        {
            return;
        }

        long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string contributor = RawInputContributors.MouseMove(clampedDx, clampedDy);
        Enqueue(new RawInputSample(tsUtcMs, contributor));
    }

    private void CaptureForegroundHeartbeat()
    {
        if (!IsCollectionEnabled())
        {
            _lastForegroundAppHash = string.Empty;
            return;
        }

        if (!TryReadForegroundProcessId(out uint processId) || processId == 0)
        {
            return;
        }

        string appHash = ComputeForegroundAppHash(processId);
        if (string.IsNullOrWhiteSpace(appHash))
        {
            return;
        }

        _lastForegroundAppHash = appHash;
        long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string contributor = RawInputContributors.Focus(appHash);
        Enqueue(new RawInputSample(tsUtcMs, contributor));
    }

    private void CaptureIdleHeartbeat()
    {
        if (!IsCollectionEnabled() || !TryReadIdleSeconds(out double idleSeconds))
        {
            return;
        }

        long tsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string contributor = RawInputContributors.Idle(idleSeconds);
        Enqueue(new RawInputSample(tsUtcMs, contributor));
    }

    private void Enqueue(RawInputSample sample)
    {
        _samples.Writer.TryWrite(sample);
    }

    private void ProcessQueuedSamples()
    {
        while (_samples.Reader.TryRead(out RawInputSample sample))
        {
            if (!IsCollectionEnabled())
            {
                continue;
            }

            try
            {
                store.WriteSnapshot(BuildRawInputSnapshot(sample.TimestampUtcMs, sample.Contributor));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"runtime_input_bridge write_failed={ex.GetType().Name}:{ex.Message}");
            }
        }
    }

    private void RefreshCollectionGate()
    {
        try
        {
            AgentControlState control = controller.GetControlState();
            Volatile.Write(ref _collectionEnabled, control.ConsentActive && !control.Paused ? 1 : 0);
        }
        catch
        {
            Volatile.Write(ref _collectionEnabled, 0);
        }
    }

    private bool IsCollectionEnabled()
    {
        return Volatile.Read(ref _collectionEnabled) == 1;
    }

    private static AgentBrainSnapshot BuildRawInputSnapshot(long timestampUtcMs, string contributor)
    {
        DateTimeOffset timestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(timestampUtcMs);
        return new AgentBrainSnapshot(
            TimestampUtc: timestampUtc,
            TypingBurstIntensity: 0d,
            ScrollVelocityIndex: 0d,
            FocusSwitchRate: 0d,
            SmoothedTypingBurstIntensity: 0d,
            SmoothedScrollVelocityIndex: 0d,
            SmoothedFocusSwitchRate: 0d,
            FocusStrength: 0d,
            OverloadLikelihood: 0d,
            FatigueProbability: 0d,
            CognitiveStability: 0d,
            DominantStateValue: 0d,
            DominantStateName: "focus",
            Confidence: 0d,
            LastAction: "raw_input_capture",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: [contributor],
            PolicyId: "runtime-input-bridge",
            Domain: "input",
            EscalationLevel: "Inform");
    }

    private static bool TryReadForegroundProcessId(out uint processId)
    {
        processId = 0;
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(hwnd, out processId);
        return processId != 0;
    }

    private static string ComputeForegroundAppHash(uint processId)
    {
        string stableIdentifier = ResolveStableAppIdentifier(processId);
        byte[] secret = Secrets.GetInstallSecret();
        using var hmac = new HMACSHA256(secret);
        byte[] hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(stableIdentifier));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string ResolveStableAppIdentifier(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));
            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return $"exe:{path.Trim().ToLowerInvariant()}";
            }

            if (!string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return $"process:{process.ProcessName.Trim().ToLowerInvariant()}";
            }
        }
        catch
        {
        }

        return $"pid:{processId}";
    }

    private static bool TryReadIdleSeconds(out double idleSeconds)
    {
        idleSeconds = 0d;
        var input = new LastInputInfo
        {
            CbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref input))
        {
            return false;
        }

        uint tickNow = unchecked((uint)Environment.TickCount64);
        uint elapsedMs = unchecked(tickNow - input.DwTime);
        idleSeconds = elapsedMs / 1000d;
        return true;
    }

    private readonly record struct RawInputSample(long TimestampUtcMs, string Contributor);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
