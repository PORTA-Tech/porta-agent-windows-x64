using System.Text.Json;

namespace Porta.Agent.Core;

public sealed class AgentCuratedVideoTraceBuilder
{
    private const string TraceSchema = "porta.ari.video-trace.v1";
    private const string BeatMapSchema = "porta.ari.video-beat-map.v1";
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    public AgentCuratedVideoTraceResult Curate(AgentCuratedVideoTraceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CaptureDirectories is null || request.CaptureDirectories.Count == 0)
        {
            throw new ArgumentException("At least one raw capture directory is required.", nameof(request));
        }

        int targetDurationSeconds = NormalizeDuration(
            request.TargetDurationSeconds,
            request.MinDurationSeconds,
            request.MaxDurationSeconds);
        int frameRate = Math.Clamp(request.FrameRate, 24, 60);
        string outputRoot = string.IsNullOrWhiteSpace(request.OutputRoot)
            ? GetDefaultOutputRoot()
            : request.OutputRoot.Trim();
        Directory.CreateDirectory(outputRoot);

        List<RawCaptureFolder> captures = request.CaptureDirectories
            .Select(LoadCaptureFolder)
            .ToList();
        if (captures.Count == 0)
        {
            throw new InvalidOperationException("No raw capture folders could be loaded.");
        }

        ChapterDurations chapterDurations = ChapterDurations.FromTotalSeconds(targetDurationSeconds);
        CuratedCandidate candidate = SelectBestCandidate(captures, chapterDurations)
            ?? throw new InvalidOperationException(
                "No meaningful intervention sequence was found in the supplied captures. Phase 2 requires at least one real Ari event.");

        DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
        string traceId = BuildTraceId(outputRoot, request.Label, createdAtUtc);
        string traceDirectoryPath = Path.Combine(outputRoot, traceId);
        Directory.CreateDirectory(traceDirectoryPath);

        IReadOnlyList<AgentVideoTraceChapter> chapters = BuildChapters(candidate, chapterDurations);
        IReadOnlyList<AgentVideoTraceBeat> beats = BuildBeatMap(chapters);
        AgentVideoTrace trace = BuildTrace(
            candidate,
            request.Label,
            traceId,
            createdAtUtc,
            frameRate,
            chapters);

        string tracePath = Path.Combine(traceDirectoryPath, "ari-video-trace.json");
        string beatMapPath = Path.Combine(traceDirectoryPath, "ari-video-beat-map.json");
        string summaryPath = Path.Combine(traceDirectoryPath, "curation-summary.json");

        WriteJson(tracePath, trace);
        WriteJson(beatMapPath, new
        {
            schema = BeatMapSchema,
            trace_id = traceId,
            generated_at_utc = createdAtUtc,
            beats
        });
        WriteJson(summaryPath, new
        {
            trace_id = traceId,
            label = trace.Meta.Label,
            curation_mode = trace.Meta.CurationMode,
            duration_sec = trace.Meta.DurationSec,
            selected_capture = candidate.Capture.CaptureId,
            selected_event = new
            {
                action = candidate.AnchorEvent.Action,
                timestamp_utc = candidate.AnchorEvent.TimestampUtc,
                confidence = candidate.AnchorEvent.Confidence,
                trigger_state = candidate.AnchorEvent.TriggerState,
                trigger_value = candidate.AnchorEvent.TriggerValue
            },
            source_window = new
            {
                from_utc = candidate.StartUtc,
                to_utc = candidate.EndUtc
            },
            selection_score = candidate.Score,
            chapters,
            beats
        });

