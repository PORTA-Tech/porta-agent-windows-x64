using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Core;
using Porta.Agent.Core.Db;
using Porta.Agent.LocalApi;
using Porta.Agent.Runtime.Layer1;

namespace Porta.Agent.Service;

internal sealed class AgentService : IHostedService, IDisposable
{
    private readonly Layer1AgentHost _agentHost = new();
    private readonly WebApplication _localApiApp;
    private int _started;
    private int _localApiStarted;

    public AgentService()
    {
        LocalApiRuntimeSettings settings = LocalApiRuntimeSettingsStore.LoadOrCreate();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.UseKestrel(options =>
        {
            options.ListenLocalhost(settings.Port);
        });

        builder.Services.AddPortaLocalApi();
        builder.Services.AddSingleton<BucketFinalizerStateStore>(_ =>
            new BucketFinalizerStateStore(new SqliteBrainStateStore()));
        builder.Services.AddSingleton<IBrainStateStore>(sp => sp.GetRequiredService<BucketFinalizerStateStore>());
        builder.Services.AddSingleton<IBucketFinalizerPump>(sp => sp.GetRequiredService<BucketFinalizerStateStore>());
        builder.Services.AddSingleton<IAgentCoreController>(sp =>
        {
            var controller = new AgentCoreController(sp.GetRequiredService<IBrainStateStore>());
            controller.SetLocalApiAuthState(settings.LocalApiAuthReady, settings.LocalApiAuthError);
            return controller;
        });
        builder.Services.AddSingleton<ISignalAcquisitionStage, SystemSignalAcquisitionStage>();
        builder.Services.AddSingleton<IPrimitiveNormalizationStage, DefaultPrimitiveNormalizationStage>();
        builder.Services.AddSingleton<IInferenceStage, DefaultInferenceStage>();
        builder.Services.AddSingleton<IStabilizationStage, EwmaStabilizationStage>();
        builder.Services.AddSingleton<IPolicyEnvelopeProvider, FilePolicyEnvelopeProvider>();
        builder.Services.AddSingleton<IRegulationStage, DefaultRegulationStage>();
        builder.Services.AddSingleton<IBackendConnectivityProbe, HttpBackendConnectivityProbe>();
        builder.Services.AddHostedService<RuntimeSignalIngestionWorker>();
        builder.Services.AddHostedService<BucketFinalizerWorker>();
        builder.Services.AddHostedService<AgentBrainWorker>();

        _localApiApp = builder.Build();
        _localApiApp.MapPortaLocalApi(settings);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            string dbPath = CoreStateDbStartup.InitializeAndMigrate(logPragmasAtBoot: true);
            Console.WriteLine($"Core state DB initialized at {dbPath}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Core state DB startup failed: {ex.GetType().Name} {ex.Message}");
            CoreStateDbStartup.TrySetMigrationFailed();
            Interlocked.Exchange(ref _started, 0);
            throw;
        }

        await _agentHost.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _localApiApp.StartAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _localApiStarted, 1);
            Console.WriteLine("Local API host started.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Local API host failed to start: {ex.GetType().Name} {ex.Message}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _localApiStarted, 0) == 1)
        {
            await _localApiApp.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await _agentHost.StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _localApiApp.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _agentHost.Dispose();
    }
}
