using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Runtime;
using Porta.Agent.Service;

using SingleInstanceLease instanceLease = SingleInstanceLease.TryAcquire("Global\\PORTA.AriAgent.Service");
if (!instanceLease.HasHandle)
{
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<AgentService>();
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AriAgent";
});

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