        return new AgentCuratedVideoTraceResult(
            TraceId: traceId,
            TraceDirectoryPath: traceDirectoryPath,
            TracePath: tracePath,
            BeatMapPath: beatMapPath,
            Trace: trace,
            BeatMap: beats);
    }

    public static string GetDefaultOutputRoot()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "video-traces");
    }

    private static int NormalizeDuration(int targetDurationSeconds, int minDurationSeconds, int maxDurationSeconds)
    {
        int safeMin = Math.Clamp(minDurationSeconds, 20, 120);
        int safeMax = Math.Clamp(Math.Max(safeMin, maxDurationSeconds), safeMin, 120);
        int safeTarget = Math.Clamp(targetDurationSeconds, safeMin, safeMax);
        return safeTarget;
    }

    private static RawCaptureFolder LoadCaptureFolder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Capture directory path cannot be empty.");
        }

        string fullPath = Path.GetFullPath(directoryPath.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Capture directory not found: {fullPath}");
        }

        AgentRecordedCaptureSummary summary = ReadJsonFile<AgentRecordedCaptureSummary>(Path.Combine(fullPath, "capture-summary.json"));
        IReadOnlyList<AgentRecordedSignalSample> signals = LoadSignalSamples(fullPath);
        IReadOnlyList<AgentBrainSnapshot> snapshots = ReadJsonFile<List<AgentBrainSnapshot>>(Path.Combine(fullPath, "timeline-snapshots.json"));
        IReadOnlyList<AgentControlEvent> events = ReadJsonFile<List<AgentControlEvent>>(Path.Combine(fullPath, "events.json"));

        if (signals.Count == 0 || snapshots.Count == 0)
        {
            throw new InvalidOperationException(
                $"Capture '{summary.CaptureId}' does not contain enough source data for curation.");
        }

        return new RawCaptureFolder(
            CaptureId: summary.CaptureId,
            Label: summary.Label,
            DirectoryPath: fullPath,
            ObservedFromUtc: summary.ObservedFromUtc ?? signals.Min(static row => row.StartUtc),
            ObservedToUtc: summary.ObservedToUtc ?? snapshots.Max(static row => row.TimestampUtc),
            Summary: summary,
            Signals: signals.OrderBy(static row => row.StartUtc).ToList(),
            Snapshots: snapshots.OrderBy(static row => row.TimestampUtc).ToList(),
            Events: events.OrderBy(static row => row.TimestampUtc).ToList());
    }

    private static IReadOnlyList<AgentRecordedSignalSample> LoadSignalSamples(string captureDirectoryPath)
    {
        string signalSamplesPath = Path.Combine(captureDirectoryPath, "signal-samples.json");
        if (File.Exists(signalSamplesPath))
        {
            return ReadJsonFile<List<AgentRecordedSignalSample>>(signalSamplesPath);
        }

        string rawWindowsPath = Path.Combine(captureDirectoryPath, "input-signal-windows.json");
        if (!File.Exists(rawWindowsPath))
        {
            throw new FileNotFoundException(
                $"Required capture artifact not found: {signalSamplesPath}",
                signalSamplesPath);
        }

        IReadOnlyList<AgentInputSignalWindow> windows = ReadJsonFile<List<AgentInputSignalWindow>>(rawWindowsPath);
        return windows
            .Select(window => new AgentRecordedSignalSample(
                SignalKind: "input_signal_window",
                StartUtc: window.WindowStartUtc,
                EndUtc: window.WindowEndUtc,
                TimestampUtc: window.AcceptedAtUtc,
                AgentInstanceId: string.IsNullOrWhiteSpace(window.AgentInstanceId) ? null : window.AgentInstanceId,
                SessionId: string.IsNullOrWhiteSpace(window.SessionId) ? null : window.SessionId,
                SchemaVer: window.SchemaVer,
                KeyboardKeydownCount: window.KeyboardKeydownCount,
                KeyboardBackspaceCount: window.KeyboardBackspaceCount,
                KeyboardIkiMeanMs: window.KeyboardIkiMeanMs,
                KeyboardIkiStdMs: window.KeyboardIkiStdMs,
                KeyboardIkiCv: window.KeyboardIkiCv,
                KeyboardBurstCount: window.KeyboardBurstCount,
                KeyboardBurstRate: window.KeyboardBurstRate,
                KeyboardCorrectionSpikeCount: window.KeyboardCorrectionSpikeCount,
                KeyboardCorrectionSpikeRate: window.KeyboardCorrectionSpikeRate,
                MouseWheelDelta: window.MouseWheelDelta,
                MouseWheelEventCount: window.MouseWheelEventCount,
                MouseMoveDx: window.MouseMoveDx,
                MouseMoveDy: window.MouseMoveDy,
                MouseMoveSampleCount: window.MouseMoveSampleCount,
                MousePathLengthPx: window.MousePathLengthPx,
                MouseSpeedMeanPxPerSec: window.MouseSpeedMeanPxPerSec,
                MouseJerkP95: window.MouseJerkP95,
                MouseMicroCorrectionCount: window.MouseMicroCorrectionCount,
                MouseMicroCorrectionRate: window.MouseMicroCorrectionRate,
                MouseScrollBurstCount: window.MouseScrollBurstCount,
                MouseScrollBurstRate: window.MouseScrollBurstRate,
                MouseScrollSpeedMean: window.MouseScrollSpeedMean,
                FocusObservationCount: window.FocusObservationCount,
                FocusSwitchCount: window.FocusSwitchCount,
                FocusActiveAppCount: window.FocusActiveAppCount,
                FocusAppEntropy: window.FocusAppEntropy,
                FocusAppHash: string.IsNullOrWhiteSpace(window.FocusAppHash) ? null : window.FocusAppHash,
                IdleSeconds: window.IdleSeconds,
                IdleObservationCount: window.IdleObservationCount,
                IdleSecondsWithinWindow: window.IdleSecondsWithinWindow,
                MicroIdleCount: window.MicroIdleCount,
                MicroIdleSeconds: window.MicroIdleSeconds,
                TrueBreakSeconds: window.BreakSecondsWithinWindow,
                TrueBreakFlag: window.BreakSecondsWithinWindow.HasValue
                    ? window.BreakSecondsWithinWindow.Value >= 120d
                    : null,
                ShortIdleBoundaryObserved: window.ShortIdleBoundaryObserved,
                NotificationToastsEnabled: window.NotificationToastsEnabled))
            .ToList();
    }

    private static T ReadJsonFile<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required capture artifact not found: {path}", path);
        }

        T? payload = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        return payload ?? throw new InvalidOperationException($"Invalid JSON content in {path}.");
    }

    private static CuratedCandidate? SelectBestCandidate(
        IReadOnlyList<RawCaptureFolder> captures,
        ChapterDurations chapterDurations)
    {
        CuratedCandidate? best = null;
        foreach (RawCaptureFolder capture in captures)
        {
            foreach (AgentControlEvent controlEvent in capture.Events)
            {
                CuratedCandidate candidate = BuildCandidate(capture, controlEvent, chapterDurations);
                if (best is null || candidate.Score > best.Score)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static CuratedCandidate BuildCandidate(
        RawCaptureFolder capture,
        AgentControlEvent anchorEvent,
        ChapterDurations chapterDurations)
    {
        double anchorLeadSec = Math.Min(6d, chapterDurations.RegulateSec * 0.45d);
        TimeSpan originalLead = TimeSpan.FromSeconds(chapterDurations.ObserveSec + chapterDurations.InferSec + anchorLeadSec);
        TimeSpan totalDuration = TimeSpan.FromSeconds(chapterDurations.TotalSec);
        DateTimeOffset originalStart = anchorEvent.TimestampUtc - originalLead;
        DateTimeOffset originalEnd = originalStart + totalDuration;

        DateTimeOffset startUtc = originalStart;
        DateTimeOffset endUtc = originalEnd;
        double coveragePenaltySec = 0d;

        if (startUtc < capture.ObservedFromUtc)
        {
            coveragePenaltySec += (capture.ObservedFromUtc - startUtc).TotalSeconds;
            startUtc = capture.ObservedFromUtc;
            endUtc = startUtc + totalDuration;
        }

        if (endUtc > capture.ObservedToUtc)
        {
            coveragePenaltySec += (endUtc - capture.ObservedToUtc).TotalSeconds;
            endUtc = capture.ObservedToUtc;
            startUtc = endUtc - totalDuration;
        }

        if (startUtc < capture.ObservedFromUtc)
        {
            startUtc = capture.ObservedFromUtc;
        }

        if (endUtc <= startUtc)
        {
            endUtc = startUtc;
        }

        double actualDurationSec = Math.Max(0d, (endUtc - startUtc).TotalSeconds);
        if (actualDurationSec < 20d)
        {
            return new CuratedCandidate(
                Capture: capture,
                AnchorEvent: anchorEvent,
                StartUtc: startUtc,
                EndUtc: endUtc,
                Score: double.MinValue,
                SelectionReason: "insufficient_duration");
        }

        List<AgentBrainSnapshot> snapshots = capture.Snapshots
            .Where(row => row.TimestampUtc >= startUtc && row.TimestampUtc <= endUtc)
            .ToList();
        List<AgentRecordedSignalSample> signals = capture.Signals
            .Where(row => row.EndUtc >= startUtc && row.StartUtc <= endUtc)
            .ToList();
        List<AgentControlEvent> events = capture.Events
            .Where(row => row.TimestampUtc >= startUtc && row.TimestampUtc <= endUtc)
            .ToList();

        double actionWeight = ResolveActionWeight(anchorEvent.Action);
        double snapshotMotion = ComputeSnapshotMotionScore(snapshots);
        double signalActivity = ComputeSignalActivityScore(signals);
        double eventDensity = events.Count;
        double score =
            (actionWeight * 100d)
            + (anchorEvent.Confidence * 25d)
            + snapshotMotion
            + signalActivity
            + (eventDensity * 3d)
            - (coveragePenaltySec * 2d);

        string selectionReason =
            $"anchor_action={anchorEvent.Action}; confidence={anchorEvent.Confidence:F2}; motion={snapshotMotion:F2}; activity={signalActivity:F2}";

        return new CuratedCandidate(
            Capture: capture,
            AnchorEvent: anchorEvent,
            StartUtc: startUtc,
            EndUtc: endUtc,
            Score: score,
            SelectionReason: selectionReason);
    }

    private static AgentVideoTrace BuildTrace(
        CuratedCandidate candidate,
        string? label,
        string traceId,
        DateTimeOffset createdAtUtc,
        int frameRate,
        IReadOnlyList<AgentVideoTraceChapter> chapters)
    {
        double durationSec = RoundSeconds((candidate.EndUtc - candidate.StartUtc).TotalSeconds);
        List<AgentVideoTraceSignalSample> signals = candidate.Capture.Signals
            .Where(row => row.EndUtc >= candidate.StartUtc && row.StartUtc <= candidate.EndUtc)
            .Select(row => new AgentVideoTraceSignalSample(
                SignalKind: row.SignalKind,
                TStartSec: RoundSeconds(Math.Max(0d, (row.StartUtc - candidate.StartUtc).TotalSeconds)),
                TEndSec: RoundSeconds(Math.Min(durationSec, (row.EndUtc - candidate.StartUtc).TotalSeconds)),
                SourceCaptureId: candidate.Capture.CaptureId,
                SourceStartUtc: row.StartUtc,
                SourceEndUtc: row.EndUtc,
                TypingBurstIntensity: row.TypingBurstIntensity,
                ScrollVelocityIndex: row.ScrollVelocityIndex,
                FocusSwitchRate: row.FocusSwitchRate,
                KeyboardKeydownCount: row.KeyboardKeydownCount,
                KeyboardBackspaceCount: row.KeyboardBackspaceCount,
                KeyboardBackspaceRate: row.KeyboardBackspaceRate,
                KeyboardIkiMeanMs: row.KeyboardIkiMeanMs,
                KeyboardIkiStdMs: row.KeyboardIkiStdMs,
                KeyboardIkiCv: row.KeyboardIkiCv,
                KeyboardBurstCount: row.KeyboardBurstCount,
                KeyboardBurstRate: row.KeyboardBurstRate,
                KeyboardCorrectionSpikeCount: row.KeyboardCorrectionSpikeCount,
                KeyboardCorrectionSpikeRate: row.KeyboardCorrectionSpikeRate,
                MouseWheelDelta: row.MouseWheelDelta,
                MouseMoveDx: row.MouseMoveDx,
                MouseMoveDy: row.MouseMoveDy,
                MouseWheelEventCount: row.MouseWheelEventCount,
                MouseMoveSampleCount: row.MouseMoveSampleCount,
                MousePathLengthPx: row.MousePathLengthPx,
                MouseSpeedMeanPxPerSec: row.MouseSpeedMeanPxPerSec,
                MouseJerkP95: row.MouseJerkP95,
                MouseMicroCorrectionCount: row.MouseMicroCorrectionCount,
                MouseMicroCorrectionRate: row.MouseMicroCorrectionRate,
                MouseScrollBurstCount: row.MouseScrollBurstCount,
                MouseScrollBurstRate: row.MouseScrollBurstRate,
                MouseScrollSpeedMean: row.MouseScrollSpeedMean,
                FocusObservationCount: row.FocusObservationCount,
                FocusSwitchCount: row.FocusSwitchCount,
                FocusActiveAppCount: row.FocusActiveAppCount,
                FocusAppEntropy: row.FocusAppEntropy,
                FocusAppHash: row.FocusAppHash,
                IdleSeconds: row.IdleSeconds,
                IdleObservationCount: row.IdleObservationCount,
                IdleSecondsWithinWindow: row.IdleSecondsWithinWindow,
                MicroIdleCount: row.MicroIdleCount,
                MicroIdleSeconds: row.MicroIdleSeconds,
                TrueBreakSeconds: row.TrueBreakSeconds,
                TrueBreakFlag: row.TrueBreakFlag,
                ShortIdleBoundaryObserved: row.ShortIdleBoundaryObserved,
                NotificationToastsEnabled: row.NotificationToastsEnabled,
                QualityScore: row.QualityScore,
                Partial: row.Partial,
                ContributorsJson: row.ContributorsJson))
            .Where(static row => row.TEndSec >= row.TStartSec)
            .ToList();

        List<AgentVideoTraceSnapshotSample> snapshots = candidate.Capture.Snapshots
            .Where(row => row.TimestampUtc >= candidate.StartUtc && row.TimestampUtc <= candidate.EndUtc)
            .Select(row => new AgentVideoTraceSnapshotSample(
                TSec: RoundSeconds((row.TimestampUtc - candidate.StartUtc).TotalSeconds),
                SourceCaptureId: candidate.Capture.CaptureId,
                SourceTimestampUtc: row.TimestampUtc,
                FocusStrength: row.FocusStrength,
                OverloadLikelihood: row.OverloadLikelihood,
                FatigueProbability: row.FatigueProbability,
                CognitiveStability: row.CognitiveStability,
                DominantStateValue: row.DominantStateValue,
                DominantStateName: row.DominantStateName,
                Confidence: row.Confidence,
                LastAction: row.LastAction,
                ActivationWindowTriggered: row.ActivationWindowTriggered,
                CooldownTriggered: row.CooldownTriggered,
                ActivationWindowOpen: row.ActivationWindowOpen,
                SustainedAttentionDurationSec: row.SustainedAttentionDurationSec,
                IdleFragmentationIndex: row.IdleFragmentationIndex,
                OverloadActive: row.OverloadActive,
                StateFlagsJson: row.StateFlagsJson,
                Contributors: row.Contributors))
            .ToList();

        List<AgentVideoTraceEventSample> events = candidate.Capture.Events
            .Where(row => row.TimestampUtc >= candidate.StartUtc && row.TimestampUtc <= candidate.EndUtc)
            .Select(row => new AgentVideoTraceEventSample(
                TSec: RoundSeconds((row.TimestampUtc - candidate.StartUtc).TotalSeconds),
                SourceCaptureId: candidate.Capture.CaptureId,
                SourceTimestampUtc: row.TimestampUtc,
                Action: row.Action,
                TriggerState: row.TriggerState,
                TriggerValue: row.TriggerValue,
                Confidence: row.Confidence,
                DurationSeconds: row.DurationSeconds,
                EscalationLevel: row.EscalationLevel,
                Reversible: row.Reversible,
                Reason: row.Reason,
                Contributors: row.Contributors,
                InterventionId: row.InterventionId,
                PolicyId: row.PolicyId,
                Domain: row.Domain,
                WhatChanged: row.WhatChanged,
                HowToUndo: row.HowToUndo,
                LifecycleState: row.LifecycleState,
                CommandType: row.CommandType,
                ExecutionState: row.ExecutionState,
                WhyJson: row.WhyJson,
                StateFlagsJson: row.StateFlagsJson))
            .ToList();

        List<AgentVideoTraceSourceSegment> sourceSegments = chapters
            .Select(chapter => new AgentVideoTraceSourceSegment(
                ChapterId: chapter.ChapterId,
                SourceFromUtc: chapter.SourceFromUtc,
                SourceToUtc: chapter.SourceToUtc,
                TraceStartSec: chapter.StartSec,
                TraceEndSec: chapter.EndSec,
                SelectionReason: candidate.SelectionReason,
                SelectionScore: RoundSeconds(candidate.Score)))
            .ToList();

        var meta = new AgentVideoTraceMeta(
            Schema: TraceSchema,
            TraceId: traceId,
            Label: string.IsNullOrWhiteSpace(label) ? candidate.Capture.Label : label.Trim(),
            CreatedAtUtc: createdAtUtc,
            FrameRate: frameRate,
            DurationSec: durationSec,
            CurationMode: "event-centered-auto",
            StoryArc: "observe -> infer -> regulate -> explain",
            SourceCaptures:
            [
                new AgentVideoTraceSourceCapture(
                    CaptureId: candidate.Capture.CaptureId,
                    Label: candidate.Capture.Label,
                    DirectoryPath: candidate.Capture.DirectoryPath,
                    ObservedFromUtc: candidate.Capture.ObservedFromUtc,
                    ObservedToUtc: candidate.Capture.ObservedToUtc,
                    Segments: sourceSegments)
            ]);

        return new AgentVideoTrace(
            Meta: meta,
            Signals: signals,
            Snapshots: snapshots,
            Events: events,
            Chapters: chapters);
    }

    private static IReadOnlyList<AgentVideoTraceChapter> BuildChapters(
        CuratedCandidate candidate,
        ChapterDurations chapterDurations)
    {
        double totalDurationSec = RoundSeconds((candidate.EndUtc - candidate.StartUtc).TotalSeconds);
        ChapterDurations actualDurations = chapterDurations.Rebalance(totalDurationSec);

        double observeStart = 0d;
        double observeEnd = RoundSeconds(actualDurations.ObserveSec);
        double inferStart = observeEnd;
        double inferEnd = RoundSeconds(inferStart + actualDurations.InferSec);
        double regulateStart = inferEnd;
        double regulateEnd = RoundSeconds(regulateStart + actualDurations.RegulateSec);
        double explainStart = regulateEnd;
        double explainEnd = RoundSeconds(totalDurationSec);

        return
        [
            BuildChapter("observe", "Observe", "observe", observeStart, observeEnd, candidate, "Show the exact behavioral signals Ari records before any inference.", "keyboard timing, scroll, switching, idle"),
            BuildChapter("infer", "Infer", "infer", inferStart, inferEnd, candidate, "Transform recorded interaction into focus, overload, fatigue, stability, and confidence.", "state trajectory and confidence"),
            BuildChapter("regulate", "Regulate", "regulate", regulateStart, regulateEnd, candidate, "Center the real Ari intervention and the moment it triggers.", candidate.AnchorEvent.Action),
            BuildChapter("explain", "Explain", "explain", explainStart, explainEnd, candidate, "Close on why Ari acted, how confident it was, and what changed.", "trigger, confidence, reversibility")
        ];
    }

    private static AgentVideoTraceChapter BuildChapter(
        string chapterId,
        string title,
        string role,
        double startSec,
        double endSec,
        CuratedCandidate candidate,
        string purpose,
        string dataFocus)
    {
        DateTimeOffset sourceFromUtc = candidate.StartUtc.AddSeconds(startSec);
        DateTimeOffset sourceToUtc = candidate.StartUtc.AddSeconds(endSec);
        return new AgentVideoTraceChapter(
            ChapterId: chapterId,
            Title: title,
            Role: role,
            StartSec: RoundSeconds(startSec),
            EndSec: RoundSeconds(endSec),
            SourceCaptureId: candidate.Capture.CaptureId,
            SourceFromUtc: sourceFromUtc,
            SourceToUtc: sourceToUtc,
            Purpose: purpose,
            DataFocus: dataFocus);
    }

    private static IReadOnlyList<AgentVideoTraceBeat> BuildBeatMap(IReadOnlyList<AgentVideoTraceChapter> chapters)
    {
        AgentVideoTraceChapter observe = chapters.Single(static row => row.ChapterId == "observe");
        AgentVideoTraceChapter infer = chapters.Single(static row => row.ChapterId == "infer");
        AgentVideoTraceChapter regulate = chapters.Single(static row => row.ChapterId == "regulate");
        AgentVideoTraceChapter explain = chapters.Single(static row => row.ChapterId == "explain");

        double observeSplit = SplitRange(observe.StartSec, observe.EndSec, 0.65d);
        double inferSplit = SplitRange(infer.StartSec, infer.EndSec, 0.60d);
        double regulateSplit = SplitRange(regulate.StartSec, regulate.EndSec, 0.65d);

        return
        [
            BuildBeat("observe-signals", observe, observe.StartSec, observeSplit, "What Ari Tracks", "Establish the recorded interaction signals.", observe.DataFocus),
            BuildBeat("observe-boundaries", observe, observeSplit, observe.EndSec, "Observation Boundaries", "Clarify that Ari is reading interaction patterns, not content.", "behavioral telemetry only"),
            BuildBeat("infer-state-rise", infer, infer.StartSec, inferSplit, "State Inference", "Show the state trajectory building from the recorded signals.", infer.DataFocus),
            BuildBeat("infer-confidence", infer, inferSplit, infer.EndSec, "Confidence Lock", "Show confidence stabilizing before Ari acts.", "confidence and dominant state"),
            BuildBeat("regulate-trigger", regulate, regulate.StartSec, regulateSplit, "Real Intervention Trigger", "Land the real moment Ari decides to regulate.", regulate.DataFocus),
            BuildBeat("regulate-outcome", regulate, regulateSplit, regulate.EndSec, "System Outcome", "Show the output changing because of the recorded trigger.", "action, what changed, state response"),
            BuildBeat("explain-why", explain, explain.StartSec, explain.EndSec, "Why Ari Acted", "Close with the reason, confidence, and reversibility.", explain.DataFocus)
        ];
    }

    private static AgentVideoTraceBeat BuildBeat(
        string beatId,
        AgentVideoTraceChapter chapter,
        double startSec,
        double endSec,
        string title,
        string purpose,
        string dataFocus)
    {
        return new AgentVideoTraceBeat(
            BeatId: beatId,
            ChapterId: chapter.ChapterId,
            StartSec: RoundSeconds(startSec),
            EndSec: RoundSeconds(endSec),
            Title: title,
            Purpose: purpose,
            DataFocus: dataFocus,
            SourceCaptureId: chapter.SourceCaptureId,
            SourceFromUtc: chapter.SourceFromUtc.AddSeconds(startSec - chapter.StartSec),
            SourceToUtc: chapter.SourceFromUtc.AddSeconds(endSec - chapter.StartSec));
    }

    private static double SplitRange(double startSec, double endSec, double ratio)
    {
        double clampedRatio = Math.Clamp(ratio, 0.1d, 0.9d);
        return RoundSeconds(startSec + ((endSec - startSec) * clampedRatio));
    }

    private static string BuildTraceId(string outputRoot, string? label, DateTimeOffset createdAtUtc)
    {
        string labelSlug = SanitizeSlug(label);
        string baseName = $"trace-{createdAtUtc:yyyyMMdd-HHmmss}-{labelSlug}";
        string candidate = baseName;
        int suffix = 2;
        while (Directory.Exists(Path.Combine(outputRoot, candidate)))
        {
            candidate = $"{baseName}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeSlug(string? label)
    {
        string trimmed = label?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "video";
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
        return string.IsNullOrWhiteSpace(slug) ? "video" : slug;
    }

    private static void WriteJson<T>(string path, T payload)
    {
        string raw = JsonSerializer.Serialize(payload, JsonWriteOptions);
        File.WriteAllText(path, raw);
    }

    private static double ResolveActionWeight(string action)
    {
        string normalized = string.IsNullOrWhiteSpace(action)
            ? string.Empty
            : action.Trim().ToLowerInvariant();
        return normalized switch
        {
            "deep_work_notifications_suppressed" => 6.0d,
            "overload_brightness_reduced" => 6.0d,
            "break_suggestion" => 5.0d,
            "focus_lock_in_suggestion" => 5.0d,
            "stretch_break_suggestion" => 4.5d,
            "deep_work_notifications_restored" => 4.0d,
            "overload_brightness_restored" => 4.0d,
            _ when normalized.EndsWith("_failed", StringComparison.Ordinal) => 2.0d,
            _ => 3.0d
        };
    }

    private static double ComputeSnapshotMotionScore(IReadOnlyList<AgentBrainSnapshot> snapshots)
    {
        if (snapshots.Count <= 1)
        {
            return 0d;
        }

        double focusRange = snapshots.Max(static row => row.FocusStrength) - snapshots.Min(static row => row.FocusStrength);
        double overloadRange = snapshots.Max(static row => row.OverloadLikelihood) - snapshots.Min(static row => row.OverloadLikelihood);
        double fatigueRange = snapshots.Max(static row => row.FatigueProbability) - snapshots.Min(static row => row.FatigueProbability);
        double stabilityRange = snapshots.Max(static row => row.CognitiveStability) - snapshots.Min(static row => row.CognitiveStability);
        double confidenceRange = snapshots.Max(static row => row.Confidence) - snapshots.Min(static row => row.Confidence);
        return (focusRange + overloadRange + fatigueRange + stabilityRange + confidenceRange) * 25d;
    }

    private static double ComputeSignalActivityScore(IReadOnlyList<AgentRecordedSignalSample> signals)
    {
        if (signals.Count == 0)
        {
            return 0d;
        }

        double keyboardDensity = signals
            .Where(static row => row.KeyboardKeydownCount.HasValue)
            .Sum(static row => row.KeyboardKeydownCount!.Value * 0.4d);
        double typingIntensity = signals
            .Where(static row => row.TypingBurstIntensity.HasValue)
            .Sum(static row => row.TypingBurstIntensity!.Value * 12d);
        double keyboardBurst = signals
            .Where(static row => row.KeyboardBurstRate.HasValue)
            .Sum(static row => row.KeyboardBurstRate!.Value * 20d);
        double switchDensity = signals
            .Where(static row => row.FocusSwitchCount.HasValue)
            .Sum(static row => row.FocusSwitchCount!.Value * 6d);
        double switchRate = signals
            .Where(static row => row.FocusSwitchRate.HasValue)
            .Sum(static row => row.FocusSwitchRate!.Value * 12d);
        double scrollDensity = signals
            .Where(static row => row.MouseScrollSpeedMean.HasValue)
            .Sum(static row => Math.Abs(row.MouseScrollSpeedMean!.Value) / 120d);
        double scrollVelocity = signals
            .Where(static row => row.ScrollVelocityIndex.HasValue)
            .Sum(static row => row.ScrollVelocityIndex!.Value * 10d);
        double idleVariation = signals
            .Where(static row => row.IdleSecondsWithinWindow.HasValue)
            .Sum(static row => row.IdleSecondsWithinWindow!.Value);
        double ambientIdle = signals
            .Where(static row => row.IdleSeconds.HasValue && !row.IdleSecondsWithinWindow.HasValue)
            .Sum(static row => row.IdleSeconds!.Value * 0.5d);
        return keyboardDensity + typingIntensity + keyboardBurst + switchDensity + switchRate + scrollDensity + scrollVelocity + idleVariation + ambientIdle;
    }

    private static double RoundSeconds(double value)
    {
        return Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }

    private sealed record RawCaptureFolder(
        string CaptureId,
        string Label,
        string DirectoryPath,
        DateTimeOffset ObservedFromUtc,
        DateTimeOffset ObservedToUtc,
        AgentRecordedCaptureSummary Summary,
        IReadOnlyList<AgentRecordedSignalSample> Signals,
        IReadOnlyList<AgentBrainSnapshot> Snapshots,
        IReadOnlyList<AgentControlEvent> Events);

    private sealed record CuratedCandidate(
        RawCaptureFolder Capture,
        AgentControlEvent AnchorEvent,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        double Score,
        string SelectionReason);

    private sealed record ChapterDurations(
        double ObserveSec,
        double InferSec,
        double RegulateSec,
        double ExplainSec)
    {
        public double TotalSec => ObserveSec + InferSec + RegulateSec + ExplainSec;

        public static ChapterDurations FromTotalSeconds(double totalSec)
        {
            double safeTotal = Math.Max(20d, totalSec);
            double observe = safeTotal * 0.22d;
            double infer = safeTotal * 0.26d;
            double regulate = safeTotal * 0.32d;
            double explain = Math.Max(4d, safeTotal - observe - infer - regulate);
            return new ChapterDurations(
                ObserveSec: RoundSeconds(observe),
                InferSec: RoundSeconds(infer),
                RegulateSec: RoundSeconds(regulate),
                ExplainSec: RoundSeconds(explain));
        }

        public ChapterDurations Rebalance(double actualTotalSec)
        {
            if (actualTotalSec <= 0d)
            {
                return this;
            }

            double scale = actualTotalSec / TotalSec;
            double observe = RoundSeconds(ObserveSec * scale);
            double infer = RoundSeconds(InferSec * scale);
            double regulate = RoundSeconds(RegulateSec * scale);
            double explain = RoundSeconds(Math.Max(0d, actualTotalSec - observe - infer - regulate));
            return new ChapterDurations(observe, infer, regulate, explain);
        }
    }
}
