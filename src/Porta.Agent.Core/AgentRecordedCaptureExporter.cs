using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Core;

public sealed record AgentRecordedCaptureExportRequest(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Label = null,
    string? Notes = null,
    string? OutputRoot = null,
    int TimelineBucketSeconds = 1,
    int InputWindowLimit = 50_000,
    int EventLimit = 10_000,
    int DiagnosticsLimit = 10_000);

public sealed record AgentRecordedCaptureSummary(
    string CaptureId,
    string Label,
    DateTimeOffset ExportedAtUtc,
    DateTimeOffset RequestedFromUtc,
    DateTimeOffset RequestedToUtc,
    DateTimeOffset? ObservedFromUtc,
    DateTimeOffset? ObservedToUtc,
    int TimelineBucketSeconds,
    int InputWindowCount,
    int SnapshotCount,
    int EventCount,
    int DiagnosticsSnapshotCount,
    IReadOnlyList<string> AgentInstanceIds,
    IReadOnlyList<string> SessionIds,
    IReadOnlyDictionary<string, int> EventActionCounts,
    string? LatestDominantState,
    string? LatestAction,
    bool HasInterventionEvents);

public sealed record AgentRecordedCaptureInventoryItem(
    string CaptureId,
    string Label,
    string CaptureDirectoryName,
    string CaptureDirectoryPath,
    DateTimeOffset ExportedAtUtc,
    DateTimeOffset RequestedFromUtc,
    DateTimeOffset RequestedToUtc,
    int InputWindowCount,
    int SnapshotCount,
    int EventCount,
    int DiagnosticsSnapshotCount,
    bool HasInterventionEvents,
    IReadOnlyList<string> AgentInstanceIds,
    IReadOnlyList<string> SessionIds,
    IReadOnlyDictionary<string, int> EventActionCounts);

public sealed record AgentRecordedCaptureExportResult(
    string CaptureId,
    string CaptureDirectoryPath,
    string InventoryPath,
    AgentRecordedCaptureSummary Summary);

