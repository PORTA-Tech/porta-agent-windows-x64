using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

int? forcedPort = null;
string? rawPort = Environment.GetEnvironmentVariable("PORTA_LOCAL_API_PORT");
if (int.TryParse(rawPort, out int parsedPort))
{
    forcedPort = parsedPort;
}

LocalApiRuntimeSettings localApiSettings = LocalApiRuntimeSettingsStore.LoadOrCreate(forcedPort);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{localApiSettings.Port}");
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AriAgent";
});
builder.Services.AddSingleton<IBrainStateStore, SqliteBrainStateStore>();
builder.Services.AddSingleton<IAgentCoreController, AgentCoreController>();
builder.Services.AddSingleton<ISignalAcquisitionStage, SystemSignalAcquisitionStage>();
builder.Services.AddSingleton<IPrimitiveNormalizationStage, DefaultPrimitiveNormalizationStage>();
builder.Services.AddSingleton<IInferenceStage, DefaultInferenceStage>();
builder.Services.AddSingleton<IStabilizationStage, EwmaStabilizationStage>();
builder.Services.AddSingleton<IRegulationStage, DefaultRegulationStage>();
builder.Services.AddSingleton<IBackendConnectivityProbe, HttpBackendConnectivityProbe>();
builder.Services.AddHostedService<AgentBrainWorker>();
builder.Services.AddPortaLocalApi();

WebApplication app = builder.Build();
app.MapPortaLocalApi(localApiSettings);
app.MapGet("/", () => Results.Ok(new
{
    name = "porta-local-api",
    status = "ok",
    port = localApiSettings.Port,
    timestamp_utc = DateTimeOffset.UtcNow
}));

await app.RunAsync().ConfigureAwait(false);
