using System;
using System.ServiceProcess;
using System.Threading;
using Porta.Agent.Service;

var service = new AgentService();

if (Environment.UserInteractive)
{
    Console.WriteLine("Starting PortaAgentService in console mode. Press Ctrl+C to stop.");
    using var stopSignal = new ManualResetEventSlim(false);

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopSignal.Set();
    };

    service.StartForConsole();
    stopSignal.Wait();
    service.StopForConsole();
}
else
{
    ServiceBase.Run(new ServiceBase[]
    {
        service
    });
}
