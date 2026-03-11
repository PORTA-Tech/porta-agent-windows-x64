using Porta.Agent.Common.Identity;

namespace Porta.Agent.Service.Tests;

public sealed class LocalIdentityLinkStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public LocalIdentityLinkStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "porta-identity-link-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void GetCurrent_CreatesStableDeviceIdAndUnlinkedState()
    {
        string path = Path.Combine(_tempDirectory, "identity-link.json");
        var store = new LocalIdentityLinkStore(path);

        LocalIdentityLinkSnapshot first = store.GetCurrent();
        LocalIdentityLinkSnapshot second = store.GetCurrent();

        Assert.True(Guid.TryParse(first.DeviceId, out _));
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(LocalIdentityLinkStatuses.Unlinked, first.LinkStatus);
        Assert.Null(first.AccountId);
        Assert.Null(first.OrgId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SetLink_AndClearLink_PreserveDeviceIdAndPersistState()
    {
        string path = Path.Combine(_tempDirectory, "identity-link.json");
        var store = new LocalIdentityLinkStore(path);
        string deviceId = store.GetCurrent().DeviceId;
        string accountId = Guid.NewGuid().ToString("D");
        string orgId = Guid.NewGuid().ToString("D");

        LocalIdentityLinkSnapshot linked = store.SetLink(accountId, orgId);
        LocalIdentityLinkSnapshot reloadedLinked = new LocalIdentityLinkStore(path).GetCurrent();
        LocalIdentityLinkSnapshot cleared = store.ClearLink();

        Assert.Equal(deviceId, linked.DeviceId);
        Assert.Equal(LocalIdentityLinkStatuses.Linked, linked.LinkStatus);
        Assert.Equal(accountId, linked.AccountId);
        Assert.Equal(orgId, linked.OrgId);
        Assert.NotNull(linked.LinkedAtUtc);

        Assert.Equal(deviceId, reloadedLinked.DeviceId);
        Assert.Equal(LocalIdentityLinkStatuses.Linked, reloadedLinked.LinkStatus);
        Assert.Equal(accountId, reloadedLinked.AccountId);
        Assert.Equal(orgId, reloadedLinked.OrgId);

        Assert.Equal(deviceId, cleared.DeviceId);
        Assert.Equal(LocalIdentityLinkStatuses.Unlinked, cleared.LinkStatus);
        Assert.Null(cleared.AccountId);
        Assert.Null(cleared.OrgId);
        Assert.Null(cleared.LinkedAtUtc);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup issues on Windows file handles.
        }
    }
}
