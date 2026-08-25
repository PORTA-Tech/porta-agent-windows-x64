using System;
using System.IO;
using Porta.Agent.Service.Fleet;
using Xunit;

namespace Porta.Agent.Service.Tests;

// The weekly's "already sent" guard has to survive a restart, for the same reason the daily's does:
// a guard held only in a field is cleared by every auto-update, crash and reboot, and the next poll
// posts a second email.
public sealed class WeeklyDigestStateStoreTests
{
    [Fact]
    public void RemembersTheWeekAcrossARestart()
    {
        RunWithStore((store, path) =>
        {
            store.SaveLastSentWeekEnd(new DateOnly(2026, 8, 23));

            var afterRestart = new WeeklyDigestStateStore(path);
            Assert.Equal(new DateOnly(2026, 8, 23), afterRestart.LoadLastSentWeekEnd());
        });
    }

    [Fact]
    public void ReportsNothingSentWhenTheFileHasNeverBeenWritten()
    {
        RunWithStore((store, _) => Assert.Null(store.LoadLastSentWeekEnd()));
    }

    [Fact]
    public void FailsTowardNotSentWhenTheFileIsCorrupt()
    {
        // At worst the server dedup suppresses a duplicate. The opposite failure — reading a
        // corrupt file as "already sent" — silently skips a real digest, which is far worse.
        RunWithStore((store, path) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Null(new WeeklyDigestStateStore(path).LoadLastSentWeekEnd());
        });
    }

    [Fact]
    public void IgnoresAFileFromAFutureSchema()
    {
        RunWithStore((store, path) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"SchemaVersion\":99,\"LastSentWeekEnd\":\"2026-08-23\"}");
            Assert.Null(new WeeklyDigestStateStore(path).LoadLastSentWeekEnd());
        });
    }

    [Fact]
    public void DoesNotShareAFileWithTheDaily()
    {
        // Different cadences, different keys — the daily records a UTC day, this records the end of
        // a goal-day week. One file would let either clear the other's guard.
        string dir = Path.Combine(Path.GetTempPath(), "porta-weekly-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string weeklyPath = Path.Combine(dir, "weekly-digest-state.v1.json");
            string dailyPath = Path.Combine(dir, "daily-digest-state.v1.json");

            new WeeklyDigestStateStore(weeklyPath).SaveLastSentWeekEnd(new DateOnly(2026, 8, 23));
            new DailyDigestStateStore(dailyPath).SaveLastSentDate(new DateOnly(2026, 8, 23));

            Assert.NotEqual(weeklyPath, dailyPath);
            Assert.Equal(new DateOnly(2026, 8, 23), new WeeklyDigestStateStore(weeklyPath).LoadLastSentWeekEnd());
            Assert.Equal(new DateOnly(2026, 8, 23), new DailyDigestStateStore(dailyPath).LoadLastSentDate());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void RunWithStore(Action<WeeklyDigestStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-weekly-state-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "weekly-digest-state.v1.json");
        try
        {
            body(new WeeklyDigestStateStore(path), path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
