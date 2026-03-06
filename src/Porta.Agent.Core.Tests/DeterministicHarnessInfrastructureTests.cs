using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Tests.Harness;

namespace Porta.Agent.Core.Tests;

public sealed class DeterministicHarnessInfrastructureTests
{
    [Fact]
    public void DeterministicRandom_SameSeed_ReplaysSameSequence()
    {
        const int seed = 42017;
        var a = new DeterministicRandom(seed);
        var b = new DeterministicRandom(seed);

        int[] seqA = Enumerable.Range(0, 20).Select(_ => a.Next(0, 1000)).ToArray();
        int[] seqB = Enumerable.Range(0, 20).Select(_ => b.Next(0, 1000)).ToArray();

        Assert.True(seqA.SequenceEqual(seqB), a.SeedTag("Random sequence mismatch"));
    }

    [Fact]
    public void ScenarioDatabase_InMemory_MigratesAndIsIsolatedPerScenario()
    {
        using var dbA = DeterministicScenarioDatabase.Create(ScenarioDatabaseMode.InMemory);
        using var dbB = DeterministicScenarioDatabase.Create(ScenarioDatabaseMode.InMemory);

        Assert.True(TableExists(dbA.Connection, "events"));
        Assert.True(TableExists(dbA.Connection, "runtime_state"));
        Assert.True(TableExists(dbB.Connection, "events"));
        Assert.True(TableExists(dbB.Connection, "runtime_state"));

        InsertRuntimeState(dbA.Connection, key: "step19:test", valueJson: "{\"ok\":true}");
        long countA = CountRuntimeState(dbA.Connection, "step19:test");
        long countB = CountRuntimeState(dbB.Connection, "step19:test");

        Assert.Equal(1, countA);
        Assert.Equal(0, countB);
    }

    [Fact]
    public void ScenarioDatabase_TempFile_UsesUniquePathPerScenario()
    {
        string? firstPath;
        string? secondPath;

        using (var dbA = DeterministicScenarioDatabase.Create(ScenarioDatabaseMode.TempFile))
        using (var dbB = DeterministicScenarioDatabase.Create(ScenarioDatabaseMode.TempFile))
        {
            firstPath = dbA.FilePath;
            secondPath = dbB.FilePath;
            Assert.False(string.IsNullOrWhiteSpace(firstPath));
            Assert.False(string.IsNullOrWhiteSpace(secondPath));
            Assert.NotEqual(firstPath, secondPath);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
        }

        Assert.False(string.IsNullOrWhiteSpace(firstPath));
        Assert.False(string.IsNullOrWhiteSpace(secondPath));
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        object? value = cmd.ExecuteScalar();
        return value is not null;
    }

    private static void InsertRuntimeState(SqliteConnection connection, string key, string valueJson)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, '2026-03-06T00:00:00.000Z')
            ON CONFLICT(key) DO UPDATE SET
                value_json=excluded.value_json,
                updated_utc=excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.ExecuteNonQuery();
    }

    private static long CountRuntimeState(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM runtime_state WHERE key=$key;";
        cmd.Parameters.AddWithValue("$key", key);
        object? value = cmd.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt64(value);
    }
}
