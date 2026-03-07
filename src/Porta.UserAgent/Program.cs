using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Porta.Agent.Common.UserAgent;
using Porta.UserAgent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IUserAgentSessionContextStore, UserAgentSessionContextStore>();
builder.Services.AddHostedService<UserAgentHost>();

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
