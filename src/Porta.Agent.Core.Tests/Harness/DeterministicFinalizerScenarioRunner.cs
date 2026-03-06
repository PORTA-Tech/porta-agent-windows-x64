using System.Globalization;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests.Harness;

public readonly record struct ScriptedBucketInput(
    double TypingBurstIntensity = 0.70d,
    double ScrollVelocityIndex = 0.25d,
    double FocusSwitchRate = 0.20d,
    string AppHash = "app_a",
    int KeyboardKeydowns = 3,
    int MouseMoves = 2,
    int MouseWheels = 1,
    double IdleSeconds = 0d);

public readonly record struct FinalizerTickObservation(
    long BucketSeq,
    DateTimeOffset BucketStartUtc,
    DateTimeOffset BucketEndUtc,
    AgentLatestStateDetails? LatestState,
    AgentNotificationSuppressionStatus NotificationStatus,
    AgentBrightnessActionStatus BrightnessStatus,
    IReadOnlyList<AgentControlEvent> RecentEvents);

public readonly record struct LocalApiProjectionSnapshot(
    Porta.Agent.LocalApi.LocalApiStatusResponse Status,
    Porta.Agent.LocalApi.LocalApiCurrentSnapshotResponse Snapshot,
    IReadOnlyList<Porta.Agent.LocalApi.LocalApiEventResponse> Events);

public sealed class DeterministicFinalizerScenarioRunner : IDisposable
{
    private SqliteBrainStateStore _innerStore;
    private BucketFinalizerStateStore _stateStore;
    private AgentCoreController _controller;
    private readonly string _dbPath;
    private bool _disposed;
    private long _nextBucketSeq;

    public DeterministicFinalizerScenarioRunner(DateTimeOffset startUtc, int seed)
    {
        Seed = seed;
        DateTimeOffset alignedStart = AlignToBucketBoundary(startUtc);
        Clock = new DeterministicTestClock(alignedStart, Time.bucketSeconds);
        Random = new DeterministicRandom(seed);

        string tempDir = Path.Combine(Path.GetTempPath(), "porta-step19-scenarios");
        Directory.CreateDirectory(tempDir);
        _dbPath = Path.Combine(tempDir, $"scenario-{Guid.NewGuid():N}.db");
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        NotificationController = new FakeNotificationController(Clock);
        BrightnessController = new FakeBrightnessController(Clock)
        {
            Supported = true,
            CurrentLevel = 70
        };

        _innerStore = new SqliteBrainStateStore(
            new DeepWorkOutputActionCoordinator(NotificationController),
            new OverloadBrightnessActionCoordinator(BrightnessController),
            dbPathOverride: _dbPath);
        _stateStore = new BucketFinalizerStateStore(_innerStore, () => Clock.UtcNow);
        _controller = new AgentCoreController(_stateStore);
        _nextBucketSeq = Time.bucketSeqFromEventTs(Clock.UtcNow.ToUnixTimeMilliseconds());
    }

    public DeterministicTestClock Clock { get; }

    public DeterministicRandom Random { get; }

    public int Seed { get; }

    public FakeNotificationController NotificationController { get; }

    public FakeBrightnessController BrightnessController { get; }

    public string DbPath => _dbPath;

    public void Restart()
    {
        EnsureNotDisposed();

        // Recreate store/controller against the same persisted DB path to exercise
        // startup reconciliation logic deterministically.
        _innerStore = new SqliteBrainStateStore(
            new DeepWorkOutputActionCoordinator(NotificationController),
            new OverloadBrightnessActionCoordinator(BrightnessController),
            dbPathOverride: _dbPath);
        _stateStore = new BucketFinalizerStateStore(_innerStore, () => Clock.UtcNow);
        _controller = new AgentCoreController(_stateStore);
        _nextBucketSeq = Time.bucketSeqFromEventTs(Clock.UtcNow.ToUnixTimeMilliseconds());
    }

