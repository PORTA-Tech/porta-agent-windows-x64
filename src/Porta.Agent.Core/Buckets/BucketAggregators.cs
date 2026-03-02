namespace Porta.Agent.Core.Buckets;

/// <summary>
/// Pure in-memory deterministic bucket mutations.
/// Same input sequence produces same bucket state.
/// </summary>
internal static class BucketAggregators
{
    internal static void ApplyEventToBucket(BucketBuffer bucket, AgentBrainSnapshot snapshot)
    {
        if (bucket.LatestSnapshot == null || snapshot.TimestampUtc >= bucket.LatestSnapshot.TimestampUtc)
        {
            bucket.LatestSnapshot = snapshot;
        }

        bucket.SampleCount++;

        foreach (string contributor in snapshot.Contributors.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            bucket.ContributorsStage.Add(contributor);
        }

        // Staged explanation fields; persisted by finalizer in later steps.
        bucket.WhyStage["last_action"] = snapshot.LastAction;
        bucket.WhyStage["policy_id"] = snapshot.PolicyId;
        bucket.WhyStage["domain"] = snapshot.Domain;

        bucket.StateFlagsStage["activation_window_triggered"] = snapshot.ActivationWindowTriggered ? "1" : "0";
        bucket.StateFlagsStage["cooldown_triggered"] = snapshot.CooldownTriggered ? "1" : "0";
        bucket.StateFlagsStage["activation_window_open"] = snapshot.ActivationWindowOpen ? "1" : "0";
    }
}
