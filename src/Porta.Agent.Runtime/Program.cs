using System;
using System.Threading.Tasks;
using Porta.Agent.Runtime;

internal class Program
{
    static async Task Main()
    {
        Console.WriteLine("PORTA Agent Runtime started.");
        Console.WriteLine("Scroll using trackpad.");
        Console.WriteLine("Press ESC to exit.");

        using var host = new AgentHost();
        await host.StartAsync();

        while (Console.ReadKey(true).Key != ConsoleKey.Escape)
        {
            // keep running
        }

        await host.StopAsync();
    }
}
