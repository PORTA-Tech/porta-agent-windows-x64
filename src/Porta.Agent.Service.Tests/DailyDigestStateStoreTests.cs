using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

// B1 — the digest "already sent today" guard must survive a service restart. It used to live only
// in a DailyDigestReporter field, so any restart after 19:30 UTC re-sent the digest — the reported
// duplicate email.
public sealed class DailyDigestStateStoreTests
{
    [Fact]
    public void LoadLastSentDate_NoFileYet_IsNull()
    {
        RunWithStore((store, _) => Assert.Null(store.LoadLastSentDate()));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheDate_SurvivingARestart()
    {
        RunWithStore((store, path) =>
        {
            var sentOn = new DateOnly(2026, 7, 16);
            store.SaveLastSentDate(sentOn);

            // A brand-new store over the same path == the process restarted.
            var afterRestart = new DailyDigestStateStore(path);
            Assert.Equal(sentOn, afterRestart.LoadLastSentDate());
        });
    }

    [Fact]
    public void SaveLastSentDate_OverwritesThePreviousDay()
    {
        RunWithStore((store, path) =>
        {
            store.SaveLastSentDate(new DateOnly(2026, 7, 15));
            store.SaveLastSentDate(new DateOnly(2026, 7, 16));

            Assert.Equal(new DateOnly(2026, 7, 16), new DailyDigestStateStore(path).LoadLastSentDate());
        });
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"SchemaVersion":1,"LastSentDate":"garbage"}""")]
    [InlineData("""{"SchemaVersion":99,"LastSentDate":"2026-07-16"}""")]  // unknown schema
    public void LoadLastSentDate_CorruptOrUnknownSchema_FailsTowardNotSent(string contents)
    {
        // Failing toward "not sent" is deliberate: at worst the server-side dedup suppresses a
        // duplicate email. Failing the other way would silently swallow a real digest.
        RunWithStore((store, path) =>
        {
            File.WriteAllText(path, contents);
            Assert.Null(store.LoadLastSentDate());
        });
    }

    private static void RunWithStore(Action<DailyDigestStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-digest-state-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "daily-digest-state.v1.json");
        Directory.CreateDirectory(dir);
        try
        {
            body(new DailyDigestStateStore(path), path);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
