using System;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Runtime.Layer1;
using Porta.Agent.Runtime.Diagnostics;
using Porta.Agent.Runtime.Ui;

class Program
{
    private const string TraySingleInstanceMutexName = @"Global\PORTA_AriTray_SingleInstance";
    private const string ControlCenterSingleInstanceMutexName = @"Global\PORTA_AriControlCenter_SingleInstance";

    static async Task Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: TraySingleInstanceMutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            using var controlCenterMutex = new Mutex(initiallyOwned: true, name: ControlCenterSingleInstanceMutexName, createdNew: out bool controlCenterCreatedNew);
            if (controlCenterCreatedNew)
            {
                ControlCenterWindowHost.ShowBlocking(alreadyRunning: true);
            }

            return;
        }

        RuntimeProfile profile = ResolveRuntimeProfile();
        ApplyProfileDefaults(profile);
        Logger.Info($"Runtime profile: {profile}");

        using var host = new Layer1AgentHost();
        using var stopCts = new CancellationTokenSource();
        int cycleCount = ReadIntEnvironment("PORTA_START_STOP_CYCLES");
        int autoStopMs = ReadIntEnvironment("PORTA_AUTO_STOP_MS");
        bool suppressionEnabled = false;
        bool actuationEnabled = false;
        bool keyboardCaptureEnabled = true;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopCts.Cancel();
        };

        try
        {
            suppressionEnabled = ReadBoolEnvironment("PORTA_SUPPRESSION_ENABLED", defaultValue: false);
            actuationEnabled = ReadBoolEnvironment("PORTA_ACTUATION_ENABLED", defaultValue: false);
            keyboardCaptureEnabled = ReadBoolEnvironment("PORTA_KEYBOARD_CAPTURE_ENABLED", defaultValue: true);
            if (cycleCount > 0)
            {
                Logger.Info($"Running start/stop cycle test for {cycleCount} cycles.");
                for (int i = 1; i <= cycleCount; i++)
                {
                    await host.StartAsync(stopCts.Token);
                    await host.StopAsync();
                    Logger.Event("cycle_completed", ("index", i), ("total", cycleCount));
                }

                return;
            }

            await host.StartAsync(stopCts.Token);
            Logger.Info("Scroll anywhere. Press Ctrl+C to exit.");
            ControlCenterWindowHost.ShowNonBlocking(alreadyRunning: false);

            if (autoStopMs > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(autoStopMs).ConfigureAwait(false);
                    stopCts.Cancel();
                });
            }

            try
            {
                await Task.Delay(Timeout.Infinite, stopCts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            await host.StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Runtime terminated with fatal error.");
            CrashDumpWriter.TryWriteFatal(
                ex,
                new CrashDumpContext
                {
                    RuntimeProfile = profile.ToString().ToLowerInvariant(),
                    SuppressionEnabled = suppressionEnabled,
                    ActuationEnabled = actuationEnabled,
                    KeyboardCaptureEnabled = keyboardCaptureEnabled,
                    StartStopCycles = cycleCount,
                    AutoStopMs = autoStopMs
                });

            try
            {
                await host.StopAsync();
            }
            catch (Exception stopEx)
            {
                Logger.Error(stopEx, "Runtime stop failed after fatal error.");
            }

            Environment.ExitCode = 1;
        }
    }

    private static int ReadIntEnvironment(string key)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out int value) ? Math.Max(0, value) : 0;
    }

    private enum RuntimeProfile
    {
        HeadlessProd,
        DebugVisual,
        SmokeTest
    }

    private static RuntimeProfile ResolveRuntimeProfile()
    {
        string raw = Environment.GetEnvironmentVariable("PORTA_RUNTIME_PROFILE") ?? "headless_prod";
        return raw.Trim().ToLowerInvariant() switch
        {
            "debug_visual" => RuntimeProfile.DebugVisual,
            "smoke_test" => RuntimeProfile.SmokeTest,
            _ => RuntimeProfile.HeadlessProd
        };
    }

    private static void ApplyProfileDefaults(RuntimeProfile profile)
    {
        Environment.SetEnvironmentVariable("PORTA_RUNTIME_PROFILE", profile switch
        {
            RuntimeProfile.DebugVisual => "debug_visual",
            RuntimeProfile.SmokeTest => "smoke_test",
            _ => "headless_prod"
        });

        switch (profile)
        {
            case RuntimeProfile.DebugVisual:
                SetIfMissing("PORTA_RENDER_SURFACE_ENABLED", "1");
                SetIfMissing("PORTA_VISUAL_SMOKE_TEST", "0");
                SetIfMissing("PORTA_RENDER_FRAME_STREAM_ENABLED", "1");
                break;
            case RuntimeProfile.SmokeTest:
                SetIfMissing("PORTA_RENDER_SURFACE_ENABLED", "1");
                SetIfMissing("PORTA_VISUAL_SMOKE_TEST", "1");
                SetIfMissing("PORTA_RENDER_FRAME_STREAM_ENABLED", "1");
                break;
            default:
                SetIfMissing("PORTA_RENDER_SURFACE_ENABLED", "0");
                SetIfMissing("PORTA_VISUAL_SMOKE_TEST", "0");
                SetIfMissing("PORTA_RENDER_FRAME_STREAM_ENABLED", "1");
                break;
        }
    }

    private static void SetIfMissing(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static bool ReadBoolEnvironment(string key, bool defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
