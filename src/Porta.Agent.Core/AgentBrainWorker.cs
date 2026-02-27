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
        PrimitiveSignalSnapshot? previousSmoothedPrimitive = null;

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
                    // Hard gate: while paused/no-consent, do not collect or persist new samples.
                    previousSmoothedPrimitive = null;
                    controller.MarkHealthyHeartbeat();
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                RawSignalSample raw = acquisition.Capture(nowUtc);
                PrimitiveSignalSnapshot primitive = normalization.Normalize(raw);
                PrimitiveSignalSnapshot smoothedPrimitive = SmoothPrimitive(primitive, previousSmoothedPrimitive, alpha: 0.35);
                previousSmoothedPrimitive = smoothedPrimitive;

                InferredStateSnapshot inferred = inference.Infer(smoothedPrimitive);
                InferredStateSnapshot stabilized = stabilization.Stabilize(inferred);
                RegulationDecisionSnapshot decision = regulation.Evaluate(stabilized, control);
                (string dominantStateName, double dominantStateValue) = ResolveDominantState(stabilized);

                AgentBrainSnapshot snapshot = new(
                    TimestampUtc: stabilized.TimestampUtc,
                    TypingBurstIntensity: primitive.TypingBurstIntensity,
                    ScrollVelocityIndex: primitive.ScrollVelocityIndex,
                    FocusSwitchRate: primitive.FocusSwitchRate,
                    SmoothedTypingBurstIntensity: smoothedPrimitive.TypingBurstIntensity,
                    SmoothedScrollVelocityIndex: smoothedPrimitive.ScrollVelocityIndex,
                    SmoothedFocusSwitchRate: smoothedPrimitive.FocusSwitchRate,
                    FocusStrength: stabilized.FocusStrength,
                    OverloadLikelihood: stabilized.OverloadLikelihood,
                    FatigueProbability: stabilized.FatigueProbability,
                    CognitiveStability: stabilized.CognitiveStability,
                    DominantStateValue: dominantStateValue,
                    DominantStateName: dominantStateName,
                    Confidence: stabilized.Confidence,
                    LastAction: decision.Action,
                    ActivationWindowTriggered: decision.ActivationWindowTriggered,
                    CooldownTriggered: decision.CooldownTriggered,
                    ActivationWindowOpen: decision.ActivationWindowOpen,
                    Contributors: stabilized.Contributors,
                    PolicyId: decision.PolicyId,
                    Domain: decision.Domain,
                    EscalationLevel: decision.EscalationLevel,
                    PlannedEndTimestampUtc: decision.PlannedEndTimestampUtc,
                    CooldownEndTimestampUtc: decision.CooldownEndTimestampUtc);
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

    private static PrimitiveSignalSnapshot SmoothPrimitive(
        PrimitiveSignalSnapshot current,
        PrimitiveSignalSnapshot? previous,
        double alpha)
    {
        if (previous == null)
        {
            return current;
        }

        return current with
        {
            TypingBurstIntensity = Blend(previous.TypingBurstIntensity, current.TypingBurstIntensity, alpha),
            ScrollVelocityIndex = Blend(previous.ScrollVelocityIndex, current.ScrollVelocityIndex, alpha),
            FocusSwitchRate = Blend(previous.FocusSwitchRate, current.FocusSwitchRate, alpha)
        };
    }

    private static (string StateName, double StateValue) ResolveDominantState(InferredStateSnapshot state)
    {
        double dominant = Math.Max(state.FocusStrength, Math.Max(state.OverloadLikelihood, state.FatigueProbability));
        if (dominant == state.FocusStrength)
        {
            return ("focus", dominant);
        }

        if (dominant == state.OverloadLikelihood)
        {
            return ("overload", dominant);
        }

        return ("fatigue", dominant);
    }

    private static double Blend(double previous, double current, double alpha)
    {
        return Math.Clamp((alpha * current) + ((1 - alpha) * previous), 0, 1);
    }
}
