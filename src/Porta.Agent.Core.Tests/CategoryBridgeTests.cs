using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Core.Tests;

// Category bridge (v1.2.112). The brain runs in the SERVICE process, but categories,
// labels, and meeting flags were only ever registered in the USER-AGENT process —
// AppCategoryClassifier's registries are static and per-process, so the service's copies
// were permanently empty and every category-aware rule (2f off-task gate, 2g return-loop,
// 2b meeting shield) ran dark. Existing unit tests missed it because they called
// RegisterHashCategory in-process. These tests therefore seed ONLY via the ingest API
// path — the way a real service process learns about apps.
//
// Collection: ResetForTests() wipes the PROCESS-WIDE static registries; every test class
// that registers into them must run serialized with this one or lose its state mid-test.
[Collection(AppCategoryRegistryCollection.Name)]
public sealed class CategoryBridgeTests
{
    // Hex-style identities: the controller's ingest validation (FocusHashPattern) only
    // accepts what real hashed identities look like.
    private const string WorkHash = "aa11bb22cc33dd44";
    private const string YoutubeHash = "ee55ff66aa77bb88";
    private const string DocsHash = "cc99dd00ee11ff22";
    private const string MeetingHash = "ab12cd34ef56ab78";

    // ── 1. Contract: additive fields, old clients unchanged ──

    [Fact]
    public void Contract_NewFieldsRoundTrip()
    {
        string json = """
            {
              "schema_ver": 2,
              "agent_instance_id": "agent-1",
              "session_id": "session-1",
              "keyboard_keydown_count": 1,
              "keyboard_backspace_count": 0,
              "mouse_wheel_delta": 0,
              "mouse_move_dx": 0,
              "mouse_move_dy": 0,
              "focus_app_hash": "abc123",
              "idle_seconds": 0,
              "focus_app_category": 1,
              "focus_app_is_meeting": true,
              "focus_app_label": "youtube"
            }
            """;
        LocalApiInputSignalsRequest? request = JsonSerializer.Deserialize<LocalApiInputSignalsRequest>(json);
        Assert.NotNull(request);
        Assert.Equal((int)AppCategory.Entertainment, request!.FocusAppCategory);
        Assert.True(request.FocusAppIsMeeting);
        Assert.Equal("youtube", request.FocusAppLabel);
    }

    [Fact]
    public void Contract_OldJsonWithoutNewFields_TakesDefaults()
    {
        // An old client's exact wire format must keep working: category Unknown, not a
        // meeting, no label — byte-for-byte the old behavior.
        string json = """
            {
              "schema_ver": 2,
              "agent_instance_id": "agent-1",
              "session_id": "session-1",
              "keyboard_keydown_count": 1,
              "keyboard_backspace_count": 0,
              "mouse_wheel_delta": 0,
              "mouse_move_dx": 0,
              "mouse_move_dy": 0,
              "focus_app_hash": "abc123",
              "idle_seconds": 0
            }
            """;
        LocalApiInputSignalsRequest? request = JsonSerializer.Deserialize<LocalApiInputSignalsRequest>(json);
        Assert.NotNull(request);
        Assert.Equal(0, request!.FocusAppCategory);
        Assert.False(request.FocusAppIsMeeting);
        Assert.Null(request.FocusAppLabel);
    }

    // ── 2. Ingest registers into the service-process registries ──

    [Fact]
    public void Ingest_RegistersCategoryLabelAndMeeting()
    {
        AppCategoryClassifier.ResetForTests();
        AgentCoreController controller = BuildController();

        Ingest(controller, YoutubeHash, category: (int)AppCategory.Entertainment, label: "youtube");
        Ingest(controller, MeetingHash, isMeeting: true, label: "zoom");

        Assert.Equal(AppCategory.Entertainment, AppCategoryClassifier.GetCategoryForHash(YoutubeHash));
        Assert.Equal("youtube", AppCategoryClassifier.GetLabelForHash(YoutubeHash));
        Assert.True(AppCategoryClassifier.IsMeetingApp(MeetingHash));
        Assert.False(AppCategoryClassifier.IsMeetingApp(YoutubeHash));
    }

    [Fact]
    public void Ingest_UndefinedCategoryValue_IsIgnoredNeverThrows()
    {
        AppCategoryClassifier.ResetForTests();
        AgentCoreController controller = BuildController();

        Ingest(controller, YoutubeHash, category: 99);

        Assert.Equal(AppCategory.Unknown, AppCategoryClassifier.GetCategoryForHash(YoutubeHash));
    }

    // ── 3. THE bridge test: 2f fires when seeded EXCLUSIVELY via the API path ──
    // This test MUST fail on pre-bridge code: without ingest-side registration the
    // service-process category for the YouTube hash stays Unknown and the off-task gate
    // (which requires Entertainment/SocialMedia/Gaming) never fires.

