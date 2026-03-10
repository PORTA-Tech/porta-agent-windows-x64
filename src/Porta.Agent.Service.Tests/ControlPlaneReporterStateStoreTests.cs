using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

public sealed class ControlPlaneReporterStateStoreTests : IDisposable
{
    private readonly string _path;

    public ControlPlaneReporterStateStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"porta-fleet-state-{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void SaveAndLoad_RoundTripsLastSentCursor()
    {
        var store = new ControlPlaneReporterStateStore(_path);
        var expected = new ControlPlaneReporterState(
            SchemaVersion: 1,
            LastSentEventId: 123,
            LastSummarySuccessUtc: DateTimeOffset.Parse("2026-03-10T10:00:00Z"),
            LastActionSuccessUtc: DateTimeOffset.Parse("2026-03-10T10:01:00Z"));

        store.Save(expected);
        ControlPlaneReporterState loaded = store.Load();

        Assert.Equal(123, loaded.LastSentEventId);
        Assert.Equal(expected.LastSummarySuccessUtc, loaded.LastSummarySuccessUtc);
        Assert.Equal(expected.LastActionSuccessUtc, loaded.LastActionSuccessUtc);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
        }
    }
}
