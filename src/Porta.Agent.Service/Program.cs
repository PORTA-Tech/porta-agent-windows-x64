using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Runtime;
using Porta.Agent.Service;

// Ensure Kestrel always has threads even while DB workers are blocked on SQLite I/O.
// The default minimum (ProcessorCount) gets exhausted when Defender scans DB files.
System.Threading.ThreadPool.SetMinThreads(workerThreads: 64, completionPortThreads: 64);

// Defender exclusion is applied (and renewed every 60 min) by DefenderExclusionWorker.
// Group Policy refreshes strip Add-MpPreference exclusions every ~90 min; periodic renewal
// ensures the exclusion survives without manual intervention.

using SingleInstanceLease instanceLease = SingleInstanceLease.TryAcquire("Global\\PORTA.AriAgent.Service");
if (!instanceLease.HasHandle)
{
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<AgentService>();
builder.Services.AddHostedService<LocalApiHealthWatchdog>();
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AriAgent";
});

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
