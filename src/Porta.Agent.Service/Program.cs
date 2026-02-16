using System;
using System.Threading.Tasks;
using Porta.Agent.Runtime;

class Program
{
    static async Task Main()
    {
        using var host = new AgentHost();
        await host.StartAsync();

        Console.WriteLine("PORTA Agent Runtime started.");
        Console.WriteLine("Scroll using mouse or trackpad. Press ESC to exit.");

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
                break;
        }

        await host.StopAsync();
        Console.WriteLine("Stopped.");
    }
}
