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
            // Category bridge (v1.2.112): pre-warm this process's classifier registries from
            // the persisted label/category files so hashes stored in old buckets have their
            // categories right after a restart, before they re-arrive via ingest. Best-effort.
            Fleet.CategoryRegistrySeeder.SeedFromFiles(force: true);
            return controller;
        });
        services.AddHostedService<AgentBrainWorker>();
        services.AddHostedService<BucketFinalizerWorker>();
        services.AddSingleton<ICurrentSessionUserAgentRuntime, WindowsCurrentSessionUserAgentRuntime>();
        services.AddSingleton<CurrentSessionUserAgentWatchdogCoordinator>(sp =>
            new CurrentSessionUserAgentWatchdogCoordinator(
                sp.GetRequiredService<ICurrentSessionUserAgentRuntime>(),
                sp.GetRequiredService<ILogger<CurrentSessionUserAgentWatchdog>>(),
                sp.GetRequiredService<IAgentCoreController>()));
        services.AddHostedService<CurrentSessionUserAgentWatchdog>();
        services.AddSingleton<ControlPlaneClient>();
        // Part 5 — /v1/narrative: the user agent's card asks here; this forwards to the control
        // plane where the LLM + key live. Fail-soft to null (template) at every layer.
        services.AddSingleton<INarrativeGateway, NarrativeGateway>();
        services.AddSingleton<AriContextCache>();
        services.AddSingleton<IAriContextProvider>(sp => sp.GetRequiredService<AriContextCache>());
        services.AddSingleton<ControlPlaneReporterStateStore>();
        services.AddSingleton<MachineSummaryMapper>();
        services.AddSingleton<MachineActionMapper>();
        services.AddHostedService<ControlPlaneReporter>();
        services.AddHostedService<DailyDigestReporter>();
        services.AddHostedService<DailyStatsReporter>();
        services.AddHostedService<UserContextRefresher>();
        services.AddHostedService<DefenderExclusionWorker>();
        services.AddHostedService<AgentUpdateWorker>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        // v1.2.112 (B2): heal the shared-folder ACL before anything else touches it.
        // Best-effort, idempotent, never blocks start.
        SharedDataAclHealer.EnsureSharedFolderWritable();

        try
        {
            string dbPath = CoreStateDbStartup.InitializeAndMigrate(logPragmasAtBoot: true);
            Console.WriteLine($"Core state DB initialized at {dbPath}.");
        }
        catch (Exception ex)
        {
            // DB migration failure is non-fatal: the service continues in degraded mode
            // (in-memory state, no local persistence) rather than crashing. A crash would
            // trigger Windows SCM recovery restarts which exhaust the retry budget (3/day)
            // and leave the service permanently stopped until manual intervention.
            Console.WriteLine($"Core state DB startup failed — continuing in degraded mode: {ex.GetType().Name} {ex.Message}");
            CoreStateDbStartup.TrySetMigrationFailed();
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
            // SAFEGUARD: this catch used to swallow the local-API start failure into a Console the
            // Windows service has no terminal for, leaving ari.exe alive but NOT listening, with no
            // trace of why. That exact silent failure (a schema-guard mismatch) cost a full day of
            // debugging on 2026-07-09. Persist the full exception so a dead listener is never
            // invisible again.
            try
            {
                string diagPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "PortaAgent",
                    "local-api-start-error.log");
                System.IO.File.AppendAllText(
                    diagPath,
                    $"[{DateTimeOffset.UtcNow:O}] local_api_start_failed: {ex}\n\n");
            }
            catch
            {
            }
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
