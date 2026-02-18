using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<AgentService>();
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PortaAgentService";
});

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
