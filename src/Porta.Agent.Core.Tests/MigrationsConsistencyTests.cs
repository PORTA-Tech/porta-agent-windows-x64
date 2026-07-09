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
