using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserBreakOverlayStateStoreTests
{
    [Fact]
    public void SaveCountdown_ThenLoadActiveCountdown_RoundTrips()
    {
        using TempDirectoryScope tempDirectory = new();
        string path = Path.Combine(tempDirectory.DirectoryPath, "break-overlay.json");
        var store = new UserBreakOverlayStateStore(path);
        DateTimeOffset acceptedAtUtc = DateTimeOffset.Parse("2026-04-09T08:00:00Z");
        DateTimeOffset endsAtUtc = acceptedAtUtc.AddMinutes(5);

        store.SaveCountdown(new UserBreakOverlayCountdownState(
            InterventionId: "intv-1",
            TipKey: "break_suggestion",
            BreakKind: "long_break",
            Title: "Long break in progress",
            Message: "Ari is holding the screen for a real recovery break.",
            AcceptedAtUtc: acceptedAtUtc,
            EndsAtUtc: endsAtUtc));

        UserBreakOverlayCountdownState? loaded = store.LoadActiveCountdown(acceptedAtUtc.AddMinutes(1));

        Assert.NotNull(loaded);
        Assert.Equal("intv-1", loaded!.InterventionId);
        Assert.Equal("long_break", loaded.BreakKind);
        Assert.Equal("Long break in progress", loaded.Title);
        Assert.Equal(endsAtUtc, loaded.EndsAtUtc);
    }

    [Fact]
    public void LoadActiveCountdown_ExpiredState_ClearsPersistedFile()
    {
        using TempDirectoryScope tempDirectory = new();
        string path = Path.Combine(tempDirectory.DirectoryPath, "break-overlay.json");
        var store = new UserBreakOverlayStateStore(path);
        DateTimeOffset acceptedAtUtc = DateTimeOffset.Parse("2026-04-09T08:00:00Z");

        store.SaveCountdown(new UserBreakOverlayCountdownState(
            InterventionId: "intv-2",
            TipKey: "stretch_break_suggestion",
            BreakKind: "short_break",
            Title: "Short break in progress",
            Message: "Ari is holding the screen for a short break.",
            AcceptedAtUtc: acceptedAtUtc,
            EndsAtUtc: acceptedAtUtc.AddSeconds(90)));

        UserBreakOverlayCountdownState? loaded = store.LoadActiveCountdown(acceptedAtUtc.AddMinutes(5));

        Assert.Null(loaded);
        Assert.False(File.Exists(path));
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public TempDirectoryScope()
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "porta-break-overlay-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
