using System.Linq;
using Porta.Agent.Core.Db;
using Xunit;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Guards the database migration numbering — the exact class of bug that took down the whole fleet
/// path on 2026-07-09. Back then <c>LatestSchemaVersion</c> was a hardcoded 21 while
/// <c>reminders_v22</c> was already in the migration list, so once a DB advanced to v22 the service
/// refused to open it, the local-API listener died silently, and every agent showed AWAY.
/// <c>LatestSchemaVersion</c> is now derived from the list; these tests assert the list stays
/// well-formed and the derived version always tracks the newest migration.
/// </summary>
public sealed class MigrationsConsistencyTests
{
    [Fact]
    public void MigrationVersions_AreUnique_Contiguous_From1()
    {
        int[] versions = Migrations.OrderedStepVersions;

        Assert.NotEmpty(versions);
        Assert.Equal(versions.Length, versions.Distinct().Count()); // no duplicate version numbers
        Assert.Equal(1, versions.Min());                            // numbering starts at 1
        Assert.Equal(versions.Length, versions.Max());              // max == count => contiguous 1..N
    }

    /// <summary>
    /// Task attribution (2026-09-01) — V35 exists, is the newest, and did not arrive by editing a
    /// step that had already shipped.
    ///
    /// The V31 comment records what that costs: a column added inside the already-shipped step 30
    /// was created on every fresh database and on none of the installed ones, so every test passed
    /// while every project read on a real machine threw. A new release adds a NEW step, always.
    /// </summary>
    [Fact]
    public void TaskAffinity_ArrivedAsItsOwnStep_NotAsAnEditToAShippedOne()
    {
        Assert.Contains(35, Migrations.OrderedStepVersions);
        Assert.Equal(35, Migrations.LatestSchemaVersion);
    }

    [Fact]
    public void LatestSchemaVersion_AlwaysEqualsNewestMigration()
    {
        int[] versions = Migrations.OrderedStepVersions;

        // The whole point of deriving it: the supported version can never fall behind the newest
        // migration again. If anyone re-hardcodes it below the max, this fails loudly and stops
        // the exact 2026-07-09 outage from shipping twice.
        Assert.Equal(versions.Max(), Migrations.LatestSchemaVersion);
    }
}
