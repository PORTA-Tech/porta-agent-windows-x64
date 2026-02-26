using System.Diagnostics;

namespace Porta.Agent.Core;

public sealed record RawSignalSample(
    DateTimeOffset TimestampUtc,
    double InteractionLoad,
    double ScrollLoad,
    double FocusSwitchLoad,
    double ProcessCountLoad);

public sealed record PrimitiveSignalSnapshot(
    DateTimeOffset TimestampUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    IReadOnlyList<string> Contributors);

public sealed record InferredStateSnapshot(
    DateTimeOffset TimestampUtc,
    double FocusStrength,
    double OverloadLikelihood,
    double FatigueProbability,
    double CognitiveStability,
    double Confidence,
    IReadOnlyList<string> Contributors);

public interface ISignalAcquisitionStage
{
    RawSignalSample Capture(DateTimeOffset nowUtc);
}

public interface IPrimitiveNormalizationStage
{
    PrimitiveSignalSnapshot Normalize(RawSignalSample raw);
}

public interface IInferenceStage
{
    InferredStateSnapshot Infer(PrimitiveSignalSnapshot primitive);
}

public interface IStabilizationStage
{
    InferredStateSnapshot Stabilize(InferredStateSnapshot inferred);
}

public interface IRegulationStage
{
    string Evaluate(InferredStateSnapshot stabilized, AgentControlState control);
}

public interface IBackendConnectivityProbe
{
    Task<bool> IsConnectedAsync(CancellationToken token);
}

public sealed class SystemSignalAcquisitionStage : ISignalAcquisitionStage
{
    public RawSignalSample Capture(DateTimeOffset nowUtc)
    {
        int processCount = Process.GetProcesses().Length;
        double seconds = nowUtc.ToUnixTimeMilliseconds() / 1000.0;
        double interaction = Math.Abs(Math.Sin(seconds / 6.0));
        double scroll = Math.Abs(Math.Cos(seconds / 8.0));
        double focusSwitch = Math.Abs(Math.Sin(seconds / 11.0));
        double processLoad = Math.Clamp(processCount / 400.0, 0.0, 1.0);

        return new RawSignalSample(
            TimestampUtc: nowUtc,
            InteractionLoad: interaction,
            ScrollLoad: scroll,
            FocusSwitchLoad: focusSwitch,
            ProcessCountLoad: processLoad);
    }
}

public sealed class DefaultPrimitiveNormalizationStage : IPrimitiveNormalizationStage
{
    public PrimitiveSignalSnapshot Normalize(RawSignalSample raw)
    {
        double typingBurst = Math.Clamp((raw.InteractionLoad * 0.7) + (raw.ProcessCountLoad * 0.3), 0, 1);
        double scrollVelocity = Math.Clamp((raw.ScrollLoad * 0.75) + (raw.InteractionLoad * 0.25), 0, 1);
        double focusSwitchRate = Math.Clamp((raw.FocusSwitchLoad * 0.8) + (raw.ProcessCountLoad * 0.2), 0, 1);
        var contributors = new List<string>(3);
        if (typingBurst > 0.6) contributors.Add("interaction_load");
        if (scrollVelocity > 0.6) contributors.Add("scroll_load");
        if (focusSwitchRate > 0.6) contributors.Add("focus_switch_load");
        if (contributors.Count == 0) contributors.Add("low_activity");

        return new PrimitiveSignalSnapshot(raw.TimestampUtc, typingBurst, scrollVelocity, focusSwitchRate, contributors);
    }
}

public sealed class DefaultInferenceStage : IInferenceStage
{
    public InferredStateSnapshot Infer(PrimitiveSignalSnapshot primitive)
    {
        double overload = Math.Clamp(
            (primitive.ScrollVelocityIndex * 0.45) +
            (primitive.FocusSwitchRate * 0.35) +
            (primitive.TypingBurstIntensity * 0.20),
            0, 1);

        double focus = Math.Clamp(
            (primitive.TypingBurstIntensity * 0.5) +
            ((1 - primitive.FocusSwitchRate) * 0.3) +
            ((1 - overload) * 0.2),
            0, 1);

        double fatigue = Math.Clamp((overload * 0.6) + ((1 - focus) * 0.4), 0, 1);
        double stability = Math.Clamp(1.0 - Math.Abs(overload - focus), 0, 1);
        double confidence = Math.Clamp(0.55 + (primitive.TypingBurstIntensity * 0.25) + (stability * 0.20), 0, 1);

        var contributors = new List<string>();
        if (overload >= 0.7) contributors.Add("high_overload_likelihood");
        if (fatigue >= 0.7) contributors.Add("high_fatigue_probability");
        if (focus >= 0.7) contributors.Add("high_focus_strength");
        if (contributors.Count == 0) contributors.Add("balanced_profile");

        return new InferredStateSnapshot(
            primitive.TimestampUtc,
            focus,
            overload,
            fatigue,
            stability,
            confidence,
            contributors);
    }
}

public sealed class EwmaStabilizationStage : IStabilizationStage
{
    private readonly object _gate = new();
    private InferredStateSnapshot? _previous;

    public InferredStateSnapshot Stabilize(InferredStateSnapshot inferred)
    {
        lock (_gate)
        {
            const double alpha = 0.35;
            if (_previous == null)
            {
                _previous = inferred;
                return inferred;
            }

            var smoothed = inferred with
            {
                FocusStrength = Blend(_previous.FocusStrength, inferred.FocusStrength, alpha),
                OverloadLikelihood = Blend(_previous.OverloadLikelihood, inferred.OverloadLikelihood, alpha),
                FatigueProbability = Blend(_previous.FatigueProbability, inferred.FatigueProbability, alpha),
                CognitiveStability = Blend(_previous.CognitiveStability, inferred.CognitiveStability, alpha),
                Confidence = Blend(_previous.Confidence, inferred.Confidence, alpha)
            };
            _previous = smoothed;
            return smoothed;
        }
    }

    private static double Blend(double previous, double current, double alpha)
    {
        return Math.Clamp((alpha * current) + ((1 - alpha) * previous), 0, 1);
    }
}

public sealed class DefaultRegulationStage : IRegulationStage
{
    public string Evaluate(InferredStateSnapshot stabilized, AgentControlState control)
    {
        if (!control.ConsentActive)
        {
            return "consent_required";
        }

        if (control.Paused)
        {
            return "paused_by_user";
        }

        if (stabilized.OverloadLikelihood > 0.80)
        {
            return "focus_mode_enabled";
        }

        if (stabilized.FatigueProbability > 0.75)
        {
            return "break_suggestion";
        }

        if (stabilized.FocusStrength > 0.70)
        {
            return "maintain_focus";
        }

        return "monitor_only";
    }
}

public sealed class HttpBackendConnectivityProbe : IBackendConnectivityProbe
{
    private readonly string? _backend = Environment.GetEnvironmentVariable("PORTA_BACKEND_URL");

    public async Task<bool> IsConnectedAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_backend) || !Uri.TryCreate(_backend, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpRequestMessage request = new(HttpMethod.Head, uri);
            using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
