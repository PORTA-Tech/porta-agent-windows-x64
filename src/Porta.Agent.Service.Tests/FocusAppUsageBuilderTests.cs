using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

// Workstream 5 — residual honesty rule. Diagnosis on real fleet label maps showed the 2e
// site pipeline works (github/gmail/claude/... tokens), but every UNRECOGNIZED page pools
// into one identity labeled "web", which then ranks high in "where your time went".
// "web" is meaningless to users: the residue must render as "Other browsing", and never
// as a guessed site or category.
public sealed class FocusAppUsageBuilderTests
{
    [Fact]
    public void BuildTopApps_RendersResidualWebAsOtherBrowsing()
    {
        var dwell = new Dictionary<string, double>
        {
            ["hash-github"] = 3600d,
            ["hash-web"] = 1800d,
        };
        var labels = new Dictionary<string, string>
        {
            ["hash-github"] = "github",
            ["hash-web"] = "web",
        };

        IReadOnlyList<FocusAppUsagePayload> apps = FocusAppUsageBuilder.BuildTopApps(dwell, labels);

        Assert.Contains(apps, a => a.Label == "github");
        Assert.Contains(apps, a => a.Label == FocusAppUsageBuilder.ResidualBrowsingLabel);
        Assert.DoesNotContain(apps, a => string.Equals(a.Label, "web", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildTopApps_RanksByDwellAndDropsSubMinuteApps()
    {
        var dwell = new Dictionary<string, double>
        {
            ["a"] = 120d,
            ["b"] = 3600d,
            ["c"] = 30d, // below MinSecondsToReport → dropped
        };
        var labels = new Dictionary<string, string>
        {
            ["a"] = "spotify",
            ["b"] = "code",
            ["c"] = "calculator",
        };

        IReadOnlyList<FocusAppUsagePayload> apps = FocusAppUsageBuilder.BuildTopApps(dwell, labels);

        Assert.Equal(2, apps.Count);
        Assert.Equal("code", apps[0].Label);
        Assert.Equal("spotify", apps[1].Label);
    }

    // A1 — the lock screen is away time, never an app. The capture fix stops new LockApp dwell
    // being recorded at all; this render-side guard keeps rows ALREADY stored under a shell label
    // out of "where your time went" on machines whose history predates that fix.
    [Fact]
    public void BuildTopApps_ExcludesLockScreenAndShellSurfaces()
    {
        var dwell = new Dictionary<string, double>
        {
            ["hash-code"] = 3600d,
            ["hash-lock"] = 1500d,   // 25m locked — the reported "LockApp 25m" row
            ["hash-start"] = 300d,
        };
        var labels = new Dictionary<string, string>
        {
            ["hash-code"] = "code",
            ["hash-lock"] = "lockapp",
            ["hash-start"] = "startmenuexperiencehost",
        };

        IReadOnlyList<FocusAppUsagePayload> apps = FocusAppUsageBuilder.BuildTopApps(dwell, labels);

        FocusAppUsagePayload only = Assert.Single(apps);
        Assert.Equal("code", only.Label);
        Assert.DoesNotContain(apps, a => string.Equals(a.Label, "lockapp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildTopApps_MergesResidualDwellAcrossBrowsers()
    {
        // Chrome-web and Edge-web are different identities (hash includes the exe), but the
        // user-facing row is one honest "Other browsing" total.
        var dwell = new Dictionary<string, double>
        {
            ["chrome-web"] = 600d,
            ["edge-web"] = 900d,
        };
        var labels = new Dictionary<string, string>
        {
            ["chrome-web"] = "web",
            ["edge-web"] = "web",
        };

        IReadOnlyList<FocusAppUsagePayload> apps = FocusAppUsageBuilder.BuildTopApps(dwell, labels);

        FocusAppUsagePayload residual = Assert.Single(apps);
        Assert.Equal(FocusAppUsageBuilder.ResidualBrowsingLabel, residual.Label);
        Assert.Equal(1500d, residual.Seconds, precision: 6);
    }
}
