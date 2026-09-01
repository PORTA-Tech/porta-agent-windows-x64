using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Buckets;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Core;

/// <summary>
/// Task attribution (2026-09-01) — credit only the work that belongs to the task.
///
/// The rule itself lives in <see cref="TaskCreditDecision"/> and only there. This file is the
/// machinery around it: the footprint the rule reads, the streak it counts, the held seconds it
/// produces, and the release that replays them.
///
/// <b>Compute once, pass down.</b> The bucket transaction resolves the active task id and the
/// credit decision ONCE, above the three writers, and hands both to all three. The existing
/// comments in this store already say why: "these two are the same measurement at two grains and
/// must never disagree, so the rule lives in one predicate". Re-deriving the drift test inside each
/// writer is how the Reports page ends up showing two different totals for the same work.
///
/// <b>Cost.</b> Buckets are 5 seconds, so everything on this path runs 720 times an hour inside the
/// capture write transaction. The footprint is therefore CACHED in memory and invalidated on the
/// four events that can change it — task switch, affinity membership change, confirm, and the
/// epoch check below. There is no per-bucket query of <c>task_app_affinity</c>.
/// </summary>
public sealed partial class SqliteBrainStateStore
{
    /// <summary>Held seconds, waiting for something to resolve them. Visible as unassigned.</summary>
    internal const string PendingStateHeld = "held";

    /// <summary>Replayed onto a task. Terminal.</summary>
    internal const string PendingStateReleased = "released";

    /// <summary>Resolved to nobody's task. Terminal, and still visible as unassigned time.</summary>
    internal const string PendingStateUnassigned = "unassigned";

    internal const string AffinitySourceLearned = "learned";
    internal const string AffinitySourceConfirmed = "confirmed";
    internal const string AffinitySourceProvisional = "provisional";

    /// <summary>
    /// The drift streak, persisted so a service restart mid-drift does not silently restart the
    /// count at zero — which would hand the user another ten minutes of misattributed time every
    /// time the machine woke up.
    /// </summary>
    internal const string DriftStreakRuntimeStateKey = "task_drift_streak_v1";

    /// <summary>
    /// A fingerprint of the install secret that produced the hashes in <c>task_app_affinity</c>.
    /// See <see cref="ResolveAffinityEpochOrEmpty"/>.
    /// </summary>
    internal const string AffinityEpochRuntimeStateKey = "task_affinity_epoch_v1";

    /// <summary>
    /// How many times the drift gate fired today, keyed by the 05:00 goal day.
    ///
    /// Held SECONDS alone cannot tell "one long afternoon on the wrong task" from "the gate is
    /// firing every twenty minutes and chewing the day up". The first is the feature working; the
    /// second is the feature misbehaving, and they need to be distinguishable from a distance —
    /// before the first signal is somebody complaining.
    /// </summary>
    internal const string DriftHoldCountRuntimeStateKey = "task_drift_hold_count_v1";

    // ── in-memory running state, all guarded by _gate with the bucket transaction ──

    private long _affinityCacheTaskId = -1;
    private HashSet<string>? _affinityCache;
    private long _driftStreakTaskId;
    private int _driftStreakBuckets;
    private bool _driftStreakLoaded;

    // The learning window closes once and never reopens, so a task past it need never be asked
    // about again. Without this the hot path would run a tasks lookup 720 times an hour to be told
    // the same thing every time.
    private long _learningWindowClosedForTaskId;

    // The install-secret fingerprint can only change across a process restart — the secret itself
    // is resolved once and cached — so this is checked once per process, not once per bucket.
    private bool _affinityEpochChecked;

    /// <summary>
    /// Everything the bucket transaction needs to attribute one bucket, resolved once.
    /// </summary>
    internal readonly record struct TaskAttributionContext(
        long TaskId,
        TaskCredit Credit,
        TaskLearning Learning,
        int DriftStreakBefore,
        int DriftStreakAfter,
        bool WithinLearningWindow);

