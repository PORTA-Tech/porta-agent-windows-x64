using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// 2026-08-12 — <c>primitive_buckets</c> was in neither the retention prune nor the local-data
/// reset. Both omissions had the same cause: those two DELETE lists were written 38 minutes before
/// the table existed (b3beced 13:23:52, a7d5370 14:01:26, both on 2026-03-02) and nobody came back.
///
/// The reset half was a PRIVACY defect. The product told the user their local data was deleted and
/// left 542 MB of behavioural primitives behind.
///
/// These tests are written against the TABLE rather than through the pipeline, because the defect
/// was never about how the rows get written — it was about two lists that quietly did not mention
/// them. A test that exercises the pipeline would not have caught it either.
/// </summary>
public sealed class PrimitiveBucketsRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 13, 12, 0, 0, TimeSpan.Zero);

    // 14 days, matching DefaultRetentionDays and every sibling capture table.
    [Fact]
    public void RowsOlderThanRetention_ArePruned_AndRecentOnesSurvive()
    {
        RunWithStore((store, dbPath) =>
        {
            InsertPrimitiveBucket(dbPath, seq: 1, startUtc: Now.AddDays(-30));   // well outside
            InsertPrimitiveBucket(dbPath, seq: 2, startUtc: Now.AddDays(-15));   // just outside
            InsertPrimitiveBucket(dbPath, seq: 3, startUtc: Now.AddDays(-13));   // just inside
            InsertPrimitiveBucket(dbPath, seq: 4, startUtc: Now);                // today

            TriggerPrune(store);

            IReadOnlyList<long> remaining = ReadSeqs(dbPath);
            Assert.Equal(new long[] { 3, 4 }, remaining);
        });
    }

    // The regression that actually happened: everything else pruned, this one did not.
    [Fact]
    public void ThePruneCoversPrimitiveBuckets_NotJustItsSiblings()
    {
        RunWithStore((store, dbPath) =>
        {
            InsertPrimitiveBucket(dbPath, seq: 1, startUtc: Now.AddDays(-40));
            InsertStateBucket(dbPath, timestampUtc: Now.AddDays(-40));

            TriggerPrune(store);

            Assert.Equal(0, CountRows(dbPath, "state_buckets"));
            Assert.Equal(0, CountRows(dbPath, "primitive_buckets"));
        });
    }

    // THE PRIVACY ONE. If this fails, the product is lying to the user about deleting their data.
    [Fact]
    public void DeleteLocalData_LeavesNoPrimitiveBucketsBehind()
    {
        RunWithStore((store, dbPath) =>
        {
            InsertPrimitiveBucket(dbPath, seq: 1, startUtc: Now);
            InsertPrimitiveBucket(dbPath, seq: 2, startUtc: Now.AddMinutes(-5));
            Assert.Equal(2, CountRows(dbPath, "primitive_buckets"));

            store.DeleteLocalData();

            Assert.Equal(0, CountRows(dbPath, "primitive_buckets"));
        });
    }

    /// <summary>
    /// The prune is rate-limited to once every 30 minutes and only runs as a piggyback on a write.
    /// WriteSettings is the cheapest write that calls it.
    /// </summary>
    private static void TriggerPrune(SqliteBrainStateStore store)
        => store.WriteSettings(new AgentSettingsRecord(
            TimestampUtc: Now,
            PrivacyLevel: "standard",
            RegulationIntensity: "balanced",
            DisabledCategories: Array.Empty<string>(),
            NotificationReductionEnabled: false,
            FocusModeEnabled: false,
            AnimationReductionEnabled: false,
            TemporaryAppRestrictionEnabled: false,
            BaselineRecalibrationStartedUtc: null,
            BaselineRecalibrationUntilUtc: null));

    private static void InsertPrimitiveBucket(string dbPath, long seq, DateTimeOffset startUtc)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO primitive_buckets
                (bucket_seq, start_utc, end_utc, primitives_json, contributors_json, quality, partial)
            VALUES ($seq, $start, $end, '{"schema_ver":1}', '{"schema_ver":1}', 1.0, 0);
            """;
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", startUtc.AddSeconds(5).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void InsertStateBucket(string dbPath, DateTimeOffset timestampUtc)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO state_buckets
                (timestamp_utc, focus_strength, overload_likelihood, fatigue_probability,
                 cognitive_stability, confidence, contributors_json)
            VALUES ($ts, 0.5, 0.1, 0.1, 0.5, 0.9, '{"schema_ver":1}');
            """;
        cmd.Parameters.AddWithValue("$ts", timestampUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<long> ReadSeqs(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT bucket_seq FROM primitive_buckets ORDER BY bucket_seq;";
        var seqs = new List<long>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            seqs.Add(reader.GetInt64(0));
        }

        return seqs;
    }

    private static int CountRows(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-primitive-retention", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            body(store, dbPath);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
