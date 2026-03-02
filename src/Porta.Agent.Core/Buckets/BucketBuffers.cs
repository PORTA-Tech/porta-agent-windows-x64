namespace Porta.Agent.Core.Buckets;

/// <summary>
/// In-memory bucket store keyed by bucket_seq.
/// </summary>
internal sealed class BucketBuffers
{
    private readonly object _gate = new();
    private readonly Dictionary<long, BucketBuffer> _buffers = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _buffers.Count;
            }
        }
    }

    public BucketBuffer GetOrCreate(long bucketSeq)
    {
        lock (_gate)
        {
            if (_buffers.TryGetValue(bucketSeq, out BucketBuffer? existing))
            {
                return existing;
            }

            var created = new BucketBuffer(bucketSeq);
            _buffers[bucketSeq] = created;
            return created;
        }
    }

    public bool TryGet(long bucketSeq, out BucketBuffer? buffer)
    {
        lock (_gate)
        {
            return _buffers.TryGetValue(bucketSeq, out buffer);
        }
    }

    public bool Remove(long bucketSeq)
    {
        lock (_gate)
        {
            return _buffers.Remove(bucketSeq);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffers.Clear();
        }
    }

    public IReadOnlyList<long> OrderedBucketSeqs()
    {
        lock (_gate)
        {
            return _buffers.Keys.OrderBy(static k => k).ToArray();
        }
    }
}
