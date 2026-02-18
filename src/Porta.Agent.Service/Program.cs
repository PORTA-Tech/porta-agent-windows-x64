using System;
using System.Threading;
using Porta.Agent.Service;
#if WINDOWS
using System.ServiceProcess;
#endif

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
#if WINDOWS
    ServiceBase.Run(new ServiceBase[]
    {
        service
    });
#else
    service.StartForConsole();
    Thread.Sleep(Timeout.Infinite);
#endif
}