    public FinalizerTickObservation RunBucket(ScriptedBucketInput input)
    {
        EnsureNotDisposed();

        (long startMs, long endMs) = Time.bucketStartEnd(_nextBucketSeq);
        DateTimeOffset startUtc = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
        DateTimeOffset endUtc = DateTimeOffset.FromUnixTimeMilliseconds(endMs);
        Clock.AdvanceTo(startUtc);

        // Non-raw aggregate sample that contributes primitive values and stable app context.
        WriteSnapshot(
            timestampUtc: startUtc.AddMilliseconds(100),
            typing: input.TypingBurstIntensity,
            scroll: input.ScrollVelocityIndex,
            focus: input.FocusSwitchRate,
            contributors: [$"foreground_app:{NormalizeAppHash(input.AppHash)}"]);

        EmitRawKeyboardEvents(input, startUtc, endUtc);
        EmitRawMouseMoveEvents(input, startUtc, endUtc);
        EmitRawMouseWheelEvents(input, startUtc, endUtc);
        EmitRawFocusEvents(input, startUtc, endUtc);
        EmitRawIdleSamples(input, startUtc, endUtc);

        // Deterministic finalize tick at bucket_end + finalize_delay.
        DateTimeOffset finalizeAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(endMs + Time.finalizeDelayMs);
        _stateStore.RunFinalizerTick(finalizeAtUtc);

        AgentLatestStateDetails? latestState = _stateStore.ReadLatestStateDetails();
        AgentNotificationSuppressionStatus notification = _stateStore.ReadNotificationSuppressionStatus();
        AgentBrightnessActionStatus brightness = _stateStore.ReadBrightnessActionStatus();
        IReadOnlyList<AgentControlEvent> recentEvents = _stateStore.ReadRecentEvents(100);

        _nextBucketSeq++;
        Clock.AdvanceTo(endUtc);

        return new FinalizerTickObservation(
            BucketSeq: _nextBucketSeq - 1,
            BucketStartUtc: startUtc,
            BucketEndUtc: endUtc,
            LatestState: latestState,
            NotificationStatus: notification,
            BrightnessStatus: brightness,
            RecentEvents: recentEvents);
    }

    public LocalApiProjectionSnapshot ReadLocalApiProjection(bool localApiAuthReady = true, string? localApiAuthError = null)
    {
        EnsureNotDisposed();

        AgentServiceStatusSnapshot status = _controller.GetStatus();
        var statusDto = StatusMapper.Map(status, localApiAuthReady, localApiAuthError);
        var snapshotDto = SnapshotMapper.Map(
            _controller.GetCurrentSnapshot(),
            _controller.GetLatestStateDetails());
        IReadOnlyList<Porta.Agent.LocalApi.LocalApiEventResponse> events = EventMapper.MapRecentOrdered(
            _controller.GetRecentEvents(100));
        return new LocalApiProjectionSnapshot(statusDto, snapshotDto, events);
    }

    public void RunFinalizerTick(DateTimeOffset nowUtc)
    {
        EnsureNotDisposed();
        _stateStore.RunFinalizerTick(nowUtc);
    }

