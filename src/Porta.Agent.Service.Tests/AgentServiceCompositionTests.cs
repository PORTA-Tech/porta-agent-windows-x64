using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Core;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service.Tests;

public sealed class AgentServiceCompositionTests
{
    [Fact]
    public void ConfigureServices_RegistersBrainWorkerAndPipelineStages()
    {
        var services = new ServiceCollection();
        var settings = new LocalApiRuntimeSettings(
            Port: 17887,
            AuthToken: null,
            LocalApiAuthReady: true,
            LocalApiAuthError: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        AgentService.ConfigureServices(services, settings);

        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(AgentBrainWorker));
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(ISignalAcquisitionStage)
                && descriptor.ImplementationType == typeof(SystemSignalAcquisitionStage));
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IPrimitiveNormalizationStage)
                && descriptor.ImplementationType == typeof(DefaultPrimitiveNormalizationStage));
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IInferenceStage)
                && descriptor.ImplementationType == typeof(DefaultInferenceStage));
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IStabilizationStage)
                && descriptor.ImplementationType == typeof(EwmaStabilizationStage));
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IRegulationStage)
                && descriptor.ImplementationType == typeof(DefaultRegulationStage));
        Assert.Contains(
            services,
            static descriptor => descriptor.ServiceType == typeof(IBackendConnectivityProbe)
                && descriptor.ImplementationType == typeof(HttpBackendConnectivityProbe));
    }
}
