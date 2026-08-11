using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

// Complaint 7 (2026-08-11) - "my day has to add up. 42 minutes going nowhere is worse than an
// honest unnamed row, and it breaks the same rule."
//
// Foreground time whose identity has no label used to be DROPPED from "where your time went".
// Measured on the founder's machine 2026-08-10: 42m 30s across 14 of 19 identities vanished.
public sealed class FocusAppUsageUnnamedTests
{
    [Fact]
    public void UnlabelledTimeIsReported_NotDropped()
    {
        var dwell = new Dictionary<string, double>
        {
            ["known"] = 3600d,
            ["nolabel1"] = 900d,
            ["nolabel2"] = 600d
        };
        var labels = new Dictionary<string, string> { ["known"] = "cursor" };

        IReadOnlyList<FocusAppUsagePayload> rows = FocusAppUsageBuilder.BuildTopApps(dwell, labels);

        FocusAppUsagePayload? unnamed = rows.SingleOrDefault(r => r.Label == FocusAppUsageBuilder.UnnamedTimeLabel);
        Assert.NotNull(unnamed);
        Assert.Equal(1500d, unnamed!.Seconds);
    }

    [Fact]
    public void TheDayAddsUp()
    {
        // The actual requirement: every reported second is accounted for somewhere.
        var dwell = new Dictionary<string, double>
        {
            ["known"] = 3600d,
            ["residue"] = 1800d,
            ["nolabel"] = 1200d
        };
        var labels = new Dictionary<string, string> { ["known"] = "cursor", ["residue"] = "web" };

        IReadOnlyList<FocusAppUsagePayload> rows = FocusAppUsageBuilder.BuildTopApps(dwell, labels);

        Assert.Equal(6600d, rows.Sum(r => r.Seconds));
    }

    [Fact]
    public void UnlabelledTimeIsNotCalledBrowsing()
    {
        // An unlabelled identity may be a desktop app, and nothing here knows which. Calling it
        // "Other browsing" would be exactly the invented name a client report must never carry.
        var dwell = new Dictionary<string, double> { ["nolabel"] = 1200d };

        IReadOnlyList<FocusAppUsagePayload> rows = FocusAppUsageBuilder.BuildTopApps(
            dwell, new Dictionary<string, string>());

        Assert.DoesNotContain(rows, r => r.Label == FocusAppUsageBuilder.ResidualBrowsingLabel);
        Assert.Contains(rows, r => r.Label == FocusAppUsageBuilder.UnnamedTimeLabel);
    }

    [Fact]
    public void ResidualBrowsingStillMeansAnUnrecognisedPage()
    {
        // The honesty rule Yadin explicitly did not want removed: "web" stays "Other browsing".
        var dwell = new Dictionary<string, double> { ["residue"] = 1800d };
        var labels = new Dictionary<string, string> { ["residue"] = "web" };

        IReadOnlyList<FocusAppUsagePayload> rows = FocusAppUsageBuilder.BuildTopApps(dwell, labels);

        Assert.Equal(FocusAppUsageBuilder.ResidualBrowsingLabel, rows.Single().Label);
    }
}
