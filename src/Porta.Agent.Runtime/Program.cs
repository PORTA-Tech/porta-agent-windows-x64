using System;
using System.Threading.Tasks;

namespace Porta.Agent.Runtime;

class Program
{
    static async Task Main(string[] args)
    {
        using var host = new AgentHost();

        await host.StartAsync();

        Console.WriteLine("PORTA Agent Runtime started.");
        Console.WriteLine("Spin your mouse wheel to see scroll deltas.");
        Console.WriteLine("Press ESC to exit.");

        while (Console.ReadKey(true).Key != ConsoleKey.Escape) { }

        await host.StopAsync();
        Console.WriteLine("PORTA Agent Runtime stopped.");
    }
}
