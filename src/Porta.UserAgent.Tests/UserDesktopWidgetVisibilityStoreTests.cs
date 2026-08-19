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

    // ── Phase 4.2 — the timed hides ────────────────────────────────────────────────────────
    // Same invariant as above, tighter: a duration hide ends when it says it does, and can
    // never buy MORE silence than "until tomorrow" would have.

    [Fact]
    public void HideForOneHour_ReturnsOnTheHour()
    {
        DateTimeOffset until = _store.HideFor(TimeSpan.FromHours(1), At(7, 14, 15));

        Assert.Equal(At(7, 15, 15), until);
        Assert.True(_store.IsHidden(At(7, 14, 16)));
        Assert.True(_store.IsHidden(At(7, 15, 14)));
        Assert.False(_store.IsHidden(At(7, 15, 16)));
    }

    [Fact]
    public void HideForTwoHours_ReturnsAfterTwo_NotAtTheDayBoundary()
    {
        DateTimeOffset until = _store.HideFor(TimeSpan.FromHours(2), At(7, 14, 0));

        Assert.Equal(At(7, 16, 0), until);
        Assert.True(_store.IsHidden(At(7, 15, 59)));
        Assert.False(_store.IsHidden(At(7, 16, 1)));
    }

    [Fact]
    public void TimedHideExpiring_DeletesTheFileLazily()
    {
        _store.HideFor(TimeSpan.FromHours(1), At(7, 14, 0));

        Assert.False(_store.IsHidden(At(7, 15, 30)));
        Assert.False(File.Exists(_path));
        Assert.False(_store.IsHidden(At(7, 16, 0)));
    }

    [Fact]
    public void TimedHideNeverOutlastsTheDayBoundary()
    {
        // 04:30 + 2h would be 06:30, past the 05:00 comeback. The boundary wins: a short hide
        // cannot be used to stay hidden into a morning that "until tomorrow" would have ended.
        DateTimeOffset until = _store.HideFor(TimeSpan.FromHours(2), At(7, 4, 30));

        Assert.Equal(At(7, 5, 0), until);
        Assert.True(_store.IsHidden(At(7, 4, 59)));
        Assert.False(_store.IsHidden(At(7, 5, 1)));
    }

    [Fact]
    public void HandEditedUntil_CannotBuyMoreThanTheDayBoundary()
    {
        // The end is stored, so it is worth assuming someone edits it. A far-future UntilUtc is
        // clamped back to the boundary the "today" contract would have given. Written by hand
        // rather than by editing the store's own output: the serializer escapes the '+' in an
        // offset as +, so a string replace against a "+00:00" needle matches nothing and the
        // test passes while asserting on an unedited file.
        File.WriteAllText(
            _path,
            $"{{\"HiddenAtUtc\":\"{At(7, 14, 0).ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\","
            + "\"Mode\":\"duration\","
            + $"\"UntilUtc\":\"{At(20, 12, 0).ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}");

        Assert.True(_store.IsHidden(At(7, 23, 0)));
        Assert.False(_store.IsHidden(At(8, 5, 1)));
    }

    [Fact]
    public void PermanentFlag_StillWinsOverATimedHide()
    {
        File.WriteAllText(_permanentPath, "disabled");
        _store.HideFor(TimeSpan.FromHours(1), At(7, 14, 0));

        Assert.True(_store.IsHidden(At(7, 16, 0))); // the hour passing does not clear it
        Assert.Equal(DateTimeOffset.MaxValue, _store.HiddenUntil(At(7, 16, 0)));
    }

    [Fact]
    public void HiddenUntil_IsNullWhenVisible()
    {
        Assert.Null(_store.HiddenUntil(At(7, 14, 0)));

        _store.HideFor(TimeSpan.FromHours(1), At(7, 14, 0));
        Assert.Equal(At(7, 15, 0), _store.HiddenUntil(At(7, 14, 30)));
        Assert.Null(_store.HiddenUntil(At(7, 15, 30)));
    }

    [Fact]
    public void ReadMode_DistinguishesTimedFromDaily()
    {
        Assert.Equal(string.Empty, _store.ReadMode(At(7, 14, 0)));

        _store.Hide(At(7, 14, 0));
        Assert.Equal("today", _store.ReadMode(At(7, 14, 30)));

        _store.HideFor(TimeSpan.FromHours(1), At(7, 14, 0));
        Assert.Equal("duration", _store.ReadMode(At(7, 14, 30)));

        // Expired: nothing is in force, so there is no mode to report.
        Assert.Equal(string.Empty, _store.ReadMode(At(7, 16, 0)));
    }

    [Fact]
    public void AFileFromAnEarlierBuild_StillReadsAsToday()
    {
        // Written before phase 4.2 existed: no UntilUtc at all. It must keep its old meaning
        // rather than being treated as a duration hide that ended immediately.
        File.WriteAllText(
            _path,
            $"{{\"HiddenAtUtc\":\"{At(7, 14, 0).ToUniversalTime():O}\",\"Mode\":\"today\"}}");

        Assert.True(_store.IsHidden(At(7, 23, 0)));
        Assert.Equal("today", _store.ReadMode(At(7, 23, 0)));
        Assert.False(_store.IsHidden(At(8, 5, 1)));
    }
}
