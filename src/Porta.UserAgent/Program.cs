using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Porta.Agent.Common.Runtime;
using Porta.Agent.Common.UserAgent;
using Porta.UserAgent;

using System.Diagnostics.Process currentProcess = System.Diagnostics.Process.GetCurrentProcess();
using SingleInstanceLease instanceLease = SingleInstanceLease.TryAcquire(
    $"Local\\PORTA.UserAgent.Session.{Math.Max(0, currentProcess.SessionId)}");
if (!instanceLease.HasHandle)
{
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IUserAgentSessionContextStore, UserAgentSessionContextStore>();
builder.Services.AddHostedService<UserAgentHost>();

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