    public void UpsertRuntimeStateValue(string key, string valueJson, DateTimeOffset? updatedUtc = null)
    {
        EnsureNotDisposed();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Runtime-state key is required.", nameof(key));
        }

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using SqliteTransaction tx = connection.BeginTransaction();
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
        cmd.Parameters.AddWithValue("$key", key.Trim());
        cmd.Parameters.AddWithValue("$value_json", valueJson ?? string.Empty);
        cmd.Parameters.AddWithValue("$updated_utc", (updatedUtc ?? Clock.UtcNow).ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public string? ReadRuntimeStateValue(string key)
    {
        EnsureNotDisposed();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key=$key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        object? scalar = cmd.ExecuteScalar();
        return scalar == null || scalar == DBNull.Value
            ? null
            : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<(string TimestampUtc, string Action, string IdempotencyKey)> ReadRecentEventRows(int limit = 100)
    {
        EnsureNotDisposed();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT timestamp_utc, action, idempotency_key
            FROM events
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using SqliteDataReader reader = cmd.ExecuteReader();
        var rows = new List<(string TimestampUtc, string Action, string IdempotencyKey)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Ensure deterministic cleanup so scenario DBs are isolated per test.
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // Best effort; the next scenario still gets a unique path.
        }
    }

    private void WriteRawContributor(DateTimeOffset timestampUtc, string contributor)
    {
        WriteSnapshot(
            timestampUtc,
            typing: 0d,
            scroll: 0d,
            focus: 0d,
            contributors: [contributor]);
    }

    private void WriteSnapshot(
        DateTimeOffset timestampUtc,
        double typing,
        double scroll,
        double focus,
        IReadOnlyList<string> contributors)
    {
        var snapshot = new AgentBrainSnapshot(
            TimestampUtc: timestampUtc,
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: scroll,
            FocusSwitchRate: focus,
            SmoothedTypingBurstIntensity: typing,
            SmoothedScrollVelocityIndex: scroll,
            SmoothedFocusSwitchRate: focus,
            FocusStrength: 0.5d,
            OverloadLikelihood: 0.1d,
            FatigueProbability: 0.1d,
            CognitiveStability: 0.7d,
            DominantStateValue: 0.5d,
            DominantStateName: "focus",
            Confidence: 0.8d,
            LastAction: "observe",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: contributors);

        _stateStore.WriteSnapshot(snapshot);
    }

    private static DateTimeOffset AlignToBucketBoundary(DateTimeOffset value)
    {
        long bucketSeq = Time.bucketSeqFromEventTs(value.ToUnixTimeMilliseconds());
        (long startUtcMs, _) = Time.bucketStartEnd(bucketSeq);
        return DateTimeOffset.FromUnixTimeMilliseconds(startUtcMs);
    }

    private static string NormalizeAppHash(string appHash)
    {
        if (string.IsNullOrWhiteSpace(appHash))
        {
            return "app_a";
        }

        return appHash.Trim().ToLowerInvariant();
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DeterministicFinalizerScenarioRunner));
        }
    }

    private void EmitRawKeyboardEvents(ScriptedBucketInput input, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        int keydowns = Math.Max(2, input.KeyboardKeydowns);
        double intensity = Clamp01(input.TypingBurstIntensity);
        int intervalMs = (int)Math.Round(Lerp(220d, 25d, intensity), MidpointRounding.AwayFromZero);
        int jitterMs = Math.Max(1, (int)Math.Round(intervalMs * (keydowns >= 8 ? 0.45d : 0.10d), MidpointRounding.AwayFromZero));
        int offsetMs = 200;
        for (int i = 0; i < keydowns; i++)
        {
            DateTimeOffset ts = ClampToBucket(startUtc, endUtc, offsetMs);
            bool isBackspace = keydowns >= 8 && (i % 5 == 4);
            WriteRawContributor(ts, RawInputContributors.KeyboardKeydown(isBackspace));
            if (i >= keydowns - 1)
            {
                continue;
            }

            if (keydowns >= 8)
            {
                int shortInterval = Math.Max(15, intervalMs / 4);
                int longInterval = Math.Max(shortInterval + 10, intervalMs * 3);
                offsetMs += (i % 2 == 0) ? shortInterval : longInterval;
            }
            else
            {
                int jitterSign = (i % 4) switch
                {
                    0 => -1,
                    1 => 1,
                    2 => 0,
                    _ => -1
                };
                offsetMs += intervalMs + (jitterSign * jitterMs);
            }
        }
    }

    private void EmitRawMouseMoveEvents(ScriptedBucketInput input, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        int moves = Math.Max(1, input.MouseMoves);
        double intensity = Clamp01(input.ScrollVelocityIndex);
        int intervalMs = (int)Math.Round(Lerp(220d, 30d, intensity), MidpointRounding.AwayFromZero);
        int baseDx = (int)Math.Round(Lerp(2d, 220d, intensity), MidpointRounding.AwayFromZero);
        int baseDy = (int)Math.Round(Lerp(1d, 120d, intensity), MidpointRounding.AwayFromZero);
        for (int i = 0; i < moves; i++)
        {
            int offsetMs = 700 + (i * intervalMs);
            DateTimeOffset ts = ClampToBucket(startUtc, endUtc, offsetMs);
            double factor = intensity >= 0.75d
                ? (i % 2 == 0 ? 0.25d : 1.90d)
                : intensity >= 0.35d
                    ? (i % 3 == 0 ? 0.70d : 1.25d)
                    : 1.0d;
            int dx = Math.Max(1, (int)Math.Round(baseDx * factor, MidpointRounding.AwayFromZero));
            int dy = Math.Max(1, (int)Math.Round(baseDy * factor, MidpointRounding.AwayFromZero));
            WriteRawContributor(ts, RawInputContributors.MouseMove(dx, dy));
        }
    }

    private void EmitRawMouseWheelEvents(ScriptedBucketInput input, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        int wheels = Math.Max(0, input.MouseWheels);
        double intensity = Clamp01(input.ScrollVelocityIndex);
        int intervalMs = (int)Math.Round(Lerp(260d, 45d, intensity), MidpointRounding.AwayFromZero);
        int delta = Math.Max(1, (int)Math.Round(Lerp(1d, 160d, intensity), MidpointRounding.AwayFromZero));
        for (int i = 0; i < wheels; i++)
        {
            int offsetMs = 1100 + (i * intervalMs);
            DateTimeOffset ts = ClampToBucket(startUtc, endUtc, offsetMs);
            WriteRawContributor(ts, RawInputContributors.MouseWheel(delta));
        }
    }

    private void EmitRawFocusEvents(ScriptedBucketInput input, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        double focusIntensity = Clamp01(input.FocusSwitchRate);
        int focusEvents;
        int intervalMs;
        if (focusIntensity < 0.15d)
        {
            focusEvents = 1;
            intervalMs = 1100;
        }
        else if (focusIntensity >= 0.75d)
        {
            // Keep intervals above FocusRawAggregates.MinFocusIntervalMs (800ms) so switch/entropy metrics are retained.
            focusEvents = 5;
            intervalMs = 900;
        }
        else
        {
            focusEvents = 3;
            intervalMs = 1200;
        }

        string baseHash = NormalizeAppHash(input.AppHash);
        string altHash = $"{baseHash}_alt";
        for (int i = 0; i < focusEvents; i++)
        {
            int offsetMs = 400 + (i * intervalMs);
            DateTimeOffset ts = ClampToBucket(startUtc, endUtc, offsetMs);
            string appHash = focusEvents <= 1
                ? baseHash
                : focusIntensity >= 0.75d
                    ? $"{baseHash}_{i}"
                    : (i % 2 == 0 ? baseHash : altHash);
            WriteRawContributor(ts, RawInputContributors.Focus(appHash));
        }
    }

    private void EmitRawIdleSamples(ScriptedBucketInput input, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        // Two samples per bucket so idle-derived metrics can be computed deterministically.
        WriteRawContributor(ClampToBucket(startUtc, endUtc, 900), RawInputContributors.Idle(input.IdleSeconds));
        WriteRawContributor(ClampToBucket(startUtc, endUtc, 4900), RawInputContributors.Idle(input.IdleSeconds));
    }

    private static DateTimeOffset ClampToBucket(DateTimeOffset startUtc, DateTimeOffset endUtc, int offsetMs)
    {
        DateTimeOffset raw = startUtc.AddMilliseconds(offsetMs);
        DateTimeOffset min = startUtc.AddMilliseconds(1);
        DateTimeOffset max = endUtc.AddMilliseconds(-1);
        if (raw < min)
        {
            return min;
        }

        if (raw > max)
        {
            return max;
        }

        return raw;
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static double Lerp(double from, double to, double t)
    {
        return from + ((to - from) * Clamp01(t));
    }
}
