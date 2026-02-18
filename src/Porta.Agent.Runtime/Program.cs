using System;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Runtime;
using Porta.Agent.Runtime.Diagnostics;

class Program
{
    static async Task Main()
    {
        using var host = new AgentHost();
        using var stopCts = new CancellationTokenSource();
        int cycleCount = ReadIntEnvironment("PORTA_START_STOP_CYCLES");
        int autoStopMs = ReadIntEnvironment("PORTA_AUTO_STOP_MS");

        host.KillSwitchTriggered += () => stopCts.Cancel();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopCts.Cancel();
        };

        try
        {
            bool suppressionEnabled = ReadBoolEnvironment("PORTA_SUPPRESSION_ENABLED", defaultValue: false);
            bool actuationEnabled = ReadBoolEnvironment("PORTA_ACTUATION_ENABLED", defaultValue: false);
            bool keyboardCaptureEnabled = ReadBoolEnvironment("PORTA_KEYBOARD_CAPTURE_ENABLED", defaultValue: true);
            host.SetScrollSuppression(suppressionEnabled);
            host.SetActuationEnabled(actuationEnabled);
            host.SetKeyboardCapture(keyboardCaptureEnabled);

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
            await host.StopAsync();
            Environment.ExitCode = 1;
        }
    }

    private static int ReadIntEnvironment(string key)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out int value) ? Math.Max(0, value) : 0;
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
