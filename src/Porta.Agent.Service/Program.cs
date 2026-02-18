using System;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Runtime;

class Program
{
    static void Main()
    {
        using var host = new AgentHost();
        using var stopSignal = new ManualResetEventSlim(false);

        host.Start();

        Console.WriteLine("AgentHost started. Press ESC to stop.");
        if (Environment.UserInteractive && !Console.IsInputRedirected)
        {
            _ = Task.Run(() =>
            {
                while (!stopSignal.IsSet)
                {
                    try
                    {
                        if (Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                        {
                            stopSignal.Set();
                            break;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }
            });
        }

        stopSignal.Wait();
    }
}
