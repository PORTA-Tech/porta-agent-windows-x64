using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Workstream 3 — hide means hide for TODAY. The invariant under test: no sequence of
// widget-side actions leaves the widget hidden past the next 5:00 local boundary, and
// only the Control Center's permanent flag survives it.
public sealed class UserDesktopWidgetVisibilityStoreTests : IDisposable
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(5);

    private readonly string _dir;
    private readonly UserDesktopWidgetVisibilityStore _store;
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly string _permanentPath;

    public UserDesktopWidgetVisibilityStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"widget-visibility-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "hidden.v2.json");
        _legacyPath = Path.Combine(_dir, "hidden.v1.flag");
        _permanentPath = Path.Combine(_dir, "disabled.v1.flag");
        _store = new UserDesktopWidgetVisibilityStore(_path, _legacyPath, _permanentPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private static DateTimeOffset At(int day, int hour, int minute = 0)
        => new(2026, 07, day, hour, minute, 0, Offset);

    [Fact]
    public void HideAtAfternoon_HiddenAllEvening_VisibleNextMorning()
    {
        _store.Hide(At(7, 14, 0));

        Assert.True(_store.IsHidden(At(7, 14, 1)));
        Assert.True(_store.IsHidden(At(7, 23, 59)));
        Assert.True(_store.IsHidden(At(8, 4, 59)));
        Assert.False(_store.IsHidden(At(8, 5, 1)));
    }

    [Fact]
    public void HideAtNight_VisibleSameMorningAtFive()
    {
        // 02:00 is "tonight", not "today" — the next 5:00 boundary is 3 hours away.
        _store.Hide(At(7, 2, 0));

        Assert.True(_store.IsHidden(At(7, 4, 59)));
        Assert.False(_store.IsHidden(At(7, 5, 1)));
    }

    [Fact]
    public void CrossingBoundary_DeletesTheFileLazily()
    {
        _store.Hide(At(7, 14, 0));
        Assert.False(_store.IsHidden(At(8, 6, 0)));
        Assert.False(File.Exists(_path));

        // And the store stays visible on later reads.
        Assert.False(_store.IsHidden(At(8, 7, 0)));
    }

    [Fact]
    public void Show_UnhidesImmediately()
    {
        _store.Hide(At(7, 14, 0));
        _store.Show();
        Assert.False(_store.IsHidden(At(7, 14, 5)));
    }

    [Fact]
    public void LegacyV1Flag_MigratesToHiddenSinceNow()
    {
        // A fleet machine hidden under the old "forever" contract: the first read after
        // the update converts it and the widget returns the next morning.
        File.WriteAllText(_legacyPath, "hidden");

        Assert.True(_store.IsHidden(At(7, 14, 0)));
        Assert.False(File.Exists(_legacyPath));
        Assert.True(File.Exists(_path));

        Assert.True(_store.IsHidden(At(7, 23, 0)));
        Assert.False(_store.IsHidden(At(8, 5, 1)));
    }

    [Fact]
    public void CorruptStateFile_FailsTowardVisible()
    {
        File.WriteAllText(_path, "{not json");
        Assert.False(_store.IsHidden(At(7, 14, 0)));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void PermanentFlag_WinsOverEverything()
    {
        File.WriteAllText(_permanentPath, "disabled");

        Assert.True(_store.IsHidden(At(7, 14, 0)));
        Assert.True(_store.IsHidden(At(20, 5, 1))); // no boundary ever clears it

        _store.Show(); // widget-side show cannot clear the permanent flag
        Assert.True(_store.IsHidden(At(7, 14, 0)));

        File.Delete(_permanentPath);
        Assert.False(_store.IsHidden(At(7, 14, 0)));
    }

    [Fact]
    public void HideIsIdempotentWithinTheDay()
    {
        _store.Hide(At(7, 10, 0));
        _store.Hide(At(7, 22, 0)); // re-hide later the same day still ends 05:00 next day
        Assert.True(_store.IsHidden(At(8, 4, 30)));
        Assert.False(_store.IsHidden(At(8, 5, 30)));
    }
}
