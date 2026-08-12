using System;
using System.IO;
using Porta.Agent.Core.Tests.Harness;
using Xunit;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Maintenance item (c), 2026-08-12 — the harness must not leave databases behind.
///
/// This is the regression test for a leak that reached 5.1 GB across 2,608 files in %TEMP% and took
/// the machine down to 36 MB free. Deleting them was a mop; these assertions are the fix staying
/// fixed. Both temp directories are covered — `porta-step19-scenarios` (the scenario runner) and
/// `porta-step19-tests` (DeterministicScenarioDatabase).
/// </summary>
public class ScenarioHarnessCleanupTests
{
    [Fact]
    public void ScenarioRunner_LeavesNoDatabaseFilesBehind()
    {
        string dbPath;

        using (var runner = new DeterministicFinalizerScenarioRunner(
            new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero), seed: 1))
        {
            dbPath = runner.DbPath;

            // Write something, so the -wal and -shm sidecars actually exist. A scenario that never
            // wrote would have passed this test even before the fix.
            runner.RunBucket(new ScriptedBucketInput());

            Assert.True(File.Exists(dbPath), "the scenario database should exist while the runner is alive");
        }

        Assert.False(File.Exists(dbPath), $"leaked database: {dbPath}");
        Assert.False(File.Exists(dbPath + "-wal"), $"leaked write-ahead log: {dbPath}-wal");
        Assert.False(File.Exists(dbPath + "-shm"), $"leaked shared-memory file: {dbPath}-shm");
    }

    [Fact]
    public void ScenarioRunner_SurvivesARestartWithoutLeaking()
    {
        // Restart() builds a SECOND store against the same path, which is what leaves a second
        // pooled handle on the file. If pooling were not cleared, this is the case that leaks.
        string dbPath;

        using (var runner = new DeterministicFinalizerScenarioRunner(
            new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero), seed: 2))
        {
            dbPath = runner.DbPath;
            runner.RunBucket(new ScriptedBucketInput());
            runner.Restart();
            runner.RunBucket(new ScriptedBucketInput());
        }

        Assert.False(File.Exists(dbPath), $"leaked database after restart: {dbPath}");
        Assert.False(File.Exists(dbPath + "-wal"), $"leaked write-ahead log after restart: {dbPath}-wal");
        Assert.False(File.Exists(dbPath + "-shm"), $"leaked shared-memory file after restart: {dbPath}-shm");
    }

    [Fact]
    public void ScenarioDatabase_TempFile_LeavesNoFilesBehind()
    {
        string path;

        using (DeterministicScenarioDatabase db = DeterministicScenarioDatabase.CreateTempFile())
        {
            path = db.FilePath!;
            db.Connection.Open();
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE probe(id INTEGER PRIMARY KEY); INSERT INTO probe(id) VALUES (1);";
            cmd.ExecuteNonQuery();
        }

        Assert.False(File.Exists(path), $"leaked database: {path}");
        Assert.False(File.Exists(path + "-wal"), $"leaked write-ahead log: {path}-wal");
        Assert.False(File.Exists(path + "-shm"), $"leaked shared-memory file: {path}-shm");
    }
}
