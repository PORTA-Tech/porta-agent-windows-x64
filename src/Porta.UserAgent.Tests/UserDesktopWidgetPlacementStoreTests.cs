using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetPlacementStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsPlacement()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-widget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string path = Path.Combine(tempDirectory, "widget-placement.json");
            UserDesktopWidgetPlacementStore store = new(path);

            UserDesktopWidgetPlacement placement = new(216.5d, 44d);
            store.Save(placement);

            UserDesktopWidgetPlacement? loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(placement.Left, loaded!.Left);
            Assert.Equal(placement.Top, loaded.Top);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Clear_RemovesPersistedPlacement()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-widget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string path = Path.Combine(tempDirectory, "widget-placement.json");
            UserDesktopWidgetPlacementStore store = new(path);

            store.Save(new UserDesktopWidgetPlacement(180d, 56d));
            store.Clear();

            Assert.Null(store.Load());
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