    [Fact]
    public void Bridge_OffTaskFires_WhenCategoryArrivesOnlyViaIngest()
    {
        AppCategoryClassifier.ResetForTests();
        AgentCoreController controller = BuildController();
        Ingest(controller, YoutubeHash, category: (int)AppCategory.Entertainment, label: "youtube");

        DerivedSessionMetricsResult result = Run2fScenario(offAnchorHash: YoutubeHash);

        // Same expectation as the existing 2f in-process test — but the category came in
        // through the wire, exactly as a real service process learns it.
        Assert.Equal("outside_focus_context", result.AttentionSegmentReasonCode);
        Assert.True(result.DistractionEpisodeActive);
    }

    // ── 4. Meeting bridge: 2b sees the flag when seeded only via ingest ──

    [Fact]
    public void Bridge_MeetingFlagViaIngest_ClassifiesInMeetingNotAway()
    {
        AppCategoryClassifier.ResetForTests();
        AgentCoreController controller = BuildController();
        Ingest(controller, MeetingHash, isMeeting: true, label: "zoom");

        using SqliteConnection connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InputBucketRow row = BuildStableRow() with
        {
            DominantAppHash = MeetingHash,
            TrueBreakFlag = 1,
            TrueBreakSeconds = 305d,
            IdleSeconds = 305d
        };

        // meetingInProgress is resolved in production as IsMeetingApp(mapped.DominantAppHash)
        // (SqliteBrainStateStore finalize path) — replicate that exact call here.
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            BuildPrimitive(),
            BaselineGatingResult.Warm,
            meetingInProgress: AppCategoryClassifier.IsMeetingApp(row.DominantAppHash));

