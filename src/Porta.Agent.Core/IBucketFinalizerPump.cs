namespace Porta.Agent.Core;

public interface IBucketFinalizerPump
{
    void RunFinalizerTick(DateTimeOffset nowUtc);
}