    /// <summary>
    /// The single decision point for a finalized bucket. Runs inside the bucket transaction, above
    /// the three writers, and touches the database only for things that cannot be held in memory.
    ///
    /// <paramref name="dominantAppHash"/> MUST be the BUCKET's own dominant hash
    /// (<c>InputBucketRow.DominantAppHash</c>), never the continuity ANCHOR's
    /// (<c>DerivedSessionMetricsResult.DominantAppHash</c>). <c>context_excursion</c> is a working
    /// kind by design, so a short hop is credited; evaluating it against the anchor's hash would
    /// mean a five-minute excursion into anything kept passing the affinity test because the anchor
    /// still matched, and the gate would never see the excursion at all.
    /// </summary>
    internal TaskAttributionContext ResolveTaskAttribution(
        SqliteConnection connection,
        SqliteTransaction tx,
        string? attentionSegmentKind,
        string? dominantAppHash,
        string? bucketStartUtcRaw)
    {
        long taskId = ResolveActiveTaskId(connection, tx);
        if (taskId <= 0)
        {
            // No task is running. Nothing can be credited, held or learned; the app rollup still
            // records the time against task 0 exactly as it always has.
            ResetDriftStreak(connection, tx, taskId: 0, bucketStartUtcRaw);
            return new TaskAttributionContext(
                TaskId: 0,
                Credit: TaskCreditDecision.DecideWithoutAffinity(attentionSegmentKind),
                Learning: TaskLearning.None,
                DriftStreakBefore: 0,
                DriftStreakAfter: 0,
                WithinLearningWindow: false);
        }

        EnsureAffinityEpochCurrent(connection, tx, bucketStartUtcRaw);
        LoadDriftStreakIfNeeded(connection, tx);

        // A task switch or an auto-close is visible here as the active task id changing, which is
        // the one place it can never be missed. Both reset the streak, and both drop the previous
        // task's provisional rows: a footprint must not learn an application from a stretch that
        // belonged to a task the user has already left.
        if (_driftStreakTaskId != taskId)
        {
            DiscardProvisionalAffinity(connection, tx, _driftStreakTaskId);
            _driftStreakTaskId = taskId;
            _driftStreakBuckets = 0;
            PersistDriftStreak(connection, tx, bucketStartUtcRaw);
        }

        IReadOnlySet<string> affinity = ReadAffinityCached(connection, tx, taskId);
        bool withinLearningWindow = IsWithinLearningWindow(connection, tx, taskId);

        int streakBefore = _driftStreakBuckets;
        TaskCredit credit = TaskCreditDecision.Decide(
            attentionSegmentKind,
            dominantAppHash,
            affinity,
            withinLearningWindow,
            streakBefore,
            TaskCreditDecision.DefaultDriftThresholdBuckets);
        TaskLearning learning = TaskCreditDecision.Learning(
            credit, dominantAppHash, affinity, withinLearningWindow);
        int streakAfter = TaskCreditDecision.NextDriftStreak(
            streakBefore, attentionSegmentKind, dominantAppHash, affinity, withinLearningWindow);

        // The bucket that CROSSES the threshold is the last one credited, and it is also the last
        // bucket of a stretch we have just decided did not belong to this task. It must not learn.
        //
        // Without this the write order defeats the design: the crossing bucket's provisional row
        // would be inserted by LearnAffinityForBucket AFTER ApplyStreakTransition below had
        // discarded the stretch's provisional rows, leaving exactly one survivor — the application
        // the gate had just fired on — to be promoted the moment the user came back.
        if (learning == TaskLearning.Provisional
            && streakAfter >= TaskCreditDecision.DefaultDriftThresholdBuckets)
        {
            learning = TaskLearning.None;
        }

        ApplyStreakTransition(connection, tx, taskId, streakBefore, streakAfter, bucketStartUtcRaw);

        return new TaskAttributionContext(
            taskId, credit, learning, streakBefore, streakAfter, withinLearningWindow);
    }

