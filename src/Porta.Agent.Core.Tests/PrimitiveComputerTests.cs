using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class PrimitiveComputerTests
{
    [Fact]
    public void PrimitiveRegistry_IsPinned_AndCompleteForPersistedPrimitives()
    {
        Assert.Equal(1, PrimitiveRegistry.RegistryVersion);
        Assert.Equal(3, PrimitiveRegistry.Metrics.Count);

        PrimitiveMetricDefinition typing = PrimitiveRegistry.TypingBurstIntensity;
        Assert.Equal("typing_burst_intensity", typing.PrimitiveKey);
        Assert.Equal("keyboard", typing.ChannelDependency);
        Assert.Equal(PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha, typing.EwmaAlpha, precision: 6);
        Assert.True(typing.AppliesMissingChannelRule);
        Assert.Contains("keyboard_iki_mean", typing.SourceMetricKeys);

        PrimitiveMetricDefinition scroll = PrimitiveRegistry.ScrollVelocityIndex;
        Assert.Equal("scroll_velocity_index", scroll.PrimitiveKey);
        Assert.Equal("mouse", scroll.ChannelDependency);
        Assert.Contains("mouse_speed_mean", scroll.SourceMetricKeys);

        PrimitiveMetricDefinition focus = PrimitiveRegistry.FocusSwitchRate;
        Assert.Equal("focus_switch_rate", focus.PrimitiveKey);
        Assert.Equal("focus", focus.ChannelDependency);
        Assert.Contains("focus_switch_rate", focus.SourceMetricKeys);
    }

    [Fact]
    public void PrimitiveNormalizationConstants_ArePinned()
    {
        Assert.Equal(1.2d, PortaExecutionConstants.PrimitiveNormalization.SigmoidScale, precision: 6);
        Assert.Equal(0.9d, PortaExecutionConstants.PrimitiveNormalization.MissingChannelDecay, precision: 6);
        Assert.Equal(0.35d, PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha, precision: 6);
    }

    [Fact]
    public void ZZero_WithSPrevPointFive_ReturnsExpectedSigmoidAndEwma()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertSPrevState(connection, tx, typing: 0.5d, scroll: 0.5d, focus: 0.5d);

        InputBucketRow row = BuildRow();
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 0.0d, ready: true),
            ("mouse_speed_mean", z: 0.0d, ready: true),
            ("focus_switch_rate", z: 0.0d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);
        Assert.Equal(0.5d, result.TypingRaw, precision: 6);
        Assert.Equal(0.5d, result.ScrollRaw, precision: 6);
        Assert.Equal(0.5d, result.FocusRaw, precision: 6);
        Assert.Equal(0.5d, result.TypingSmoothed, precision: 6);
        Assert.Equal(0.5d, result.ScrollSmoothed, precision: 6);
        Assert.Equal(0.5d, result.FocusSmoothed, precision: 6);
    }

    [Fact]
    public void MissingChannel_SPrevPointEight_DecaysToPointSevenTwo()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertSPrevState(connection, tx, typing: 0.80d, scroll: 0.50d, focus: 0.50d);

        InputBucketRow row = BuildRow();
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 0.0d, ready: false),
            ("mouse_speed_mean", z: 0.0d, ready: true),
            ("focus_switch_rate", z: 0.0d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);
        Assert.False(result.TypingChannelAvailable);
        Assert.Equal(0.72d, result.TypingSmoothed, precision: 6);
    }

    [Fact]
    public void Restart_AfterPersistedSPrev_MatchesContinuousRun()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"porta-primitives-{Guid.NewGuid():N}.db");
        try
        {
            using (var initConnection = new SqliteConnection($"Data Source={dbPath}"))
            {
                initConnection.Open();
                using var initCmd = initConnection.CreateCommand();
                initCmd.CommandText =
                    """
                    CREATE TABLE runtime_state (
                        key TEXT PRIMARY KEY,
                        value_json TEXT NOT NULL,
                        updated_utc TEXT NOT NULL
                    );
                    """;
                initCmd.ExecuteNonQuery();

                using SqliteTransaction initTx = initConnection.BeginTransaction();
                InsertSPrevState(initConnection, initTx, typing: 0.5d, scroll: 0.5d, focus: 0.5d);
                initTx.Commit();
            }

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using SqliteTransaction tx = connection.BeginTransaction();
                PrimitiveComputationResult bucket1 = PrimitiveComputer.ComputeForFinalizedBucket(
                    connection,
                    tx,
                    BuildRow(),
                    BuildRobustSnapshot(
                        ("keyboard_iki_mean", z: 1.0d, ready: true),
                        ("mouse_speed_mean", z: 0.5d, ready: true),
                        ("focus_switch_rate", z: -0.5d, ready: true)));
                PrimitiveComputer.PersistSPrevForFinalizedBucket(
                    connection,
                    tx,
                    updatedUtc: "2026-03-03T00:00:10.000Z",
                    primitive: bucket1);
                tx.Commit();
            }

            PrimitiveComputationResult continuous;
            using (var continuousConnection = new SqliteConnection($"Data Source={dbPath}"))
            {
                continuousConnection.Open();
                using SqliteTransaction tx = continuousConnection.BeginTransaction();
                continuous = PrimitiveComputer.ComputeForFinalizedBucket(
                    continuousConnection,
                    tx,
                    BuildRow(),
                    BuildRobustSnapshot(
                        ("keyboard_iki_mean", z: 0.2d, ready: true),
                        ("mouse_speed_mean", z: -0.3d, ready: true),
                        ("focus_switch_rate", z: 0.1d, ready: true)));
            }

            PrimitiveComputationResult restart;
            using (var restartConnection = new SqliteConnection($"Data Source={dbPath}"))
            {
                restartConnection.Open();
                using SqliteTransaction tx = restartConnection.BeginTransaction();
                restart = PrimitiveComputer.ComputeForFinalizedBucket(
                    restartConnection,
                    tx,
                    BuildRow(),
                    BuildRobustSnapshot(
                        ("keyboard_iki_mean", z: 0.2d, ready: true),
                        ("mouse_speed_mean", z: -0.3d, ready: true),
                        ("focus_switch_rate", z: 0.1d, ready: true)));
            }

            Assert.Equal(continuous.TypingSmoothed, restart.TypingSmoothed, precision: 6);
            Assert.Equal(continuous.ScrollSmoothed, restart.ScrollSmoothed, precision: 6);
            Assert.Equal(continuous.FocusSmoothed, restart.FocusSmoothed, precision: 6);
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }

                string walPath = $"{dbPath}-wal";
                string shmPath = $"{dbPath}-shm";
                if (File.Exists(walPath))
                {
                    File.Delete(walPath);
                }

                if (File.Exists(shmPath))
                {
                    File.Delete(shmPath);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ExtremeZInputTen_BehavesAsClampedThree()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            BuildRow(),
            BuildRobustSnapshot(
                ("keyboard_iki_mean", z: 10d, ready: true),
                ("mouse_speed_mean", z: 10d, ready: true),
                ("focus_switch_rate", z: 10d, ready: true)));

        double expected = Sigmoid(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * 3d);
        Assert.Equal(expected, result.TypingRaw, precision: 6);
        Assert.Equal(expected, result.ScrollRaw, precision: 6);
        Assert.Equal(expected, result.FocusRaw, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_NoPreviousState_UsesPinnedNeutralSPrev()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        InputBucketRow row = BuildRow();
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 1.0d, ready: true),
            ("mouse_speed_mean", z: 0.0d, ready: true),
            ("focus_switch_rate", z: -1.0d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);

        double expectedTyping = Sigmoid(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * 1.0d);
        double expectedScroll = Sigmoid(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * 0.0d);
        double expectedFocus = Sigmoid(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * -1.0d);

        Assert.True(result.TypingChannelAvailable);
        Assert.True(result.ScrollChannelAvailable);
        Assert.True(result.FocusChannelAvailable);
        Assert.Equal(expectedTyping, result.TypingRaw, precision: 6);
        Assert.Equal(expectedScroll, result.ScrollRaw, precision: 6);
        Assert.Equal(expectedFocus, result.FocusRaw, precision: 6);

        double neutral = PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral;
        double alpha = PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha;
        Assert.Equal((alpha * expectedTyping) + ((1d - alpha) * neutral), result.TypingSmoothed, precision: 6);
        Assert.Equal((alpha * expectedScroll) + ((1d - alpha) * neutral), result.ScrollSmoothed, precision: 6);
        Assert.Equal((alpha * expectedFocus) + ((1d - alpha) * neutral), result.FocusSmoothed, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_UsesClampedZBeforeSigmoid()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        InputBucketRow row = BuildRow();
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 100d, ready: true),
            ("mouse_speed_mean", z: -100d, ready: true),
            ("focus_switch_rate", z: 100d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);

        double expectedMax = Sigmoid(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * PortaExecutionConstants.RobustNormalization.ZClampMax);
        double expectedMin = Sigmoid(PortaExecutionConstants.PrimitiveNormalization.SigmoidScale * PortaExecutionConstants.RobustNormalization.ZClampMin);

        Assert.Equal(expectedMax, result.TypingRaw, precision: 6);
        Assert.Equal(expectedMin, result.ScrollRaw, precision: 6);
        Assert.Equal(expectedMax, result.FocusRaw, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_WithPreviousState_UsesEwmaWhenChannelAvailable()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertSPrevState(connection, tx, typing: 0.80d, scroll: 0.60d, focus: 0.40d);

        InputBucketRow row = BuildRow();
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 0.0d, ready: true),
            ("mouse_speed_mean", z: 0.0d, ready: true),
            ("focus_switch_rate", z: 0.0d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);

        double raw = 0.5d; // sigmoid(0)
        double expectedTyping = (PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha * raw) + ((1d - PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha) * 0.80d);
        double expectedScroll = (PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha * raw) + ((1d - PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha) * 0.60d);
        double expectedFocus = (PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha * raw) + ((1d - PortaExecutionConstants.PrimitiveNormalization.EwmaAlpha) * 0.40d);

        Assert.Equal(expectedTyping, result.TypingSmoothed, precision: 6);
        Assert.Equal(expectedScroll, result.ScrollSmoothed, precision: 6);
        Assert.Equal(expectedFocus, result.FocusSmoothed, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_MissingChannel_DecaysPreviousInsteadOfZeroing()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertSPrevState(connection, tx, typing: 0.80d, scroll: 0.60d, focus: 0.40d);

        InputBucketRow row = BuildRow();
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 0.0d, ready: false),
            ("mouse_speed_mean", z: 0.0d, ready: true),
            ("focus_switch_rate", z: 0.0d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);

        double expectedTyping = PortaExecutionConstants.PrimitiveNormalization.MissingChannelDecay * 0.80d;
        Assert.False(result.TypingChannelAvailable);
        Assert.Equal(expectedTyping, result.TypingSmoothed, precision: 6);
        Assert.NotEqual(0d, result.TypingSmoothed);
    }

    [Fact]
    public void ComputeForFinalizedBucket_PartialAndMissingMajorChannel_UsesDecay()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertSPrevState(connection, tx, typing: 0.80d, scroll: 0.60d, focus: 0.40d);

        InputBucketRow row = BuildRow() with
        {
            Partial = 1,
            ContributorsJson = "{\"schema_ver\":1,\"missing_major_channels\":[\"keyboard\"]}"
        };
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 0.0d, ready: true),
            ("mouse_speed_mean", z: 0.0d, ready: true),
            ("focus_switch_rate", z: 0.0d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);

        double expectedTyping = PortaExecutionConstants.PrimitiveNormalization.MissingChannelDecay * 0.80d;
        Assert.False(result.TypingChannelAvailable);
        Assert.Equal(expectedTyping, result.TypingSmoothed, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_CaptorUnavailable_UsesDecay()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertSPrevState(connection, tx, typing: 0.80d, scroll: 0.60d, focus: 0.40d);
        InsertCaptorAvailability(connection, tx, keyboard: false, mouse: true, focus: true, idle: true);

        InputBucketRow row = BuildRow();
        var robust = BuildRobustSnapshot(
            ("keyboard_iki_mean", z: 0.0d, ready: true),
            ("mouse_speed_mean", z: 0.0d, ready: true),
            ("focus_switch_rate", z: 0.0d, ready: true));

        PrimitiveComputationResult result = PrimitiveComputer.ComputeForFinalizedBucket(connection, tx, row, robust);

        double expectedTyping = PortaExecutionConstants.PrimitiveNormalization.MissingChannelDecay * 0.80d;
        Assert.False(result.TypingChannelAvailable);
        Assert.Equal(expectedTyping, result.TypingSmoothed, precision: 6);
    }

    [Fact]
    public void PersistSPrevForFinalizedBucket_WritesRuntimeStateContract()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        PrimitiveComputationResult result = new(
            TypingRaw: 0.3d,
            ScrollRaw: 0.4d,
            FocusRaw: 0.5d,
            TypingSmoothed: 0.6d,
            ScrollSmoothed: 0.7d,
            FocusSmoothed: 0.8d,
            TypingChannelAvailable: true,
            ScrollChannelAvailable: false,
            FocusChannelAvailable: true);

        PrimitiveComputer.PersistSPrevForFinalizedBucket(
            connection,
            tx,
            updatedUtc: "2026-03-03T00:00:00.000Z",
            primitive: result);
        tx.Commit();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_ver").GetInt32());
        JsonElement values = root.GetProperty("values");
        Assert.Equal(0.6d, values.GetProperty("typing_burst_intensity").GetDouble(), precision: 6);
        Assert.Equal(0.7d, values.GetProperty("scroll_velocity_index").GetDouble(), precision: 6);
        Assert.Equal(0.8d, values.GetProperty("focus_switch_rate").GetDouble(), precision: 6);
        Assert.Equal(PrimitiveRegistry.Metrics.Count, values.EnumerateObject().Count());
    }

    [Fact]
    public void EnsurePrimitivesPrevStateInitialized_MissingRow_WritesNeutralValues()
    {
        using var connection = OpenInMemoryDb();
        PrimitiveComputer.EnsurePrimitivesPrevStateInitialized(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement values = doc.RootElement.GetProperty("values");

        foreach (PrimitiveMetricDefinition metric in PrimitiveRegistry.Metrics)
        {
            Assert.Equal(
                PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral,
                values.GetProperty(metric.PrimitiveKey).GetDouble(),
                precision: 6);
        }
    }

    [Fact]
    public void EnsurePrimitivesPrevStateInitialized_BackfillsMissingKeysDeterministically()
    {
        using var connection = OpenInMemoryDb();
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            string partialJson = "{\"schema_ver\":1,\"values\":{\"typing_burst_intensity\":0.25}}";
            InsertRawRuntimeState(
                connection,
                tx,
                key: PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey,
                valueJson: partialJson);
            tx.Commit();
        }

        PrimitiveComputer.EnsurePrimitivesPrevStateInitialized(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement values = doc.RootElement.GetProperty("values");

        Assert.Equal(0.25d, values.GetProperty("typing_burst_intensity").GetDouble(), precision: 6);
        Assert.Equal(PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral, values.GetProperty("scroll_velocity_index").GetDouble(), precision: 6);
        Assert.Equal(PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral, values.GetProperty("focus_switch_rate").GetDouble(), precision: 6);
    }

    [Fact]
    public void EnsurePrimitivesPrevStateInitialized_CorruptRow_ReinitializesNeutral()
    {
        using var connection = OpenInMemoryDb();
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            InsertRawRuntimeState(
                connection,
                tx,
                key: PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey,
                valueJson: "{\"schema_ver\":1,\"values\":\"bad\"}");
            tx.Commit();
        }

        PrimitiveComputer.EnsurePrimitivesPrevStateInitialized(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement values = doc.RootElement.GetProperty("values");

        foreach (PrimitiveMetricDefinition metric in PrimitiveRegistry.Metrics)
        {
            Assert.Equal(
                PortaExecutionConstants.PrimitiveNormalization.InitialSPrevNeutral,
                values.GetProperty(metric.PrimitiveKey).GetDouble(),
                precision: 6);
        }
    }

    [Fact]
    public void EnsurePrimitivesPrevStateInitialized_LegacyRow_MigratesToCurrentMap()
    {
        using var connection = OpenInMemoryDb();
        using (SqliteTransaction tx = connection.BeginTransaction())
        {
            string legacyJson = JsonSerializer.Serialize(new
            {
                schema_ver = 1,
                s_prev = new
                {
                    typing_burst_intensity = 0.11d,
                    scroll_velocity_index = 0.22d,
                    focus_switch_rate = 0.33d
                }
            });
            InsertRawRuntimeState(
                connection,
                tx,
                key: PortaExecutionConstants.PrimitiveNormalization.LegacySPrevRuntimeStateKey,
                valueJson: legacyJson);
            tx.Commit();
        }

        PrimitiveComputer.EnsurePrimitivesPrevStateInitialized(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement values = doc.RootElement.GetProperty("values");
        Assert.Equal(0.11d, values.GetProperty("typing_burst_intensity").GetDouble(), precision: 6);
        Assert.Equal(0.22d, values.GetProperty("scroll_velocity_index").GetDouble(), precision: 6);
        Assert.Equal(0.33d, values.GetProperty("focus_switch_rate").GetDouble(), precision: 6);
    }

    [Fact]
    public void PersistSPrevForFinalizedBucket_UsesRegistryOrderForValuesMap()
    {
        using var connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();

        PrimitiveComputationResult result = new(
            TypingRaw: 0.1d,
            ScrollRaw: 0.2d,
            FocusRaw: 0.3d,
            TypingSmoothed: 0.4d,
            ScrollSmoothed: 0.5d,
            FocusSmoothed: 0.6d,
            TypingChannelAvailable: true,
            ScrollChannelAvailable: true,
            FocusChannelAvailable: true);

        PrimitiveComputer.PersistSPrevForFinalizedBucket(
            connection,
            tx,
            updatedUtc: "2026-03-03T00:00:00.000Z",
            primitive: result);
        tx.Commit();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey);
        string json = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "{}";
        using JsonDocument doc = JsonDocument.Parse(json);

        List<string> orderedKeys = doc.RootElement
            .GetProperty("values")
            .EnumerateObject()
            .Select(static p => p.Name)
            .ToList();

        List<string> expected = PrimitiveRegistry.Metrics.Select(static m => m.PrimitiveKey).ToList();
        Assert.Equal(expected, orderedKeys);
    }

    private static SqliteConnection OpenInMemoryDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE runtime_state (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static void InsertSPrevState(
        SqliteConnection connection,
        SqliteTransaction tx,
        double typing,
        double scroll,
        double focus)
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            values = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey] = typing,
                [PrimitiveRegistry.ScrollVelocityIndex.PrimitiveKey] = scroll,
                [PrimitiveRegistry.FocusSwitchRate.PrimitiveKey] = focus
            }
        });

        InsertRawRuntimeState(
            connection,
            tx,
            key: PortaExecutionConstants.PrimitiveNormalization.SPrevRuntimeStateKey,
            valueJson: valueJson);
    }

    private static void InsertCaptorAvailability(
        SqliteConnection connection,
        SqliteTransaction tx,
        bool keyboard,
        bool mouse,
        bool focus,
        bool idle)
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            channels = new
            {
                keyboard,
                mouse,
                focus,
                idle
            }
        });

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, $updated_utc);
            """;
        cmd.Parameters.AddWithValue("$key", "captor_availability");
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.Parameters.AddWithValue("$updated_utc", "2026-03-03T00:00:00.000Z");
        cmd.ExecuteNonQuery();
    }

    private static void InsertRawRuntimeState(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        string valueJson)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value_json, $updated_utc)
            ON CONFLICT(key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value_json", valueJson);
        cmd.Parameters.AddWithValue("$updated_utc", "2026-03-03T00:00:00.000Z");
        cmd.ExecuteNonQuery();
    }

    private static InputBucketRow BuildRow()
    {
        return new InputBucketRow(
            BucketSeq: 1,
            TimestampUtc: "2026-03-03T00:00:05.000Z",
            StartUtc: "2026-03-03T00:00:05.000Z",
            TypingBurstIntensity: 0.2d,
            ScrollVelocityIndex: 0.3d,
            FocusSwitchRate: 0.4d,
            KeyboardIkiMean: 0d,
            KeyboardIkiCv: 0d,
            KeyboardBurstRate: 0d,
            KeyboardBackspaceRate: 0d,
            MouseSpeedMean: 0d,
            MouseJerkP95: 0d,
            MouseScrollSpeed: 0d,
            MouseMicroCorrectionRate: 0d,
            FocusAppEntropy: 0d,
            FocusSwitchCount: 0,
            DominantAppHash: "app_hash_test",
            FocusActiveAppCount: 0d,
            IdleSeconds: 0d,
            IdleMicroIdleCount: 0d,
            IdleMicroIdleSeconds: 0d,
            TrueBreakSeconds: 0d,
            TrueBreakFlag: 0,
            QualityScore: 1d,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
    }

    private static RobustNormalizationSnapshot BuildRobustSnapshot(params (string key, double z, bool ready)[] metrics)
    {
        var map = new Dictionary<string, MetricNormalizationValue>(StringComparer.Ordinal);
        foreach ((string key, double z, bool ready) in metrics)
        {
            map[key] = new MetricNormalizationValue(
                MetricKey: key,
                RawValue: 0d,
                WinsorizedValue: 0d,
                Z: z,
                MetricReady: ready,
                WasWinsorized: false,
                NonFinite: 0);
        }

        return new RobustNormalizationSnapshot(
            Metrics: map,
            TypingComponent: 0d,
            ScrollComponent: 0d,
            FocusComponent: 0d);
    }

    private static double Sigmoid(double value)
    {
        return 1d / (1d + Math.Exp(-value));
    }
}
