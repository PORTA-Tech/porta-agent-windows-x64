namespace Porta.Agent.Core.Buckets;

internal enum BucketLifecycleState
{
    OPEN = 0,
    PENDING_FINALIZE = 1,
    FINAL = 2
}