    /// <summary>
    /// The two moments that decide what a drift stretch taught the footprint.
    ///
    /// Ending BELOW the threshold means the user came back: the stretch was an excursion that
    /// belonged to this task, so its provisional rows are promoted — those with at least
    /// <see cref="TaskCreditDecision.LaterLearningMinimumSeconds"/> of credited time become members
    /// and the rest are dropped as noise.
    ///
    /// Reaching the threshold means the user did not come back: the stretch was not this task's
    /// work, so its provisional rows are DISCARDED. Leaving them would mean the very application
    /// the gate just fired on joins the footprint the moment the user returns, and the gate would
    /// never fire on it again.
    /// </summary>
    private void ApplyStreakTransition(
        SqliteConnection connection,
        SqliteTransaction tx,
        long taskId,
        int streakBefore,
        int streakAfter,
        string? bucketStartUtcRaw)
    {
        int threshold = TaskCreditDecision.DefaultDriftThresholdBuckets;

        if (streakAfter == 0 && streakBefore > 0 && streakBefore < threshold)
        {
            if (PromoteProvisionalAffinity(connection, tx, taskId))
            {
                InvalidateAffinityCache();
            }
        }
        else if (streakAfter >= threshold && streakBefore < threshold)
        {
            DiscardProvisionalAffinity(connection, tx, taskId);
            IncrementDriftHoldCount(connection, tx, bucketStartUtcRaw);
            Console.WriteLine(
                $"task_attribution_drift_hold task_id={taskId} buckets={streakAfter} at={bucketStartUtcRaw}");
        }

        if (streakAfter != streakBefore)
        {
            _driftStreakBuckets = streakAfter;
            _driftStreakTaskId = taskId;
            PersistDriftStreak(connection, tx, bucketStartUtcRaw);
        }
    }

    /// <summary>
    /// Drops every cached fact about task attribution. Called by the local-data reset, which clears
    /// sqlite_sequence and therefore RECYCLES task ids: a cached footprint or a closed-window latch
    /// held against id 4 would silently attach to the next task to be given id 4.
    /// </summary>
    private void ResetTaskAttributionRuntimeState()
    {
        InvalidateAffinityCache();
        _driftStreakTaskId = 0;
        _driftStreakBuckets = 0;
        _driftStreakLoaded = true;
        _learningWindowClosedForTaskId = 0;
    }

    /// <summary>
    /// One more drift hold on the bucket's own goal day. Counted at the CROSSING, so this is
    /// episodes and not buckets — the number of separate times Ari decided the user had moved on.
    /// </summary>
    private static void IncrementDriftHoldCount(
        SqliteConnection connection, SqliteTransaction tx, string? bucketStartUtcRaw)
    {
        if (!DateTimeOffset.TryParse(
                bucketStartUtcRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset bucketStart))
        {
            return;
        }

        string day = AgentCoreController.GoalDay(bucketStart.ToLocalTime());
        int count = ReadDriftHoldCount(connection, tx, day) + 1;
        UpsertRuntimeStateValue(
            connection,
            tx,
            DriftHoldCountRuntimeStateKey,
            JsonSerializer.Serialize(new { schema_ver = 1, local_date = day, count }),
            bucketStart.ToUniversalTime().ToString("O"));
    }

    private static int ReadDriftHoldCount(
        SqliteConnection connection, SqliteTransaction? tx, string localDate)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", DriftHoldCountRuntimeStateKey);
        if (cmd.ExecuteScalar() is not string json || json.Length == 0)
        {
            return 0;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            // A stored count for a DIFFERENT day is yesterday's and reads as zero today, which is
            // also how the day rolls over without a scheduled reset anybody has to remember.
            string storedDay = root.TryGetProperty("local_date", out JsonElement d)
                ? d.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(storedDay, localDate, StringComparison.Ordinal))
            {
                return 0;
            }

            return root.TryGetProperty("count", out JsonElement c) && c.TryGetInt32(out int value)
                ? Math.Max(0, value)
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// How many separate times the drift gate fired on the given goal day. Fleet telemetry reads
    /// this beside the held seconds: seconds say how much, this says how often.
    /// </summary>
    public int ReadDriftHoldCountForDay(string localDate)
    {
        if (string.IsNullOrWhiteSpace(localDate))
        {
            return 0;
        }

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        return ReadDriftHoldCount(connection, tx: null, localDate);
    }

    internal static long ResolveActiveTaskId(SqliteConnection connection, SqliteTransaction? tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM tasks WHERE state = 'active' LIMIT 1;";
        object? scalar = cmd.ExecuteScalar();
        return scalar is null || scalar == DBNull.Value ? 0L : Convert.ToInt64(scalar);
    }

    // ── the footprint ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Membership is a single-column test: 'learned' and 'confirmed' are the footprint,
    /// 'provisional' is not. The 2-minute minimum is enforced at PROMOTION time rather than here,
    /// so the hot path never does arithmetic per row.
    /// </summary>
    private IReadOnlySet<string> ReadAffinityCached(
        SqliteConnection connection, SqliteTransaction tx, long taskId)
    {
        if (_affinityCache is not null && _affinityCacheTaskId == taskId)
        {
            return _affinityCache;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                SELECT app_hash FROM task_app_affinity
                WHERE task_id = $task AND source IN ('learned','confirmed');
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                set.Add(reader.GetString(0));
            }
        }

