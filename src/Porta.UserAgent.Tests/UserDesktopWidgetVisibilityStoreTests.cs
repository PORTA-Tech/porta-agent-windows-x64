using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetVisibilityStoreTests
{
    [Fact]
    public void HideAndShow_RoundTripsHiddenFlag()
    {
        string path = Path.Combine(Path.GetTempPath(), $"widget-hidden-{Guid.NewGuid():N}.flag");
        UserDesktopWidgetVisibilityStore store = new(path);

        try
        {
            Assert.False(store.IsHidden());

            store.Hide();

            Assert.True(store.IsHidden());

            store.Show();

            Assert.False(store.IsHidden());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
