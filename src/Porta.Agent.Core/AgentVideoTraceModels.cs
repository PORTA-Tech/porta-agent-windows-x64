namespace Porta.Agent.Core;

public sealed record AgentCuratedVideoTraceRequest(
    IReadOnlyList<string> CaptureDirectories,
    string? Label = null,
    string? OutputRoot = null,
    int TargetDurationSeconds = 45,
    int MinDurationSeconds = 30,
    int MaxDurationSeconds = 60,
    int FrameRate = 30);

public sealed record AgentCuratedVideoTraceResult(
    string TraceId,
    string TraceDirectoryPath,
    string TracePath,
    string BeatMapPath,
    AgentVideoTrace Trace,
    IReadOnlyList<AgentVideoTraceBeat> BeatMap);

public sealed record AgentVideoTrace(
    AgentVideoTraceMeta Meta,
    IReadOnlyList<AgentVideoTraceSignalSample> Signals,
    IReadOnlyList<AgentVideoTraceSnapshotSample> Snapshots,
    IReadOnlyList<AgentVideoTraceEventSample> Events,
    IReadOnlyList<AgentVideoTraceChapter>? Chapters = null);

public sealed record AgentVideoTraceMeta(
    string Schema,
    string TraceId,
    string Label,
    DateTimeOffset CreatedAtUtc,
    int FrameRate,
    double DurationSec,
    string CurationMode,
    string StoryArc,
    IReadOnlyList<AgentVideoTraceSourceCapture> SourceCaptures);

public sealed record AgentVideoTraceSourceCapture(
    string CaptureId,
    string Label,
    string DirectoryPath,
    DateTimeOffset? ObservedFromUtc,
    DateTimeOffset? ObservedToUtc,
    IReadOnlyList<AgentVideoTraceSourceSegment> Segments);

public sealed record AgentVideoTraceSourceSegment(
    string ChapterId,
    DateTimeOffset SourceFromUtc,
    DateTimeOffset SourceToUtc,
    double TraceStartSec,
    double TraceEndSec,
    string SelectionReason,
    double SelectionScore);

public sealed record AgentVideoTraceSignalSample(
    string SignalKind,
    double TStartSec,
    double TEndSec,
    string SourceCaptureId,
    DateTimeOffset SourceStartUtc,
    DateTimeOffset SourceEndUtc,
    double? TypingBurstIntensity,
    double? ScrollVelocityIndex,
    double? FocusSwitchRate,
    int? KeyboardKeydownCount,
    int? KeyboardBackspaceCount,
    double? KeyboardBackspaceRate,
    double? KeyboardIkiMeanMs,
    double? KeyboardIkiStdMs,
    double? KeyboardIkiCv,
    int? KeyboardBurstCount,
    double? KeyboardBurstRate,
    int? KeyboardCorrectionSpikeCount,
    double? KeyboardCorrectionSpikeRate,
    int? MouseWheelDelta,
    int? MouseWheelEventCount,
    double? MouseMoveDx,
    double? MouseMoveDy,
    int? MouseMoveSampleCount,
    double? MousePathLengthPx,
    double? MouseSpeedMeanPxPerSec,
    double? MouseJerkP95,
    int? MouseMicroCorrectionCount,
    double? MouseMicroCorrectionRate,
    int? MouseScrollBurstCount,
    double? MouseScrollBurstRate,
    double? MouseScrollSpeedMean,
    int? FocusObservationCount,
    int? FocusSwitchCount,
    double? FocusActiveAppCount,
    double? FocusAppEntropy,
    string? FocusAppHash,
    double? IdleSeconds,
    int? IdleObservationCount,
    double? IdleSecondsWithinWindow,
    double? MicroIdleCount,
    double? MicroIdleSeconds,
    double? TrueBreakSeconds,
    bool? TrueBreakFlag,
    bool? ShortIdleBoundaryObserved,
    bool? NotificationToastsEnabled,
    double? QualityScore,
    bool? Partial,
    string? ContributorsJson);

public sealed record AgentVideoTraceSnapshotSample(
    double TSec,
    string SourceCaptureId,
    DateTimeOffset SourceTimestampUtc,
    double FocusStrength,
    double OverloadLikelihood,
    double FatigueProbability,
    double CognitiveStability,
    double DominantStateValue,
    string DominantStateName,
    double Confidence,
    string LastAction,
    bool ActivationWindowTriggered,
    bool CooldownTriggered,
    bool ActivationWindowOpen,
    double SustainedAttentionDurationSec,
    double IdleFragmentationIndex,
    bool OverloadActive,
    string StateFlagsJson,
    IReadOnlyList<string> Contributors);

public sealed record AgentVideoTraceEventSample(
    double TSec,
    string SourceCaptureId,
    DateTimeOffset SourceTimestampUtc,
    string Action,
    string TriggerState,
    double TriggerValue,
    double Confidence,
    int? DurationSeconds,
    string EscalationLevel,
    bool Reversible,
    string Reason,
    IReadOnlyList<string> Contributors,
    string InterventionId,
    string PolicyId,
    string Domain,
    string WhatChanged,
    string HowToUndo,
    string LifecycleState,
    string CommandType,
    string ExecutionState,
    string WhyJson,
    string StateFlagsJson);

public sealed record AgentVideoTraceChapter(
    string ChapterId,
    string Title,
    string Role,
    double StartSec,
    double EndSec,
    string SourceCaptureId,
    DateTimeOffset SourceFromUtc,
    DateTimeOffset SourceToUtc,
    string Purpose,
    string DataFocus);

public sealed record AgentVideoTraceBeat(
    string BeatId,
    string ChapterId,
    double StartSec,
    double EndSec,
    string Title,
    string Purpose,
    string DataFocus,
    string SourceCaptureId,
    DateTimeOffset SourceFromUtc,
    DateTimeOffset SourceToUtc);
