using System;
using System.Collections.Generic;

namespace Porta.Agent.Runtime.Diagnostics;

internal sealed class CrashDumpContext
{
    public string RuntimeProfile { get; init; } = "unknown";
    public bool SuppressionEnabled { get; init; }
    public bool ActuationEnabled { get; init; }
    public bool KeyboardCaptureEnabled { get; init; }
    public int StartStopCycles { get; init; }
    public int AutoStopMs { get; init; }
}

internal sealed class CrashDumpRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTimeOffset CapturedUtc { get; init; }
    public string CrashKind { get; init; } = "fatal_exception";
    public int ProcessId { get; init; }
    public int ManagedThreadId { get; init; }
    public CrashDumpContext Context { get; init; } = new();
    public CrashExceptionRecord Exception { get; init; } = new();
}

internal sealed class CrashExceptionRecord
{
    public string Type { get; init; } = string.Empty;
    public int HResult { get; init; }
    public string MessageHash { get; init; } = string.Empty;
    public string StackHash { get; init; } = string.Empty;
    public IReadOnlyList<string> TopFrames { get; init; } = Array.Empty<string>();
    public string? InnerType { get; init; }
    public int InnerHResult { get; init; }
}