        Assert.Equal("in_meeting", result.AttentionSegmentKind);
        Assert.False(result.DistractionEpisodeActive);
    }

    // ── 5. Regression: 2g stays quiet for work↔work cycling seeded via ingest ──

    [Fact]
    public void Bridge_WorkToWorkPingPongViaIngest_NeverFlags()
    {
        AppCategoryClassifier.ResetForTests();
        AgentCoreController controller = BuildController();
        Ingest(controller, WorkHash, category: (int)AppCategory.Development, label: "code");
        Ingest(controller, DocsHash, category: (int)AppCategory.Productivity, label: "notion");

        using SqliteConnection connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 60d,
            saContextAppHash: DocsHash,
            continuityState: "keep",
            continuityScore: 1d,
            continuitySessionActiveInteractionSeconds: 60d,
            continuityPingPongRecentHashesJson:
                $"[[\"{WorkHash}\",50],[\"{DocsHash}\",55],[\"{WorkHash}\",60]]",
            continuityPingPongCycleCount: 2,
            continuityPingPongWindowStartSeconds: 50d);

        InputBucketRow row = BuildStableRow() with { DominantAppHash = DocsHash };
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection, tx, row, BuildPrimitive(), BaselineGatingResult.Warm);

        Assert.NotEqual("return_loop_distraction", result.AttentionSegmentReasonCode);
        Assert.NotEqual("outside_focus_context", result.AttentionSegmentReasonCode);
        Assert.False(result.DistractionEpisodeActive);
    }

    // ── 6. Unknown stays conservative: no category on the wire → no off-task, ever ──

    [Fact]
    public void Bridge_UnknownCategoryViaIngest_NeverFiresOffTask()
    {
        AppCategoryClassifier.ResetForTests();
        AgentCoreController controller = BuildController();
        Ingest(controller, YoutubeHash); // no category field — old client behavior

        DerivedSessionMetricsResult result = Run2fScenario(offAnchorHash: YoutubeHash);

        Assert.NotEqual("outside_focus_context", result.AttentionSegmentReasonCode);
    }

    // ── Harness ──

    private static AgentCoreController BuildController()
        => new(new LocalApiContractFakeStore());

    // Drives the controller's ingest entry — the same method the local API endpoint calls.
    // The fake store has no bucket ingress wired, so ingest may stop after validation +
    // registration; that is exactly the surface under test (registration must happen
    // BEFORE bucket processing, so it must have happened by the time ingest returns/throws).
    private static void Ingest(
        AgentCoreController controller,
        string focusAppHash,
        int category = 0,
        bool isMeeting = false,
        string? label = null)
    {
        try
        {
            controller.IngestInputSignals(new InputSignalsPacket(
                SchemaVer: 2,
                AgentInstanceId: "agent-bridge-test",
                SessionId: "session-bridge-test",
                WindowStartUtc: DateTimeOffset.UtcNow.AddSeconds(-4),
                WindowEndUtc: DateTimeOffset.UtcNow,
                KeyboardKeydownCount: 3,
                KeyboardBackspaceCount: 0,
                MouseWheelDelta: 0,
                MouseMoveDx: 5d,
                MouseMoveDy: 5d,
                FocusAppHash: focusAppHash,
                IdleSeconds: 0d,
                LastInputEventUtc: DateTimeOffset.UtcNow,
                FocusAppCategory: category,
                FocusAppIsMeeting: isMeeting,
                FocusAppLabel: label));
        }
        catch (InvalidOperationException)
        {
            // input_signals_ingress_unavailable / consent gates on the fake store — the
            // downstream pipeline is not under test here; registration precedes it.
        }
    }

    // The exact 2f scenario from DerivedSessionMetricsComputerTests: anchor locked on a
    // work app, sustained interaction on a different app past the off-task threshold.
    private static DerivedSessionMetricsResult Run2fScenario(string offAnchorHash)
    {
        using SqliteConnection connection = OpenInMemoryDb();
        using SqliteTransaction tx = connection.BeginTransaction();
        InsertDerivedSessionState(
            connection,
            tx,
            sustainedAttentionDurationSec: 300d,
            saContextAppHash: "app_hash_work",
            continuityState: "keep",
            continuityScore: 1d,
            continuityCandidateAnchorAppHash: offAnchorHash,
            continuityCandidateAnchorAccumulatedSeconds: 100d,
            continuityAnchorLockedSeconds: 100d,
            continuitySessionActiveInteractionSeconds: 300d);

        InputBucketRow row = BuildStableRow() with { DominantAppHash = offAnchorHash };
        return DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection, tx, row, BuildPrimitive(), BaselineGatingResult.Warm);
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

    private static void InsertDerivedSessionState(
        SqliteConnection connection,
        SqliteTransaction tx,
        double sustainedAttentionDurationSec,
        string saContextAppHash,
        string continuityState,
        double continuityScore,
        string continuityCandidateAnchorAppHash = "",
        double continuityCandidateAnchorAccumulatedSeconds = 0d,
        double continuityAnchorLockedSeconds = 0d,
        double continuitySessionActiveInteractionSeconds = 0d,
        string continuityPingPongRecentHashesJson = "",
        long continuityPingPongCycleCount = 0,
        double continuityPingPongWindowStartSeconds = 0d)
    {
        string valueJson = JsonSerializer.Serialize(new
        {
            schema_ver = 1,
            contract_ver = "v1",
            task_continuity_score = continuityScore,
            task_continuity_seconds = sustainedAttentionDurationSec,
            continuity_state = continuityState,
            continuity_confidence = 1d,
            continuity_anchor_app_hash = saContextAppHash,
            continuity_last_update_bucket_seq = 0L,
            continuity_idle_pause_seconds = 0d,
            continuity_context_switch_seconds = 0d,
            continuity_reason_code = "",
            continuity_instability_seconds = 0d,
            focus_episode_candidate_seconds = 0d,
            sa_seconds = sustainedAttentionDurationSec,
            sa_context_app_hash = saContextAppHash,
            sa_last_update_bucket_seq = 0L,
            sa_freeze_reason = "",
            sa_idle_pause_seconds = 0d,
            sa_context_switch_seconds = 0d,
            sustained_attention_duration_sec = sustainedAttentionDurationSec,
            idle_fragmentation_index = 0.1d,
            dominant_app_hash = saContextAppHash,
            bucket_seq = 0L,
            candidate_anchor_app_hash = continuityCandidateAnchorAppHash,
            candidate_anchor_accumulated_seconds = continuityCandidateAnchorAccumulatedSeconds,
            anchor_locked_seconds = continuityAnchorLockedSeconds,
            session_active_interaction_seconds = continuitySessionActiveInteractionSeconds,
            ping_pong_recent_hashes_json = continuityPingPongRecentHashesJson,
            ping_pong_cycle_count = continuityPingPongCycleCount,
            ping_pong_window_start_seconds = continuityPingPongWindowStartSeconds
        });

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc)
            VALUES ($key, $value, $updated)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json;
            """;
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.DerivedSessionMetrics.RuntimeStateKey);
        cmd.Parameters.AddWithValue("$value", valueJson);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static PrimitiveComputationResult BuildPrimitive()
    {
        return new PrimitiveComputationResult(
            TypingRaw: 0.3d,
            ScrollRaw: 0.4d,
            FocusRaw: 0.5d,
            TypingSmoothed: 0.3d,
            ScrollSmoothed: 0.4d,
            FocusSmoothed: 0.5d,
            TypingChannelAvailable: true,
            ScrollChannelAvailable: true,
            FocusChannelAvailable: true);
    }

    private static InputBucketRow BuildStableRow()
    {
        return new InputBucketRow(
            BucketSeq: 1,
            TimestampUtc: "2026-03-03T00:00:05.000Z",
            StartUtc: "2026-03-03T00:00:05.000Z",
            TypingBurstIntensity: 0.2d,
            ScrollVelocityIndex: 0.3d,
            FocusSwitchRate: 0.10d,
            KeyboardIkiMean: 120d,
            KeyboardIkiCv: 0.2d,
            KeyboardBurstRate: 0.4d,
            KeyboardBackspaceRate: 0.1d,
            MouseSpeedMean: 150d,
            MouseJerkP95: 1000d,
            MouseScrollSpeed: 80d,
            MouseMicroCorrectionRate: 0.1d,
            FocusAppEntropy: 1.0d,
            FocusSwitchCount: 1,
            DominantAppHash: "app_hash_work",
            FocusActiveAppCount: 2d,
            IdleSeconds: 0.2d,
            IdleMicroIdleCount: 1d,
            IdleMicroIdleSeconds: 0.2d,
            TrueBreakSeconds: 0d,
            TrueBreakFlag: 0,
            QualityScore: 0.95d,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
    }
}