        _affinityCache = set;
        _affinityCacheTaskId = taskId;
        return set;
    }

    private void InvalidateAffinityCache()
    {
        _affinityCache = null;
        _affinityCacheTaskId = -1;
    }

    /// <summary>
    /// The learning window is the task's first 15 minutes of CREDITED work, measured from
    /// <c>tasks.active_seconds</c> — the credited total itself, not wall-clock from
    /// <c>started_at_utc</c>.
    ///
    /// That choice matters. A task started at 9am and genuinely worked on for four minutes before
    /// lunch would, on a wall-clock reading, come back from lunch with its learning window already
    /// spent and a footprint of four minutes. Reading the credited total means the window is
    /// fifteen minutes OF WORK however long they take to happen, which is the thing it is actually
    /// trying to observe.
    /// </summary>
    private bool IsWithinLearningWindow(
        SqliteConnection connection, SqliteTransaction tx, long taskId)
    {
        // Closed is permanent, so once a task has been past the window it is never asked again.
        if (_learningWindowClosedForTaskId == taskId)
        {
            return false;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT active_seconds FROM tasks WHERE id = $task;";
        cmd.Parameters.AddWithValue("$task", taskId);
        object? scalar = cmd.ExecuteScalar();
        double credited = scalar is null || scalar == DBNull.Value ? 0d : Convert.ToDouble(scalar);
        bool within = credited < TaskCreditDecision.LearningWindowSeconds;
        if (!within)
        {
            _learningWindowClosedForTaskId = taskId;
        }

        return within;
    }

    /// <summary>
    /// Learn from this bucket. Runs in the SAME transaction as the credit, on
    /// <see cref="TaskCredit.Credit"/> only.
    /// </summary>
    internal void LearnAffinityForBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        long taskId,
        TaskLearning learning,
        string? dominantAppHash,
        double bucketSeconds,
        string? bucketStartUtcRaw)
    {
        string app = (dominantAppHash ?? string.Empty).Trim();
        if (taskId <= 0 || app.Length == 0 || bucketSeconds <= 0d || learning == TaskLearning.None)
        {
            return;
        }

        string source = learning == TaskLearning.Provisional
            ? AffinitySourceProvisional
            : AffinitySourceLearned;

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            // Reinforce must never rewrite `source`: a 'confirmed' row stays confirmed, which is
            // what keeps it safe from the cap below.
            cmd.CommandText =
                """
                INSERT INTO task_app_affinity (task_id, app_hash, credited_seconds, source, first_seen_utc)
                VALUES ($task, $app, $seconds, $source, $at)
                ON CONFLICT(task_id, app_hash) DO UPDATE SET
                    credited_seconds = credited_seconds + excluded.credited_seconds;
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.Parameters.AddWithValue("$app", app);
            cmd.Parameters.AddWithValue("$seconds", bucketSeconds);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$at", bucketStartUtcRaw ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        if (learning == TaskLearning.Member)
        {
            // A new member changes the SET, so the cache and the streak both have to go.
            EnforceAffinityCap(connection, tx, taskId);
            InvalidateAffinityCache();
            if (_driftStreakBuckets != 0)
            {
                _driftStreakBuckets = 0;
                _driftStreakTaskId = taskId;
                PersistDriftStreak(connection, tx, bucketStartUtcRaw);
            }
        }
    }

    /// <summary>
    /// 24 members per task, keeping the highest credited seconds. A 'confirmed' row is never
    /// evicted — the user said that application is this task's work, and a background counter does
    /// not get to overrule them. Provisional rows are not members and do not count toward the cap.
    /// </summary>
    private static void EnforceAffinityCap(
        SqliteConnection connection, SqliteTransaction tx, long taskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            DELETE FROM task_app_affinity
            WHERE task_id = $task
              AND source = 'learned'
              AND app_hash IN (
                  SELECT app_hash FROM task_app_affinity
                  WHERE task_id = $task AND source = 'learned'
                  ORDER BY credited_seconds ASC, app_hash ASC
                  LIMIT MAX(
                      0,
                      (SELECT COUNT(*) FROM task_app_affinity
                       WHERE task_id = $task AND source IN ('learned','confirmed')) - $cap)
              );
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$cap", TaskCreditDecision.MaxAffinityHashesPerTask);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The user came back below the threshold, so the excursion belonged to this task. Rows with at
    /// least two minutes of credited time join the footprint; the rest were noise and are dropped.
    /// Returns true when the SET actually changed.
    /// </summary>
    private static bool PromoteProvisionalAffinity(
        SqliteConnection connection, SqliteTransaction tx, long taskId)
    {
        int promoted;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                UPDATE task_app_affinity
                SET source = 'learned'
                WHERE task_id = $task AND source = 'provisional' AND credited_seconds >= $min;
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.Parameters.AddWithValue("$min", TaskCreditDecision.LaterLearningMinimumSeconds);
            promoted = cmd.ExecuteNonQuery();
        }

        DiscardProvisionalAffinity(connection, tx, taskId);

        if (promoted > 0)
        {
            EnforceAffinityCap(connection, tx, taskId);
        }

        return promoted > 0;
    }

    private static void DiscardProvisionalAffinity(
        SqliteConnection connection, SqliteTransaction tx, long taskId)
    {
        if (taskId <= 0)
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "DELETE FROM task_app_affinity WHERE task_id = $task AND source = 'provisional';";
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.ExecuteNonQuery();
    }

    // ── the streak ────────────────────────────────────────────────────────────────────

    private void LoadDriftStreakIfNeeded(SqliteConnection connection, SqliteTransaction tx)
    {
        if (_driftStreakLoaded)
        {
            return;
        }

        _driftStreakLoaded = true;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", DriftStreakRuntimeStateKey);
        object? scalar = cmd.ExecuteScalar();
        string? json = scalar as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            _driftStreakTaskId = root.TryGetProperty("task_id", out JsonElement idEl)
                && idEl.TryGetInt64(out long id) ? id : 0L;
            _driftStreakBuckets = root.TryGetProperty("buckets", out JsonElement bEl)
                && bEl.TryGetInt32(out int b) ? Math.Max(0, b) : 0;
        }
        catch (JsonException)
        {
            // Unreadable running state resets to zero. Failing open costs at most one more drift
            // window of misattributed time; failing closed would hold a day on a parse error.
            _driftStreakTaskId = 0;
            _driftStreakBuckets = 0;
        }
    }

    private void PersistDriftStreak(
        SqliteConnection connection, SqliteTransaction tx, string? bucketStartUtcRaw)
    {
        string json = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            task_id = _driftStreakTaskId,
            buckets = _driftStreakBuckets
        });
        UpsertRuntimeStateValue(
            connection,
            tx,
            DriftStreakRuntimeStateKey,
            json,
            string.IsNullOrWhiteSpace(bucketStartUtcRaw)
                ? DateTimeOffset.UtcNow.ToString("O")
                : bucketStartUtcRaw!);
    }

    private void ResetDriftStreak(
        SqliteConnection connection, SqliteTransaction tx, long taskId, string? bucketStartUtcRaw)
    {
        LoadDriftStreakIfNeeded(connection, tx);
        if (_driftStreakTaskId == taskId && _driftStreakBuckets == 0)
        {
            return;
        }

        DiscardProvisionalAffinity(connection, tx, _driftStreakTaskId);
        _driftStreakTaskId = taskId;
        _driftStreakBuckets = 0;
        InvalidateAffinityCache();
        PersistDriftStreak(connection, tx, bucketStartUtcRaw);
    }

    // ── hash instability ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>app_hash</c> is <c>HMAC-SHA256(installSecret, identifier)</c>. A reinstall or a rotated
    /// secret changes EVERY hash on the machine at once, so every task would drift simultaneously
    /// and the user's whole day would be held — the loudest possible version of the inverse bug.
    ///
    /// The condition is detected directly rather than guessed at from match rates: a fingerprint of
    /// the current secret is stored beside the footprints, and if it changes then every stored hash
    /// is addressed to a secret that no longer exists. The footprints are cleared, every task falls
    /// back to its learning window, and the fact is logged. Do NOT fail closed here.
    ///
    /// The fingerprint is an HMAC of a fixed constant, so it identifies the secret without
    /// containing it and reveals nothing about what the user ran.
    /// </summary>
    private void EnsureAffinityEpochCurrent(
        SqliteConnection connection, SqliteTransaction tx, string? bucketStartUtcRaw)
    {
        if (_affinityEpochChecked)
        {
            return;
        }

        _affinityEpochChecked = true;
        string epoch = ResolveAffinityEpochOrEmpty();
        if (epoch.Length == 0)
        {
            return; // cannot read the secret: leave everything alone rather than wipe on a guess.
        }

        string stored;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT value_json FROM runtime_state WHERE key = $key;";
            read.Parameters.AddWithValue("$key", AffinityEpochRuntimeStateKey);
            stored = read.ExecuteScalar() as string ?? string.Empty;
        }

        if (string.Equals(stored, epoch, StringComparison.Ordinal))
        {
            return;
        }

        if (stored.Length > 0)
        {
            using var wipe = connection.CreateCommand();
            wipe.Transaction = tx;
            wipe.CommandText = "DELETE FROM task_app_affinity;";
            int cleared = wipe.ExecuteNonQuery();
            InvalidateAffinityCache();
            _driftStreakBuckets = 0;
            Console.WriteLine(
                $"task_affinity_epoch_changed cleared_rows={cleared} at={bucketStartUtcRaw} "
                + "reason=install_secret_rotated_every_hash_is_new");
        }

        UpsertRuntimeStateValue(
            connection,
            tx,
            AffinityEpochRuntimeStateKey,
            epoch,
            string.IsNullOrWhiteSpace(bucketStartUtcRaw)
                ? DateTimeOffset.UtcNow.ToString("O")
                : bucketStartUtcRaw!);
    }

    private static string ResolveAffinityEpochOrEmpty()
    {
        try
        {
            return BucketPrivacyContract.AppHashEpochToken();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── holding ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Real work that does not belong to the active task. Neither credited nor discarded.
    ///
    /// <c>local_date</c> is resolved HERE, at hold time, from the BUCKET's own local timestamp using
    /// the same 05:00-boundary goal day <c>daily_goals</c> and <c>task_day_hours</c> use. Storing it
    /// now is what makes release safe: a release that recomputed the day at release time would move
    /// yesterday's work onto today.
    ///
    /// The primary key <c>(task_id, bucket_start_utc)</c> makes this idempotent, so a replayed
    /// bucket cannot hold the same seconds twice. Note the explicit task_id — nothing here reads
    /// <c>WHERE state = 'active'</c>, because the auto-close this change re-arms will have returned
    /// the task to pending long before anything resolves these rows.
    /// </summary>
    internal static void HoldBucketForTask(
        SqliteConnection connection,
        SqliteTransaction tx,
        long taskId,
        double bucketSeconds,
        TaskCredit credit,
        string? dominantAppHash,
        string? attentionSegmentKind,
        string? bucketStartUtcRaw)
    {
        if (credit != TaskCredit.Hold || taskId <= 0 || bucketSeconds <= 0d)
        {
            return;
        }

        if (!DateTimeOffset.TryParse(
                bucketStartUtcRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset bucketStart))
        {
            // Fail closed on an unreadable timestamp, the same way the rollup does: a held row
            // filed under a guessed day could never be released to the right one.
            return;
        }

        string localDay = AgentCoreController.GoalDay(bucketStart.ToLocalTime());

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO pending_task_seconds
                (task_id, bucket_start_utc, local_date, seconds, app_hash, kind, state)
            VALUES ($task, $at, $day, $seconds, $app, $kind, 'held')
            ON CONFLICT(task_id, bucket_start_utc) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$at", bucketStart.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$day", localDay);
        cmd.Parameters.AddWithValue("$seconds", bucketSeconds);
        cmd.Parameters.AddWithValue("$app", (dominantAppHash ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("$kind", AttentionSegmentKinds.NormalizeKind(attentionSegmentKind));
        cmd.ExecuteNonQuery();
    }

    // ── reading held time ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The day's unassigned seconds — real work Ari measured but could not place on a task.
    ///
    /// This number is the entire justification for holding time rather than crediting it. If it is
    /// not visible then the change is indistinguishable from silently losing the user's afternoon,
    /// which is a worse bug than the one being fixed.
    ///
    /// It is NOT <c>unattributed_hours</c>, which already exists and means something completely
    /// different: the diagnostic gap between a task's <c>active_seconds</c> and the rollup's
    /// lifetime seconds, caused by the rollup dropping buckets with no identifiable application.
    /// The two are computed incompatibly and must never share a label.
    /// </summary>
    public double ReadUnassignedSecondsForDay(string localDate)
    {
        if (string.IsNullOrWhiteSpace(localDate))
        {
            return 0d;
        }

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        // 'held' and 'unassigned' are both unassigned time. 'released' has landed on a task and is
        // counted there, so counting it here as well would double it.
        cmd.CommandText =
            """
            SELECT COALESCE(SUM(seconds), 0) FROM pending_task_seconds
            WHERE local_date = $day AND state IN ('held','unassigned');
            """;
        cmd.Parameters.AddWithValue("$day", localDate);
        object? scalar = cmd.ExecuteScalar();
        return scalar is null || scalar == DBNull.Value ? 0d : Convert.ToDouble(scalar);
    }

    /// <summary>Held rows for one day, newest bucket last. The input to the ask in the next chat.</summary>
    public IReadOnlyList<AgentHeldSeconds> ReadHeldSecondsForDay(string localDate)
    {
        var rows = new List<AgentHeldSeconds>();
        if (string.IsNullOrWhiteSpace(localDate))
        {
            return rows;
        }

        using ReadGateLease _ = AcquireReadGate();
        using var connection = DbOpen.OpenReadConnection(_connectionString);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT task_id, bucket_start_utc, local_date, seconds, app_hash, kind, state
            FROM pending_task_seconds
            WHERE local_date = $day
            ORDER BY bucket_start_utc;
            """;
        cmd.Parameters.AddWithValue("$day", localDate);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AgentHeldSeconds(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return rows;
    }

    // ── release ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replays held buckets onto a task through the SAME three writers, AT THEIR ORIGINAL BUCKET
    /// TIMESTAMPS. Returns the seconds actually released.
    ///
    /// Three things make this safe, and all three are load-bearing:
    ///
    /// <b>Original timestamps.</b> <c>AccumulateTaskDayHours</c> resolves the 05:00 goal day from
    /// the bucket and <c>AccumulateAppUsageRollup</c> resolves the hour from the bucket. Replaying
    /// at "now" would move yesterday's work onto today — the same mistake
    /// <c>AutoCloseStaleActiveTask</c> documents in its own summary. The stored <c>local_date</c> is
    /// used rather than recomputed, so a hold spanning the 05:00 boundary lands on two days.
    ///
    /// <b>One transaction.</b> The state flip happens in the same transaction as the replay, so a
    /// crash between them cannot credit the same seconds twice.
    ///
    /// <b>Frozen reports.</b> A sent report is a permanent snapshot behind a client link. Held rows
    /// covered by one may only ever resolve to <c>unassigned</c>, never onto a task: reattributing
    /// them would change history a client has already read. Amending a sent report is a separate
    /// decision and is not this code's to make.
    /// </summary>
    public double ReleaseHeldSecondsToTask(long heldTaskId, long targetTaskId, string? localDate = null)
    {
        if (heldTaskId <= 0 || targetTaskId <= 0)
        {
            return 0d;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                string frozenThrough = ReadFrozenReportCoverEnd(connection, tx);
                double released = 0d;

                foreach (HeldRow row in ReadHeldRows(connection, tx, heldTaskId, localDate))
                {
                    if (frozenThrough.Length > 0
                        && string.CompareOrdinal(row.LocalDate, frozenThrough) <= 0)
                    {
                        // Covered by a report the client already has. Unassigned is the only
                        // resolution left; it is honest and it changes nothing they have read.
                        SetPendingState(connection, tx, heldTaskId, row.BucketStartUtc, PendingStateUnassigned);
                        continue;
                    }

                    AttributeBucketToTask(
                        connection, tx, targetTaskId, row.Seconds, TaskCredit.Credit, row.BucketStartUtc);
                    AccumulateTaskDayHours(
                        connection, tx, targetTaskId, row.Seconds, TaskCredit.Credit,
                        row.BucketStartUtc, row.LocalDate);
                    AccumulateAppUsageRollup(
                        connection, tx, targetTaskId, row.Seconds, TaskCredit.Credit,
                        row.AppHash, row.BucketStartUtc);

                    // Same transaction as the replay. This is the idempotency guarantee: a second
                    // release finds no 'held' rows and credits nothing.
                    SetPendingState(connection, tx, heldTaskId, row.BucketStartUtc, PendingStateReleased);
                    released += row.Seconds;
                }

                tx.Commit();
                InvalidateAffinityCache();
                return released;
            }
            catch
            {
                TryRollback(tx);
                throw;
            }
        }
    }

    /// <summary>
    /// The other resolution: this was not work on any task. The seconds stay visible as unassigned
    /// and never reach a task. Returns the seconds marked.
    /// </summary>
    public double MarkHeldSecondsUnassigned(long heldTaskId, string? localDate = null)
    {
        if (heldTaskId <= 0)
        {
            return 0d;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                double marked = 0d;
                foreach (HeldRow row in ReadHeldRows(connection, tx, heldTaskId, localDate))
                {
                    SetPendingState(connection, tx, heldTaskId, row.BucketStartUtc, PendingStateUnassigned);
                    marked += row.Seconds;
                }

                tx.Commit();
                return marked;
            }
            catch
            {
                TryRollback(tx);
                throw;
            }
        }
    }

    private readonly record struct HeldRow(
        string BucketStartUtc, string LocalDate, double Seconds, string AppHash, string Kind);

    private static List<HeldRow> ReadHeldRows(
        SqliteConnection connection, SqliteTransaction tx, long taskId, string? localDate)
    {
        var rows = new List<HeldRow>();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = string.IsNullOrWhiteSpace(localDate)
            ? """
              SELECT bucket_start_utc, local_date, seconds, app_hash, kind
              FROM pending_task_seconds
              WHERE task_id = $task AND state = 'held'
              ORDER BY bucket_start_utc;
              """
            : """
              SELECT bucket_start_utc, local_date, seconds, app_hash, kind
              FROM pending_task_seconds
              WHERE task_id = $task AND state = 'held' AND local_date = $day
              ORDER BY bucket_start_utc;
              """;
        cmd.Parameters.AddWithValue("$task", taskId);
        if (!string.IsNullOrWhiteSpace(localDate))
        {
            cmd.Parameters.AddWithValue("$day", localDate);
        }

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new HeldRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static void SetPendingState(
        SqliteConnection connection,
        SqliteTransaction tx,
        long taskId,
        string bucketStartUtc,
        string state)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            UPDATE pending_task_seconds SET state = $state
            WHERE task_id = $task AND bucket_start_utc = $at AND state = 'held';
            """;
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$at", bucketStartUtc);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The newest cover date of a report that has gone out. 'failed' is excluded because nothing
    /// reached a client; 'pending' is INCLUDED because an unconfirmed send may well have arrived,
    /// and the safe reading of an unknown outcome is that the client has seen it.
    /// </summary>
    private static string ReadFrozenReportCoverEnd(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT COALESCE(MAX(range_end_local), '') FROM sent_reports WHERE send_state <> 'failed';";
        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    /// <summary>
    /// The user says this application IS this task's work. A confirmed member is never evicted by
    /// the cap. Kept deliberately narrow: it makes a hash a member of the footprint and nothing
    /// else. It does NOT promote a non-working kind into credited time — a task whose work
    /// genuinely is YouTube needs the stillness cap as well, and that is a separate decision.
    /// </summary>
    public void ConfirmTaskAppAffinity(long taskId, string appHash, DateTimeOffset nowUtc)
    {
        string app = (appHash ?? string.Empty).Trim();
        if (taskId <= 0 || app.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var connection = DbOpen.OpenConnection(_connectionString);
            using SqliteTransaction tx = BeginImmediateTransaction(connection);
            try
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        """
                        INSERT INTO task_app_affinity (task_id, app_hash, credited_seconds, source, first_seen_utc)
                        VALUES ($task, $app, 0, 'confirmed', $at)
                        ON CONFLICT(task_id, app_hash) DO UPDATE SET source = 'confirmed';
                        """;
                    cmd.Parameters.AddWithValue("$task", taskId);
                    cmd.Parameters.AddWithValue("$app", app);
                    cmd.Parameters.AddWithValue("$at", nowUtc.ToUniversalTime().ToString("O"));
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                InvalidateAffinityCache();
                _driftStreakBuckets = 0;
            }
            catch
            {
                TryRollback(tx);
                throw;
            }
        }
    }
}
