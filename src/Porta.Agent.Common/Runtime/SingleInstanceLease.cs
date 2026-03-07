using System.Threading;

namespace Porta.Agent.Common.Runtime;

public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstanceLease(string name, Mutex? mutex, bool hasHandle)
    {
        Name = name;
        _mutex = mutex;
        HasHandle = hasHandle;
    }

    public string Name { get; }

    public bool HasHandle { get; }

    public static SingleInstanceLease TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Mutex mutex = new(initiallyOwned: false, name);
        bool hasHandle;
        try
        {
            hasHandle = mutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            hasHandle = true;
        }

        if (!hasHandle)
        {
            mutex.Dispose();
            return new SingleInstanceLease(name, mutex: null, hasHandle: false);
        }

        return new SingleInstanceLease(name, mutex, hasHandle: true);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
