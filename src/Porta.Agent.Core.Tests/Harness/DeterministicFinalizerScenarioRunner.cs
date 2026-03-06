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
    private readonly SqliteBrainStateStore _innerStore;
    private readonly BucketFinalizerStateStore _stateStore;
    private readonly AgentCoreController _controller;
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

        for (int i = 0; i < Math.Max(0, input.KeyboardKeydowns); i++)
        {
            WriteRawContributor(startUtc.AddMilliseconds(200 + (i * 30)), RawInputContributors.KeyboardKeydown(isBackspace: false));
        }

        for (int i = 0; i < Math.Max(0, input.MouseMoves); i++)
        {
            WriteRawContributor(startUtc.AddMilliseconds(500 + (i * 30)), RawInputContributors.MouseMove(dx: 8, dy: 2));
        }

        for (int i = 0; i < Math.Max(0, input.MouseWheels); i++)
        {
            WriteRawContributor(startUtc.AddMilliseconds(800 + (i * 40)), RawInputContributors.MouseWheel(delta: 1));
        }

        WriteRawContributor(startUtc.AddMilliseconds(900), RawInputContributors.Idle(input.IdleSeconds));

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
}
