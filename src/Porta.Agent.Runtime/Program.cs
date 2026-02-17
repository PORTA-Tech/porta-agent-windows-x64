using System;
using System.Threading;
using Porta.Agent.Runtime;

class Program
{
    static void Main()
    {
        using var host = new AgentHost();
        host.Start();

        Console.WriteLine("Scroll using mouse or trackpad. Press ESC to exit.");

        // Keep running until ESC is pressed
        while (true)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                break;

            Thread.Sleep(10); // avoid busy-wait
        }

        host.Stop();
        Console.WriteLine("AgentHost stopped.");
    }
}
