using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Porta.Agent.Runtime.Layer3;

internal sealed class Layer3InferenceEngine
{
    private readonly Layer3InferenceConfig _config;
    private readonly object _gate = new();
    private readonly Dictionary<string, Layer3PrimitiveInput> _latestByMetric = new(StringComparer.Ordinal);
    private double _previousGlobalRisk;
    private string _previousDominantState = "stable";
    private bool _hasPrevious;

    public Layer3InferenceEngine(Layer3InferenceConfig config)
    {
        _config = config;
    }

    public void RecordPrimitive(Layer3PrimitiveInput primitive)
    {
        lock (_gate)
        {
            _latestByMetric[primitive.Name] = primitive;
        }
    }

    public Layer3StateResult Evaluate(DateTimeOffset nowUtc)
    {
        try
        {
            List<(string name, double weight, double z, double confidence, double metricRisk, double contribution)> contributors = new();
            double presentWeight = 0;
            double weightedRisk = 0;
            double confidenceAccumulator = 0;
            int confidenceCount = 0;

            lock (_gate)
            {
                foreach ((string metric, double weight) in _config.MetricWeights)
                {
                    if (!_latestByMetric.TryGetValue(metric, out Layer3PrimitiveInput primitive))
                    {
                        continue;
                    }

                    if ((nowUtc - primitive.ComputedAtUtc).TotalSeconds > _config.PrimitiveStaleAfterSeconds)
                    {
                        continue;
                    }

                    double anomalyScore = Math.Abs(primitive.ZScore);
                    double metricRisk = Math.Clamp(anomalyScore / _config.ZMax, 0, 1);
                    double contribution = metricRisk * weight;
                    contributors.Add((metric, weight, primitive.ZScore, primitive.Confidence, metricRisk, contribution));
                    presentWeight += weight;
                    weightedRisk += contribution;
                    confidenceAccumulator += primitive.Confidence;
                    confidenceCount++;
                }
            }

            double globalRisk = Math.Clamp(weightedRisk, 0, 1);
            double riskDelta = _hasPrevious ? globalRisk - _previousGlobalRisk : 0;
            string trend = riskDelta > _config.TrendEpsilon ? "up" : riskDelta < -_config.TrendEpsilon ? "down" : "flat";

            string thresholdBucket;
            Dictionary<string, double> distribution = BuildBaseDistribution(globalRisk, out thresholdBucket);

            bool trendDetected = trend == "down" && _previousDominantState != "stable";
            if (trendDetected)
            {
                double recovering = Math.Min(0.35, Math.Abs(riskDelta) * 1.5 + 0.1);
                ScaleDistribution(distribution, 1.0 - recovering);
                distribution["recovering"] = Math.Clamp(recovering, 0, 1);
            }
            else
            {
                distribution["recovering"] = 0;
            }

            NormalizeDistribution(distribution);
            (string dominantState, double dominantProbability) = distribution.OrderByDescending(kv => kv.Value).First();

            double avgPrimitiveConfidence = confidenceCount == 0 ? 0 : confidenceAccumulator / confidenceCount;
            double coverageFactor = Math.Clamp(presentWeight, 0, 1);
            double stabilityFactor = 1.0 - Math.Min(1.0, Math.Abs(riskDelta) / 0.5);
            double stateConfidence = Math.Clamp(avgPrimitiveConfidence * coverageFactor * stabilityFactor, 0, 1);

            var contributingJson = JsonSerializer.Serialize(
                contributors
                    .OrderByDescending(c => Math.Abs(c.contribution))
                    .Select(c => new
                    {
                        primitive_name = c.name,
                        z_score = Round(c.z),
                        weight = Round(c.weight),
                        contribution_to_global_risk = Round(c.contribution)
                    }));

            var topDrivers = contributors
                .OrderByDescending(c => Math.Abs(c.contribution))
                .Take(3)
                .Select(c => new { primitive_name = c.name, contribution = Round(c.contribution) });

            var explanation = JsonSerializer.Serialize(new
            {
                risk_breakdown = new
                {
                    global_risk_score = Round(globalRisk),
                    risk_delta = Round(riskDelta),
                    trend_direction = trend
                },
                top_drivers = topDrivers,
                decision_path = new
                {
                    aggregation_model_version = _config.ModelVersion,
                    threshold_bucket = thresholdBucket,
                    trend_detected = trendDetected
                },
                full_distribution = distribution.ToDictionary(k => k.Key, v => Round(v.Value))
            });

            string distributionJson = JsonSerializer.Serialize(distribution.ToDictionary(k => k.Key, v => Round(v.Value)));
            string stateId = BuildStateId(dominantState, nowUtc, globalRisk);

            _previousGlobalRisk = globalRisk;
            _previousDominantState = dominantState;
            _hasPrevious = true;

            return new Layer3StateResult(
                Success: true,
                StateId: stateId,
                StateName: dominantState,
                Probability: Round(dominantProbability),
                Confidence: Round(stateConfidence),
                GlobalRiskScore: Round(globalRisk),
                RiskDelta: Round(riskDelta),
                TrendDirection: trend,
                ModelVersion: _config.ModelVersion,
                DistributionJson: distributionJson,
                ContributingPrimitivesJson: contributingJson,
                ExplanationJson: explanation,
                EvaluatedAtUtc: nowUtc,
                ErrorReason: string.Empty);
        }
        catch
        {
            return Layer3StateResult.Error("layer3_math_failure", nowUtc);
        }
    }

    private static Dictionary<string, double> BuildBaseDistribution(double globalRisk, out string thresholdBucket)
    {
        if (globalRisk < 0.2)
        {
            thresholdBucket = "lt_0.2";
            return new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["stable"] = 0.9,
                ["elevated_risk"] = 0.1,
                ["degrading"] = 0.0,
                ["critical"] = 0.0
            };
        }

        if (globalRisk < 0.4)
        {
            thresholdBucket = "0.2_to_0.4";
            return new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["stable"] = 0.3,
                ["elevated_risk"] = 0.6,
                ["degrading"] = 0.1,
                ["critical"] = 0.0
            };
        }

        if (globalRisk < 0.7)
        {
            thresholdBucket = "0.4_to_0.7";
            return new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["stable"] = 0.1,
                ["elevated_risk"] = 0.4,
                ["degrading"] = 0.4,
                ["critical"] = 0.1
            };
        }

        thresholdBucket = "gt_0.7";
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["stable"] = 0.0,
            ["elevated_risk"] = 0.1,
            ["degrading"] = 0.3,
            ["critical"] = 0.6
        };
    }

    private static void ScaleDistribution(Dictionary<string, double> dist, double scale)
    {
        List<string> keys = dist.Keys.ToList();
        foreach (string key in keys)
        {
            dist[key] = Math.Clamp(dist[key] * scale, 0, 1);
        }
    }

    private static void NormalizeDistribution(Dictionary<string, double> dist)
    {
        double sum = dist.Values.Sum();
        if (sum <= 0)
        {
            dist.Clear();
            dist["stable"] = 1;
            dist["elevated_risk"] = 0;
            dist["degrading"] = 0;
            dist["critical"] = 0;
            dist["recovering"] = 0;
            return;
        }

        List<string> keys = dist.Keys.ToList();
        foreach (string key in keys)
        {
            dist[key] = dist[key] / sum;
        }
    }

    private static string BuildStateId(string stateName, DateTimeOffset atUtc, double risk)
    {
        string seed = $"{stateName}|{atUtc.ToUnixTimeMilliseconds()}|{risk:F6}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
