using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Porta.Agent.Core.Tests.Harness;

internal static class ScenarioAssertions
{
    public static void AssertEventsDescendingByTimestamp(
        IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> rows)
    {
        DateTimeOffset? previous = null;
        foreach ((string timestampUtc, _, _) in rows)
        {
            DateTimeOffset current = DateTimeOffset.Parse(timestampUtc, CultureInfo.InvariantCulture);
            if (previous.HasValue)
            {
                Assert.True(
                    previous.Value >= current,
                    $"Event ordering violated: previous={previous:O}, current={current:O}");
            }

            previous = current;
        }
    }

    public static void AssertTimestampsNonDecreasing(IEnumerable<DateTimeOffset> timestampsUtc)
    {
        DateTimeOffset? previous = null;
        foreach (DateTimeOffset current in timestampsUtc)
        {
            if (previous.HasValue)
            {
                Assert.True(
                    previous.Value <= current,
                    $"Timestamp monotonicity violated: previous={previous:O}, current={current:O}");
            }

            previous = current;
        }
    }

    public static void AssertRuntimeStateRowCount(string dbPath, string key, int expectedCount = 1)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM runtime_state WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        object? scalar = cmd.ExecuteScalar();
        int count = scalar is null ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        Assert.Equal(expectedCount, count);
    }

    public static void AssertLatestStateBucketJsonContracts(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT state_flags_json, contributors_json
            FROM state_buckets
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "Expected at least one finalized state_buckets row.");
        string stateFlagsJson = reader.GetString(0);
        string contributorsJson = reader.GetString(1);

        using JsonDocument stateDoc = JsonDocument.Parse(stateFlagsJson);
        using JsonDocument contributorsDoc = JsonDocument.Parse(contributorsJson);
        Assert.Equal(1, stateDoc.RootElement.GetProperty("schema_ver").GetInt32());
        Assert.Equal(1, contributorsDoc.RootElement.GetProperty("schema_ver").GetInt32());
    }
}
