using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.Common.Identity;
using Porta.Agent.Core;
using Porta.Agent.Core.Db;
using Porta.Agent.Core.Outputs;
using Porta.Agent.Service.Fleet;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service;

internal sealed class AgentService : IHostedService, IDisposable
{
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

        ConfigureServices(builder.Services, settings);

        _localApiApp = builder.Build();
        _ = _localApiApp.Services.GetRequiredService<ILocalIdentityLinkStore>().GetCurrent();
        _localApiApp.MapPortaLocalApi(settings);
    }

    internal static void ConfigureServices(IServiceCollection services, LocalApiRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPortaLocalApi();
        services.AddSingleton<ILocalIdentityLinkStore, LocalIdentityLinkStore>();
        services.AddSingleton<IRuntimePolicyProvider, FileRuntimePolicyProvider>();
        services.AddSingleton<IPolicyEnvelopeProvider, FilePolicyEnvelopeProvider>();
        services.AddSingleton<IBrightnessController, WindowsBrightnessController>();
        services.AddSingleton<IOverloadBrightnessActionCoordinator, OverloadBrightnessActionCoordinator>();
        services.AddSingleton<IDeepWorkOutputActionCoordinator, QueuedDeepWorkOutputActionCoordinator>();
        services.AddSingleton<ISignalAcquisitionStage, SystemSignalAcquisitionStage>();
        services.AddSingleton<IPrimitiveNormalizationStage, DefaultPrimitiveNormalizationStage>();
        services.AddSingleton<IInferenceStage, DefaultInferenceStage>();
        services.AddSingleton<IStabilizationStage, EwmaStabilizationStage>();
        services.AddSingleton<IRegulationStage, DefaultRegulationStage>();
        services.AddSingleton<IBackendConnectivityProbe, HttpBackendConnectivityProbe>();
        services.AddSingleton<BucketFinalizerStateStore>(sp =>
            new BucketFinalizerStateStore(new SqliteBrainStateStore(
                sp.GetRequiredService<IDeepWorkOutputActionCoordinator>(),
                sp.GetRequiredService<IOverloadBrightnessActionCoordinator>(),
                sp.GetRequiredService<IRuntimePolicyProvider>())));
        services.AddSingleton<IBrainStateStore>(sp => sp.GetRequiredService<BucketFinalizerStateStore>());
        services.AddSingleton<IBucketFinalizerPump>(sp => sp.GetRequiredService<BucketFinalizerStateStore>());
        services.AddSingleton<IAgentCoreController>(sp =>
        {
            var controller = new AgentCoreController(
                sp.GetRequiredService<IBrainStateStore>(),
                runtimePolicyProvider: sp.GetRequiredService<IRuntimePolicyProvider>());
            controller.SetLocalApiAuthState(settings.LocalApiAuthReady, settings.LocalApiAuthError);
            return controller;
        });
        services.AddHostedService<AgentBrainWorker>();
        services.AddHostedService<BucketFinalizerWorker>();
        services.AddSingleton<ICurrentSessionUserAgentRuntime, WindowsCurrentSessionUserAgentRuntime>();
        services.AddSingleton<CurrentSessionUserAgentWatchdogCoordinator>(sp =>
            new CurrentSessionUserAgentWatchdogCoordinator(
                sp.GetRequiredService<ICurrentSessionUserAgentRuntime>(),
                sp.GetRequiredService<ILogger<CurrentSessionUserAgentWatchdog>>()));
        services.AddHostedService<CurrentSessionUserAgentWatchdog>();
        services.AddSingleton<ControlPlaneClient>();
        services.AddSingleton<ControlPlaneReporterStateStore>();
        services.AddSingleton<MachineSummaryMapper>();
        services.AddSingleton<MachineActionMapper>();
        services.AddHostedService<ControlPlaneReporter>();
        services.AddHostedService<DefenderExclusionWorker>();
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

    }

    public void Dispose()
    {
        _localApiApp.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