public sealed class AgentRecordedCaptureExporter
{
    private const string CaptureSchema = "porta.phase1.capture.v1";
    private const string InventorySchema = "porta.phase1.capture.inventory.v1";
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };
    private readonly string _dbPath;

    public AgentRecordedCaptureExporter(string? dbPath = null)
    {
        _dbPath = string.IsNullOrWhiteSpace(dbPath)
            ? DbPaths.GetCoreStateDbPath()
            : dbPath.Trim();
    }

    public AgentRecordedCaptureExportResult Export(AgentRecordedCaptureExportRequest request)
    {
        if (request.ToUtc < request.FromUtc)
        {
            throw new ArgumentException("Capture export requires toUtc >= fromUtc.");
        }

        if (!File.Exists(_dbPath))
        {
            throw new FileNotFoundException("Core state database was not found.", _dbPath);
        }

        string outputRoot = string.IsNullOrWhiteSpace(request.OutputRoot)
            ? GetDefaultOutputRoot()
            : request.OutputRoot.Trim();
        Directory.CreateDirectory(outputRoot);

        var reader = new RecordedCaptureReadOnlyReader(_dbPath);
        IReadOnlyList<AgentInputSignalWindow> windows = reader.ReadInputSignalWindows(
            request.FromUtc,
            request.ToUtc,
            request.InputWindowLimit);
        IReadOnlyList<AgentBrainSnapshot> snapshots = reader.ReadTimeline(
            request.FromUtc,
            request.ToUtc,
            request.TimelineBucketSeconds);
        IReadOnlyList<AgentControlEvent> events = reader.ReadEvents(
            request.FromUtc,
            request.ToUtc,
            request.EventLimit);
        IReadOnlyList<AgentDiagnosticsSnapshot> diagnostics = reader.ReadDiagnosticsSnapshots(
            request.FromUtc,
            request.ToUtc,
            request.DiagnosticsLimit);
        AgentSettingsRecord? latestSettings = reader.ReadLatestSettings();
        AgentConsentRecord? latestConsent = reader.ReadLatestConsent();

        if (windows.Count == 0 && snapshots.Count == 0 && events.Count == 0 && diagnostics.Count == 0)
        {
            throw new InvalidOperationException(
                $"No recorded Ari data was found between {request.FromUtc:O} and {request.ToUtc:O}.");
        }

        DateTimeOffset exportedAtUtc = DateTimeOffset.UtcNow;
        string captureId = BuildCaptureId(outputRoot, request.Label, exportedAtUtc);
        string captureDirectoryPath = Path.Combine(outputRoot, captureId);
        Directory.CreateDirectory(captureDirectoryPath);

        DateTimeOffset? observedFromUtc = MinTimestamp(
            windows.Select(static row => (DateTimeOffset?)row.WindowStartUtc),
            snapshots.Select(static row => (DateTimeOffset?)row.TimestampUtc),
            events.Select(static row => (DateTimeOffset?)row.TimestampUtc),
            diagnostics.Select(static row => (DateTimeOffset?)row.TimestampUtc));
        DateTimeOffset? observedToUtc = MaxTimestamp(
            windows.Select(static row => (DateTimeOffset?)row.WindowEndUtc),
            snapshots.Select(static row => (DateTimeOffset?)row.TimestampUtc),
            events.Select(static row => (DateTimeOffset?)row.TimestampUtc),
            diagnostics.Select(static row => (DateTimeOffset?)row.TimestampUtc));

        List<string> agentInstanceIds = windows
            .Select(static row => NormalizeNonEmpty(row.AgentInstanceId))
            .Where(static value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> sessionIds = windows
            .Select(static row => NormalizeNonEmpty(row.SessionId))
            .Where(static value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Dictionary<string, int> eventActionCounts = events
            .GroupBy(static row => string.IsNullOrWhiteSpace(row.Action) ? "unknown" : row.Action.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        AgentBrainSnapshot? latestSnapshot = snapshots.LastOrDefault();
        var summary = new AgentRecordedCaptureSummary(
            CaptureId: captureId,
            Label: BuildLabel(request.Label),
            ExportedAtUtc: exportedAtUtc,
            RequestedFromUtc: request.FromUtc,
            RequestedToUtc: request.ToUtc,
            ObservedFromUtc: observedFromUtc,
            ObservedToUtc: observedToUtc,
            TimelineBucketSeconds: Math.Clamp(request.TimelineBucketSeconds, 1, 3600),
            InputWindowCount: windows.Count,
            SnapshotCount: snapshots.Count,
            EventCount: events.Count,
            DiagnosticsSnapshotCount: diagnostics.Count,
            AgentInstanceIds: agentInstanceIds,
            SessionIds: sessionIds,
            EventActionCounts: eventActionCounts,
            LatestDominantState: latestSnapshot?.DominantStateName,
            LatestAction: latestSnapshot?.LastAction,
            HasInterventionEvents: events.Count > 0);

        WriteJsonFile(Path.Combine(captureDirectoryPath, "capture-metadata.json"), new
        {
            schema = CaptureSchema,
            capture_id = captureId,
            exported_at_utc = exportedAtUtc,
            label = summary.Label,
            notes_present = !string.IsNullOrWhiteSpace(request.Notes),
            request = new
            {
                from_utc = request.FromUtc,
                to_utc = request.ToUtc,
                timeline_bucket_seconds = Math.Clamp(request.TimelineBucketSeconds, 1, 3600),
                input_window_limit = Math.Clamp(request.InputWindowLimit, 1, 200_000),
                event_limit = Math.Clamp(request.EventLimit, 1, 10_000),
                diagnostics_limit = Math.Clamp(request.DiagnosticsLimit, 1, 10_000)
            },
            source = new
            {
                db_path = _dbPath
            }
        });
        WriteJsonFile(Path.Combine(captureDirectoryPath, "capture-summary.json"), summary);
        WriteJsonFile(Path.Combine(captureDirectoryPath, "input-signal-windows.json"), windows);
        WriteJsonFile(Path.Combine(captureDirectoryPath, "timeline-snapshots.json"), snapshots);
        WriteJsonFile(Path.Combine(captureDirectoryPath, "events.json"), events);
        WriteJsonFile(Path.Combine(captureDirectoryPath, "diagnostics-snapshots.json"), diagnostics);
        WriteJsonFile(Path.Combine(captureDirectoryPath, "settings-record.json"), latestSettings);
        WriteJsonFile(Path.Combine(captureDirectoryPath, "consent-record.json"), latestConsent);

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            File.WriteAllText(Path.Combine(captureDirectoryPath, "session-notes.txt"), request.Notes.Trim() + Environment.NewLine);
        }

        var inventoryItem = new AgentRecordedCaptureInventoryItem(
            CaptureId: captureId,
            Label: summary.Label,
            CaptureDirectoryName: Path.GetFileName(captureDirectoryPath),
            CaptureDirectoryPath: captureDirectoryPath,
            ExportedAtUtc: exportedAtUtc,
            RequestedFromUtc: request.FromUtc,
            RequestedToUtc: request.ToUtc,
            InputWindowCount: summary.InputWindowCount,
            SnapshotCount: summary.SnapshotCount,
            EventCount: summary.EventCount,
            DiagnosticsSnapshotCount: summary.DiagnosticsSnapshotCount,
            HasInterventionEvents: summary.HasInterventionEvents,
            AgentInstanceIds: summary.AgentInstanceIds,
            SessionIds: summary.SessionIds,
            EventActionCounts: summary.EventActionCounts);
        string inventoryPath = UpdateInventory(outputRoot, inventoryItem, exportedAtUtc);

        return new AgentRecordedCaptureExportResult(
            CaptureId: captureId,
            CaptureDirectoryPath: captureDirectoryPath,
            InventoryPath: inventoryPath,
            Summary: summary);
    }

    public IReadOnlyList<AgentRecordedCaptureInventoryItem> ReadInventory(string? outputRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(outputRoot)
            ? GetDefaultOutputRoot()
            : outputRoot.Trim();
        string inventoryPath = Path.Combine(root, "capture-inventory.json");
        if (!File.Exists(inventoryPath))
        {
            return [];
        }

        try
        {
            InventoryEnvelope? envelope = JsonSerializer.Deserialize<InventoryEnvelope>(File.ReadAllText(inventoryPath));
            return envelope?.Captures ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string GetDefaultOutputRoot()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "video-captures");
    }

    private static void WriteJsonFile<T>(string path, T payload)
    {
        string raw = JsonSerializer.Serialize(payload, JsonWriteOptions);
        File.WriteAllText(path, raw);
    }

    private static string UpdateInventory(
        string outputRoot,
        AgentRecordedCaptureInventoryItem inventoryItem,
        DateTimeOffset exportedAtUtc)
    {
        string inventoryPath = Path.Combine(outputRoot, "capture-inventory.json");
        List<AgentRecordedCaptureInventoryItem> captures = LoadInventoryItems(inventoryPath)
            .Where(item => !string.Equals(item.CaptureId, inventoryItem.CaptureId, StringComparison.OrdinalIgnoreCase))
            .Append(inventoryItem)
            .OrderByDescending(static item => item.ExportedAtUtc)
            .ThenBy(static item => item.CaptureId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WriteJsonFile(inventoryPath, new InventoryEnvelope(
            Schema: InventorySchema,
            UpdatedAtUtc: exportedAtUtc,
            Captures: captures));

        return inventoryPath;
    }

    private static IReadOnlyList<AgentRecordedCaptureInventoryItem> LoadInventoryItems(string inventoryPath)
    {
        if (!File.Exists(inventoryPath))
        {
            return [];
        }

        try
        {
            InventoryEnvelope? envelope = JsonSerializer.Deserialize<InventoryEnvelope>(File.ReadAllText(inventoryPath));
            return envelope?.Captures ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildCaptureId(string outputRoot, string? label, DateTimeOffset exportedAtUtc)
    {
        string labelSlug = SanitizeSlug(label);
        string baseName = $"capture-{exportedAtUtc:yyyyMMdd-HHmmss}-{labelSlug}";
        string candidate = baseName;
        int suffix = 2;
        while (Directory.Exists(Path.Combine(outputRoot, candidate)))
        {
            candidate = $"{baseName}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string BuildLabel(string? label)
    {
        string trimmed = label?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed) ? "session" : trimmed;
    }

    private static string SanitizeSlug(string? label)
    {
        string trimmed = label?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "session";
        }

        Span<char> buffer = stackalloc char[trimmed.Length];
        int count = 0;
        bool lastWasDash = false;
        foreach (char ch in trimmed)
        {
            char lowered = char.ToLowerInvariant(ch);
            if ((lowered >= 'a' && lowered <= 'z') || (lowered >= '0' && lowered <= '9'))
            {
                buffer[count++] = lowered;
                lastWasDash = false;
                continue;
            }

            if (lastWasDash)
            {
                continue;
            }

            buffer[count++] = '-';
            lastWasDash = true;
        }

        string slug = new string(buffer[..count]).Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "session" : slug;
    }

    private static string? NormalizeNonEmpty(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static DateTimeOffset? MinTimestamp(params IEnumerable<DateTimeOffset?>[] sources)
    {
        List<DateTimeOffset> values = sources
            .SelectMany(static source => source)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Min();
    }

    private static DateTimeOffset? MaxTimestamp(params IEnumerable<DateTimeOffset?>[] sources)
    {
        List<DateTimeOffset> values = sources
            .SelectMany(static source => source)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Max();
    }

    private sealed record InventoryEnvelope(
        string Schema,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<AgentRecordedCaptureInventoryItem> Captures);

    private sealed class RecordedCaptureReadOnlyReader
    {
        private readonly string _connectionString;

        public RecordedCaptureReadOnlyReader(string dbPath)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            };
            _connectionString = builder.ToString();
        }

        public IReadOnlyList<AgentInputSignalWindow> ReadInputSignalWindows(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            int limit)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int boundedLimit = Math.Clamp(limit, 1, 200_000);
            var rows = new List<AgentInputSignalWindow>(Math.Min(boundedLimit, 4096));

            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    accepted_at_utc,
                    schema_ver,
                    agent_instance_id,
                    session_id,
                    window_start_utc,
                    window_end_utc,
                    keyboard_keydown_count,
                    keyboard_backspace_count,
                    mouse_wheel_delta,
                    mouse_move_dx,
                    mouse_move_dy,
                    focus_app_hash,
                    idle_seconds,
                    last_input_event_utc,
                    keyboard_iki_mean_ms,
                    keyboard_iki_std_ms,
                    keyboard_iki_cv,
                    keyboard_burst_count,
                    keyboard_burst_rate,
                    keyboard_correction_spike_count,
                    keyboard_correction_spike_rate,
                    mouse_move_sample_count,
                    mouse_path_length_px,
                    mouse_speed_mean_px_per_sec,
                    mouse_jerk_p95,
                    mouse_micro_correction_count,
                    mouse_micro_correction_rate,
                    mouse_wheel_event_count,
                    mouse_scroll_burst_count,
                    mouse_scroll_burst_rate,
                    mouse_scroll_speed_mean,
                    focus_observation_count,
                    focus_switch_count,
                    focus_active_app_count,
                    focus_app_entropy,
                    idle_observation_count,
                    idle_seconds_within_window,
                    micro_idle_count,
                    micro_idle_seconds,
                    break_seconds_within_window,
                    short_idle_boundary_observed,
                    notification_toasts_enabled
                FROM input_signal_windows
                WHERE window_end_utc >= $from AND window_start_utc <= $to
                ORDER BY window_start_utc ASC, id ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string focusAppHash = ReadStringOrFallback(reader, 11, string.Empty);
                rows.Add(new AgentInputSignalWindow(
                    AcceptedAtUtc: DateTimeOffset.Parse(reader.GetString(0)),
                    SchemaVer: reader.GetInt32(1),
                    AgentInstanceId: reader.GetString(2),
                    SessionId: reader.GetString(3),
                    WindowStartUtc: DateTimeOffset.Parse(reader.GetString(4)),
                    WindowEndUtc: DateTimeOffset.Parse(reader.GetString(5)),
                    KeyboardKeydownCount: reader.GetInt32(6),
                    KeyboardBackspaceCount: reader.GetInt32(7),
                    MouseWheelDelta: reader.GetInt32(8),
                    MouseMoveDx: reader.GetDouble(9),
                    MouseMoveDy: reader.GetDouble(10),
                    FocusAppHash: string.IsNullOrWhiteSpace(focusAppHash) ? null : focusAppHash,
                    IdleSeconds: reader.GetDouble(12),
                    LastInputEventUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 13, string.Empty)),
                    KeyboardIkiMeanMs: ReadNullableDouble(reader, 14),
                    KeyboardIkiStdMs: ReadNullableDouble(reader, 15),
                    KeyboardIkiCv: ReadNullableDouble(reader, 16),
                    KeyboardBurstCount: reader.GetInt32(17),
                    KeyboardBurstRate: ReadNullableDouble(reader, 18),
                    KeyboardCorrectionSpikeCount: reader.GetInt32(19),
                    KeyboardCorrectionSpikeRate: ReadNullableDouble(reader, 20),
                    MouseMoveSampleCount: reader.GetInt32(21),
                    MousePathLengthPx: ReadNullableDouble(reader, 22),
                    MouseSpeedMeanPxPerSec: ReadNullableDouble(reader, 23),
                    MouseJerkP95: ReadNullableDouble(reader, 24),
                    MouseMicroCorrectionCount: reader.GetInt32(25),
                    MouseMicroCorrectionRate: ReadNullableDouble(reader, 26),
                    MouseWheelEventCount: reader.GetInt32(27),
                    MouseScrollBurstCount: reader.GetInt32(28),
                    MouseScrollBurstRate: ReadNullableDouble(reader, 29),
                    MouseScrollSpeedMean: ReadNullableDouble(reader, 30),
                    FocusObservationCount: reader.GetInt32(31),
                    FocusSwitchCount: reader.GetInt32(32),
                    FocusActiveAppCount: reader.GetInt32(33),
                    FocusAppEntropy: ReadNullableDouble(reader, 34),
                    IdleObservationCount: reader.GetInt32(35),
                    IdleSecondsWithinWindow: ReadNullableDouble(reader, 36),
                    MicroIdleCount: reader.GetInt32(37),
                    MicroIdleSeconds: ReadNullableDouble(reader, 38),
                    BreakSecondsWithinWindow: ReadNullableDouble(reader, 39),
                    ShortIdleBoundaryObserved: ReadBoolOrFallback(reader, 40, false),
                    NotificationToastsEnabled: reader.IsDBNull(41) ? null : reader.GetInt32(41) == 1));
            }

            return rows;
        }

        public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            int bucketSeconds)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int bucket = Math.Clamp(bucketSeconds, 1, 3600);
            var rawRows = new List<AgentBrainSnapshot>();

            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    b.timestamp_utc,
                    b.typing_burst_intensity,
                    b.scroll_velocity_index,
                    b.focus_switch_rate,
                    b.smoothed_typing_burst_intensity,
                    b.smoothed_scroll_velocity_index,
                    b.smoothed_focus_switch_rate,
                    b.focus_strength,
                    b.overload_likelihood,
                    b.fatigue_probability,
                    b.cognitive_stability,
                    b.dominant_state_name,
                    b.dominant_state_value,
                    b.confidence,
                    b.last_action,
                    b.activation_window_triggered,
                    b.cooldown_triggered,
                    b.activation_window_open,
                    b.contributors_json,
                    b.policy_id,
                    b.domain,
                    b.escalation_level,
                    b.planned_end_timestamp_utc,
                    b.actual_end_timestamp_utc,
                    b.cooldown_end_timestamp_utc,
                    sb.sustained_attention_duration_sec,
                    sb.idle_fragmentation_index,
                    sb.state_flags_json,
                    sb.contributors_json
                FROM brain_snapshots b
                LEFT JOIN state_buckets sb ON sb.timestamp_utc = b.timestamp_utc
                WHERE b.timestamp_utc >= $from AND b.timestamp_utc <= $to
                ORDER BY b.timestamp_utc ASC;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rawRows.Add(new AgentBrainSnapshot(
                    TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                    TypingBurstIntensity: reader.GetDouble(1),
                    ScrollVelocityIndex: reader.GetDouble(2),
                    FocusSwitchRate: reader.GetDouble(3),
                    SmoothedTypingBurstIntensity: ReadDoubleOrFallback(reader, 4, reader.GetDouble(1)),
                    SmoothedScrollVelocityIndex: ReadDoubleOrFallback(reader, 5, reader.GetDouble(2)),
                    SmoothedFocusSwitchRate: ReadDoubleOrFallback(reader, 6, reader.GetDouble(3)),
                    FocusStrength: reader.GetDouble(7),
                    OverloadLikelihood: reader.GetDouble(8),
                    FatigueProbability: reader.GetDouble(9),
                    CognitiveStability: reader.GetDouble(10),
                    DominantStateName: ReadStringOrFallback(reader, 11, ResolveDominantStateName(
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9))),
                    DominantStateValue: ReadDoubleOrFallback(reader, 12, Math.Max(
                        reader.GetDouble(7),
                        Math.Max(reader.GetDouble(8), reader.GetDouble(9)))),
                    Confidence: reader.GetDouble(13),
                    LastAction: reader.GetString(14),
                    ActivationWindowTriggered: ReadBoolOrFallback(reader, 15, false),
                    CooldownTriggered: ReadBoolOrFallback(reader, 16, false),
                    ActivationWindowOpen: ReadBoolOrFallback(reader, 17, false),
                    Contributors: DeserializeContributors(reader.GetString(18)),
                    PolicyId: ReadStringOrFallback(reader, 19, "n/a"),
                    Domain: ReadStringOrFallback(reader, 20, "control"),
                    EscalationLevel: ReadStringOrFallback(reader, 21, "Inform"),
                    PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 22, string.Empty)),
                    ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 23, string.Empty)),
                    CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 24, string.Empty)),
                    SustainedAttentionDurationSec: ReadDoubleOrFallback(reader, 25, 0d),
                    IdleFragmentationIndex: ReadDoubleOrFallback(reader, 26, 0d),
                    OverloadActive: ParseOverloadActive(ReadStringOrFallback(reader, 27, "{\"schema_ver\":1}")),
                    StateFlagsJson: ReadStringOrFallback(reader, 27, "{\"schema_ver\":1}"),
                    ContributorsJson: ReadStringOrFallback(reader, 28, "{\"schema_ver\":1}")));
            }

            if (bucket == 1 || rawRows.Count <= 1)
            {
                return rawRows;
            }

            var byBucket = new Dictionary<long, AgentBrainSnapshot>();
            foreach (AgentBrainSnapshot row in rawRows)
            {
                long key = row.TimestampUtc.ToUnixTimeSeconds() / bucket;
                byBucket[key] = row;
            }

            return byBucket
                .OrderBy(static pair => pair.Key)
                .Select(static pair => pair.Value)
                .ToList();
        }

        public IReadOnlyList<AgentControlEvent> ReadEvents(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            int limit)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int boundedLimit = Math.Clamp(limit, 1, 10_000);
            var rows = new List<AgentControlEvent>(Math.Min(boundedLimit, 1024));

            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    id,
                    ts_utc,
                    timestamp_utc,
                    action,
                    trigger_state,
                    trigger_value,
                    confidence,
                    duration_seconds,
                    escalation,
                    reversible,
                    why,
                    contributors_json,
                    intervention_id,
                    policy_id,
                    domain,
                    planned_end_timestamp_utc,
                    actual_end_timestamp_utc,
                    previous_system_state_json,
                    what_changed,
                    how_to_undo,
                    reversal_applied,
                    cooldown_end_timestamp_utc,
                    lifecycle_state,
                    idempotency_key,
                    why_json,
                    state_flags_json,
                    (SELECT command_type FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS command_type,
                    (SELECT execution_state FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS execution_state,
                    (SELECT decided_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS decided_at_utc,
                    (SELECT applied_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS applied_at_utc,
                    (SELECT acked_at_utc FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS acked_at_utc,
                    (SELECT error_code FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS error_code,
                    (SELECT executor_agent_instance_id FROM interventions delivery WHERE delivery.intervention_id = events.intervention_id ORDER BY delivery.id DESC LIMIT 1) AS executor_agent_instance_id
                FROM events
                WHERE ts_utc >= $from AND ts_utc <= $to
                  AND action <> 'none'
                  AND action <> 'monitor_only'
                ORDER BY ts_utc ASC, id ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(ReadControlEvent(reader));
            }

            return rows;
        }

        public IReadOnlyList<AgentDiagnosticsSnapshot> ReadDiagnosticsSnapshots(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            int limit)
        {
            if (toUtc < fromUtc)
            {
                return [];
            }

            int boundedLimit = Math.Clamp(limit, 1, 10_000);
            var rows = new List<AgentDiagnosticsSnapshot>(Math.Min(boundedLimit, 512));

            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    timestamp_utc,
                    backend_connected,
                    consent_active,
                    paused,
                    last_error,
                    policy_version,
                    schema_version,
                    agent_version,
                    last_event_timestamp_utc
                FROM diagnostics_snapshots
                WHERE timestamp_utc >= $from AND timestamp_utc <= $to
                ORDER BY timestamp_utc ASC, id ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$to", toUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", boundedLimit);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AgentDiagnosticsSnapshot(
                    TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                    BackendConnected: reader.GetInt32(1) == 1,
                    ConsentActive: reader.GetInt32(2) == 1,
                    Paused: reader.GetInt32(3) == 1,
                    LastError: NormalizeNullable(ReadStringOrFallback(reader, 4, string.Empty)),
                    PolicyVersion: ReadStringOrFallback(reader, 5, string.Empty),
                    SchemaVersion: ReadStringOrFallback(reader, 6, string.Empty),
                    AgentVersion: ReadStringOrFallback(reader, 7, string.Empty),
                    LastEventTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 8, string.Empty)),
                    LastBackendOkUtc: null));
            }

            return rows;
        }

        public AgentConsentRecord? ReadLatestConsent()
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    timestamp_utc,
                    active,
                    version,
                    disclosure_hash
                FROM consent
                ORDER BY id DESC
                LIMIT 1;
                """;

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AgentConsentRecord(
                TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                Active: reader.GetInt32(1) == 1,
                Version: reader.GetString(2),
                DisclosureHash: reader.GetString(3));
        }

        public AgentSettingsRecord? ReadLatestSettings()
        {
            using var connection = OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                    timestamp_utc,
                    privacy_level,
                    regulation_intensity,
                    disabled_categories_json,
                    notification_reduction_enabled,
                    focus_mode_enabled,
                    animation_reduction_enabled,
                    temporary_app_restriction_enabled,
                    baseline_recalibration_started_utc,
                    baseline_recalibration_until_utc
                FROM settings
                ORDER BY id DESC
                LIMIT 1;
                """;

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AgentSettingsRecord(
                TimestampUtc: DateTimeOffset.Parse(reader.GetString(0)),
                PrivacyLevel: reader.GetString(1),
                RegulationIntensity: reader.GetString(2),
                DisabledCategories: DeserializeStringList(reader.GetString(3)),
                NotificationReductionEnabled: reader.GetInt32(4) == 1,
                FocusModeEnabled: reader.GetInt32(5) == 1,
                AnimationReductionEnabled: reader.GetInt32(6) == 1,
                TemporaryAppRestrictionEnabled: reader.GetInt32(7) == 1,
                BaselineRecalibrationStartedUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 8, string.Empty)),
                BaselineRecalibrationUntilUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 9, string.Empty)));
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private static string? NormalizeNullable(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static AgentControlEvent ReadControlEvent(SqliteDataReader reader)
        {
            return new AgentControlEvent(
                TimestampUtc: ParseEventTsUtc(reader),
                Action: reader.GetString(3),
                TriggerState: reader.GetString(4),
                TriggerValue: reader.GetDouble(5),
                Confidence: reader.GetDouble(6),
                DurationSeconds: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                EscalationLevel: reader.GetString(8),
                Reversible: reader.GetInt32(9) == 1,
                Reason: reader.GetString(10),
                Contributors: DeserializeContributors(reader.GetString(11)),
                InterventionId: ReadStringOrFallback(reader, 12, string.Empty),
                PolicyId: ReadStringOrFallback(reader, 13, "n/a"),
                Domain: ReadStringOrFallback(reader, 14, "control"),
                PlannedEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 15, string.Empty)),
                ActualEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 16, string.Empty)),
                PreviousSystemStateJson: ReadStringOrFallback(reader, 17, "{}"),
                WhatChanged: ReadStringOrFallback(reader, 18, string.Empty),
                HowToUndo: ReadStringOrFallback(reader, 19, string.Empty),
                ReversalApplied: ReadBoolOrFallback(reader, 20, false),
                CooldownEndTimestampUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 21, string.Empty)),
                LifecycleState: ReadStringOrFallback(reader, 22, "activated"),
                EventId: reader.GetInt64(0),
                IdempotencyKey: ReadStringOrFallback(reader, 23, string.Empty),
                WhyJson: ReadStringOrFallback(reader, 24, "{\"schema_ver\":1}"),
                StateFlagsJson: ReadStringOrFallback(reader, 25, "{\"schema_ver\":1}"),
                CommandType: ReadStringOrFallback(reader, 26, string.Empty),
                ExecutionState: ReadStringOrFallback(reader, 27, "decision_only"),
                DecidedAtUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 28, string.Empty)),
                AppliedAtUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 29, string.Empty)),
                AckedAtUtc: ParseOptionalDateTimeOffset(ReadStringOrFallback(reader, 30, string.Empty)),
                ExecutionErrorCode: ReadStringOrFallback(reader, 31, string.Empty),
                ExecutorAgentInstanceId: ReadStringOrFallback(reader, 32, string.Empty));
        }

        private static DateTimeOffset ParseEventTsUtc(SqliteDataReader reader)
        {
            string tsUtcRaw = ReadStringOrFallback(reader, 1, string.Empty);
            if (!string.IsNullOrWhiteSpace(tsUtcRaw))
            {
                return DateTimeOffset.Parse(tsUtcRaw);
            }

            string timestampRaw = ReadStringOrFallback(reader, 2, string.Empty);
            if (!string.IsNullOrWhiteSpace(timestampRaw))
            {
                return DateTimeOffset.Parse(timestampRaw);
            }

            return DateTimeOffset.UnixEpoch;
        }

        private static string ResolveDominantStateName(
            double focusStrength,
            double overloadLikelihood,
            double fatigueProbability)
        {
            double dominant = Math.Max(focusStrength, Math.Max(overloadLikelihood, fatigueProbability));
            if (dominant == focusStrength)
            {
                return "focus";
            }

            if (dominant == overloadLikelihood)
            {
                return "overload";
            }

            return "fatigue";
        }

        private static bool ParseOverloadActive(string stateFlagsJson)
        {
            return TryParseOverloadActiveValue(stateFlagsJson, out bool active) && active;
        }

        private static bool TryParseOverloadActiveValue(string json, out bool overloadActive)
        {
            overloadActive = false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (TryReadBool(root, out bool value))
                {
                    overloadActive = value;
                    return true;
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryReadBoolProperty(root, "value", out bool valueProp))
                    {
                        overloadActive = valueProp;
                        return true;
                    }

                    if (TryReadBoolProperty(root, "overload_active", out bool activeProp))
                    {
                        overloadActive = activeProp;
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryReadBool(JsonElement value, out bool parsed)
        {
            parsed = false;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                parsed = value.GetBoolean();
                return true;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
            {
                parsed = numeric != 0;
                return true;
            }

            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out bool fromString))
            {
                parsed = fromString;
                return true;
            }

            return false;
        }

        private static bool TryReadBoolProperty(JsonElement root, string propertyName, out bool parsed)
        {
            parsed = false;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(propertyName, out JsonElement property))
            {
                return false;
            }

            return TryReadBool(property, out parsed);
        }

        private static List<string> DeserializeContributors(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<string>();
                    foreach (JsonElement element in doc.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            string? value = element.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                items.Add(value);
                            }
                        }
                    }

                    return items;
                }

                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("contributors", out JsonElement contributorsElement)
                    && contributorsElement.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<string>();
                    foreach (JsonElement element in contributorsElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            string? value = element.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                items.Add(value);
                            }
                        }
                    }

                    return items;
                }

                return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            }
            catch
            {
                return [];
            }
        }

        private static List<string> DeserializeStringList(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            }
            catch
            {
                return [];
            }
        }

        private static double ReadDoubleOrFallback(SqliteDataReader reader, int ordinal, double fallback)
        {
            try
            {
                if (!reader.IsDBNull(ordinal))
                {
                    return reader.GetDouble(ordinal);
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal)
        {
            try
            {
                if (!reader.IsDBNull(ordinal))
                {
                    return reader.GetDouble(ordinal);
                }
            }
            catch
            {
            }

            return null;
        }

        private static string ReadStringOrFallback(SqliteDataReader reader, int ordinal, string fallback)
        {
            try
            {
                if (!reader.IsDBNull(ordinal))
                {
                    return reader.GetString(ordinal);
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static bool ReadBoolOrFallback(SqliteDataReader reader, int ordinal, bool fallback)
        {
            try
            {
                if (!reader.IsDBNull(ordinal))
                {
                    return reader.GetInt32(ordinal) == 1;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static DateTimeOffset? ParseOptionalDateTimeOffset(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(raw, out DateTimeOffset parsed))
            {
                return parsed;
            }

            return null;
        }
    }
}
