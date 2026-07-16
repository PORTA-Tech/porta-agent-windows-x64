using System.Collections.Generic;
using System.IO;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Workstream B2 — the per-class daily mute store. Same 5:00-local day boundary as the widget-
// hide store: a skip lasts through the night and auto-clears at the next 5am; classes are
// independent; a mute never becomes permanent.
public sealed class UserNotificationMuteStoreTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);

    private UserNotificationMuteStore NewStore()
    {
        string p = Path.Combine(Path.GetTempPath(), $"mute-{Guid.NewGuid():N}.json");
        _paths.Add(p);
        return new UserNotificationMuteStore(p);
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, Off);

    [Fact]
    public void AfternoonMute_LastsUntilNextFiveAm()
    {
        var store = NewStore();
        store.MuteForToday("break", At(2026, 7, 15, 14, 0));

        Assert.True(store.IsMutedForToday("break", At(2026, 7, 15, 20, 0)));  // same evening
        Assert.True(store.IsMutedForToday("break", At(2026, 7, 16, 4, 59)));  // pre-dawn next day
        Assert.False(store.IsMutedForToday("break", At(2026, 7, 16, 5, 0)));  // 5am boundary → cleared
    }

    [Fact]
    public void LateNightMute_LastsThroughTheNight()
    {
        var store = NewStore();
        store.MuteForToday("break", At(2026, 7, 15, 23, 30));

        Assert.True(store.IsMutedForToday("break", At(2026, 7, 16, 4, 0)));
        Assert.False(store.IsMutedForToday("break", At(2026, 7, 16, 5, 0)));
    }

    [Fact]
    public void EarlyMorningMute_EndsAtFiveTheSameDay()
    {
        var store = NewStore();
        store.MuteForToday("break", At(2026, 7, 15, 2, 0));

        Assert.True(store.IsMutedForToday("break", At(2026, 7, 15, 4, 0)));
        Assert.False(store.IsMutedForToday("break", At(2026, 7, 15, 5, 0)));
    }

    [Fact]
    public void ClassesAreIndependent()
    {
        var store = NewStore();
        store.MuteForToday("break", At(2026, 7, 15, 14, 0));

        Assert.True(store.IsMutedForToday("break", At(2026, 7, 15, 15, 0)));
        Assert.False(store.IsMutedForToday("ari:recovery", At(2026, 7, 15, 15, 0)));
    }

    [Fact]
    public void UnknownClassOrMissingFile_NotMuted()
    {
        var store = NewStore();
        Assert.False(store.IsMutedForToday("break", At(2026, 7, 15, 15, 0)));
        Assert.False(store.IsMutedForToday("", At(2026, 7, 15, 15, 0)));
    }

    [Fact]
    public void BoundaryCrossing_ClearsThePersistedEntry()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mute-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var store = new UserNotificationMuteStore(path);
        store.MuteForToday("break", At(2026, 7, 15, 14, 0));
        Assert.True(File.Exists(path));

        // A read past the boundary lazily removes the entry — the store IS the daily reset.
        Assert.False(store.IsMutedForToday("break", At(2026, 7, 16, 6, 0)));
        Assert.False(File.Exists(path)); // last entry removed → file deleted
    }

    public void Dispose()
    {
        foreach (string p in _paths)
        {
            try
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            catch
            {
            }
        }
    }
}
