using Porta.Agent.Common.Runtime;

namespace Porta.Agent.Core.Tests;

public sealed class SingleInstanceLeaseTests
{
    [Fact]
    public void TryAcquire_BlocksSecondBackendOwner_ForProductionServiceName()
    {
        string name = $"Global\\PORTA.AriAgent.Service.Tests.{Guid.NewGuid():N}";
        using SingleInstanceLease first = SingleInstanceLease.TryAcquire(name);

        Assert.True(first.HasHandle);
        Assert.False(TryAcquireOnWorkerThread(name));
    }

    [Fact]
    public void TryAcquire_BlocksSecondUserAgentOwner_InSameSessionName()
    {
        string name = $"Local\\PORTA.UserAgent.Session.5.Tests.{Guid.NewGuid():N}";
        using SingleInstanceLease first = SingleInstanceLease.TryAcquire(name);

        Assert.True(first.HasHandle);
        Assert.False(TryAcquireOnWorkerThread(name));
    }

    [Fact]
    public void TryAcquire_AllowsUserAgentOwners_InDifferentSessions()
    {
        string firstName = $"Local\\PORTA.UserAgent.Session.5.Tests.{Guid.NewGuid():N}";
        string secondName = $"Local\\PORTA.UserAgent.Session.6.Tests.{Guid.NewGuid():N}";
        using SingleInstanceLease first = SingleInstanceLease.TryAcquire(firstName);
        using SingleInstanceLease second = SingleInstanceLease.TryAcquire(secondName);

        Assert.True(first.HasHandle);
        Assert.True(second.HasHandle);
    }

    [Fact]
    public void TryAcquire_BlocksSecondLeaseUntilFirstIsDisposed()
    {
        string name = $"Local\\PORTA.Tests.{Guid.NewGuid():N}";

        using SingleInstanceLease first = SingleInstanceLease.TryAcquire(name);
        bool secondHasHandle = true;
        using var secondAcquire = new ManualResetEventSlim(false);
        using var secondDone = new ManualResetEventSlim(false);

        Thread thread = new(() =>
        {
            using SingleInstanceLease second = SingleInstanceLease.TryAcquire(name);
            secondHasHandle = second.HasHandle;
            secondAcquire.Set();
            secondDone.Set();
        });

        thread.IsBackground = true;
        thread.Start();
        Assert.True(secondAcquire.Wait(TimeSpan.FromSeconds(2)), "Expected secondary acquire attempt to complete.");
        Assert.True(secondDone.Wait(TimeSpan.FromSeconds(2)), "Expected secondary acquire thread to finish.");

        Assert.True(first.HasHandle);
        Assert.False(secondHasHandle);
    }

    [Fact]
    public void TryAcquire_AllowsReacquireAfterDispose()
    {
        string name = $"Local\\PORTA.Tests.{Guid.NewGuid():N}";

        SingleInstanceLease first = SingleInstanceLease.TryAcquire(name);
        Assert.True(first.HasHandle);
        first.Dispose();

        using SingleInstanceLease second = SingleInstanceLease.TryAcquire(name);
        Assert.True(second.HasHandle);
    }

    private static bool TryAcquireOnWorkerThread(string name)
    {
        bool secondHasHandle = true;
        using var secondAcquire = new ManualResetEventSlim(false);
        using var secondDone = new ManualResetEventSlim(false);

        Thread thread = new(() =>
        {
            using SingleInstanceLease second = SingleInstanceLease.TryAcquire(name);
            secondHasHandle = second.HasHandle;
            secondAcquire.Set();
            secondDone.Set();
        });

        thread.IsBackground = true;
        thread.Start();
        Assert.True(secondAcquire.Wait(TimeSpan.FromSeconds(2)), "Expected secondary acquire attempt to complete.");
        Assert.True(secondDone.Wait(TimeSpan.FromSeconds(2)), "Expected secondary acquire thread to finish.");
        return secondHasHandle;
    }
}
