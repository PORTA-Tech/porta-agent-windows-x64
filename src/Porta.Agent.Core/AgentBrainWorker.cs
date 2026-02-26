using Microsoft.Extensions.Hosting;

namespace Porta.Agent.Core;

public sealed class AgentBrainWorker(
    IAgentCoreController controller,
    ISignalAcquisitionStage acquisition,
    IPrimitiveNormalizationStage normalization,
    IInferenceStage inference,
    IStabilizationStage stabilization,
    IRegulationStage regulation,
    IBrainStateStore store,
    IBackendConnectivityProbe backendProbe) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool backendConnected = false;
        DateTimeOffset lastBackendProbeUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
                if ((nowUtc - lastBackendProbeUtc) >= TimeSpan.FromSeconds(15))
                {
                    backendConnected = await backendProbe.IsConnectedAsync(stoppingToken).ConfigureAwait(false);
                    lastBackendProbeUtc = nowUtc;
                }

                AgentControlState control = controller.GetControlState();
                if (!control.ConsentActive || control.Paused)
                {
                    string action = !control.ConsentActive ? "consent_required" : "paused_by_user";
                    AgentBrainSnapshot passiveSnapshot = new(
                        TimestampUtc: nowUtc,
                        TypingBurstIntensity: 0,
                        ScrollVelocityIndex: 0,
                        FocusSwitchRate: 0,
                        FocusStrength: 0,
                        OverloadLikelihood: 0,
                        FatigueProbability: 0,
                        CognitiveStability: 1,
                        Confidence: 1,
                        LastAction: action,
                        Contributors: ["control_state"]);
                    store.WriteSnapshot(passiveSnapshot);
                    controller.ApplyBrainSnapshot(passiveSnapshot, backendConnected);
                    controller.MarkHealthyHeartbeat();
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                RawSignalSample raw = acquisition.Capture(nowUtc);
                PrimitiveSignalSnapshot primitive = normalization.Normalize(raw);
                InferredStateSnapshot inferred = inference.Infer(primitive);
                InferredStateSnapshot stabilized = stabilization.Stabilize(inferred);
                string actionName = regulation.Evaluate(stabilized, control);
                AgentBrainSnapshot snapshot = new(
                    TimestampUtc: stabilized.TimestampUtc,
                    TypingBurstIntensity: primitive.TypingBurstIntensity,
                    ScrollVelocityIndex: primitive.ScrollVelocityIndex,
                    FocusSwitchRate: primitive.FocusSwitchRate,
                    FocusStrength: stabilized.FocusStrength,
                    OverloadLikelihood: stabilized.OverloadLikelihood,
                    FatigueProbability: stabilized.FatigueProbability,
                    CognitiveStability: stabilized.CognitiveStability,
                    Confidence: stabilized.Confidence,
                    LastAction: actionName,
                    Contributors: stabilized.Contributors);
                store.WriteSnapshot(snapshot);
                controller.ApplyBrainSnapshot(snapshot, backendConnected);
                controller.MarkHealthyHeartbeat();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                controller.MarkError($"brain_worker_failed:{ex.GetType().Name}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
