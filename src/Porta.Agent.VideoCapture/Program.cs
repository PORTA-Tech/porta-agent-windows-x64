using Porta.Agent.Core;

return await VideoCaptureCli.RunAsync(args).ConfigureAwait(false);

internal static class VideoCaptureCli
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            string command = args.Length == 0
                ? "help"
                : args[0].Trim().ToLowerInvariant();

            return Task.FromResult(command switch
            {
                "export" => RunExport(args[1..]),
                "inventory" => RunInventory(args[1..]),
                "curate" => RunCurate(args[1..]),
                "help" => PrintHelp(),
                "--help" => PrintHelp(),
                "-h" => PrintHelp(),
                _ => PrintHelp(error: $"Unknown command '{args[0]}'.")
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static int RunExport(string[] args)
    {
        Dictionary<string, string> options = ParseOptions(args);
        DateTimeOffset fromUtc = RequireTimestamp(options, "--from");
        DateTimeOffset toUtc = RequireTimestamp(options, "--to");
        string? label = ReadOptional(options, "--label");
        string? notes = ReadNotes(options);
        string? outputRoot = ReadOptional(options, "--output-root");
        string? dbPath = ReadOptional(options, "--db-path");
        int timelineBucketSeconds = ReadOptionalInt(options, "--timeline-bucket-seconds", 1, 1, 3600);
        int inputWindowLimit = ReadOptionalInt(options, "--input-limit", 50_000, 1, 200_000);
        int eventLimit = ReadOptionalInt(options, "--event-limit", 10_000, 1, 10_000);
        int diagnosticsLimit = ReadOptionalInt(options, "--diagnostics-limit", 10_000, 1, 10_000);

        var exporter = new AgentRecordedCaptureExporter(dbPath);
        AgentRecordedCaptureExportResult result = exporter.Export(new AgentRecordedCaptureExportRequest(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Label: label,
            Notes: notes,
            OutputRoot: outputRoot,
            TimelineBucketSeconds: timelineBucketSeconds,
            InputWindowLimit: inputWindowLimit,
            EventLimit: eventLimit,
            DiagnosticsLimit: diagnosticsLimit));

        Console.WriteLine($"PASS capture_id={result.CaptureId}");
        Console.WriteLine($"capture_dir={result.CaptureDirectoryPath}");
        Console.WriteLine($"inventory={result.InventoryPath}");
        Console.WriteLine(
            $"counts signals={result.Summary.SignalSampleCount} source={result.Summary.SignalSourceKind} windows={result.Summary.InputWindowCount} snapshots={result.Summary.SnapshotCount} events={result.Summary.EventCount} diagnostics={result.Summary.DiagnosticsSnapshotCount}");
        return 0;
    }

    private static int RunInventory(string[] args)
    {
        Dictionary<string, string> options = ParseOptions(args);
        string? outputRoot = ReadOptional(options, "--output-root");
        string? dbPath = ReadOptional(options, "--db-path");
        var exporter = new AgentRecordedCaptureExporter(dbPath);
        IReadOnlyList<AgentRecordedCaptureInventoryItem> items = exporter.ReadInventory(outputRoot);

        if (items.Count == 0)
        {
            Console.WriteLine("No captured sessions found.");
            return 0;
        }

        foreach (AgentRecordedCaptureInventoryItem item in items)
        {
            Console.WriteLine(
                $"{item.ExportedAtUtc:O} {item.CaptureId} label=\"{item.Label}\" signals={item.SignalSampleCount} source={item.SignalSourceKind} windows={item.InputWindowCount} snapshots={item.SnapshotCount} events={item.EventCount} path=\"{item.CaptureDirectoryPath}\"");
        }

        return 0;
    }

    private static int RunCurate(string[] args)
    {
        Dictionary<string, string> options = ParseOptions(args);
        string? captureDir = ReadOptional(options, "--capture-dir");
        string? captureDirsFile = ReadOptional(options, "--capture-dirs-file");
        string captureRoot = ReadOptional(options, "--capture-root") ?? AgentRecordedCaptureExporter.GetDefaultOutputRoot();
        int captureLimit = ReadOptionalInt(options, "--capture-limit", 3, 1, 25);
        string? outputRoot = ReadOptional(options, "--output-root");
        string? label = ReadOptional(options, "--label");
        int targetDurationSeconds = ReadOptionalInt(options, "--target-duration-seconds", 45, 30, 60);
        int minDurationSeconds = ReadOptionalInt(options, "--min-duration-seconds", 30, 20, 60);
        int maxDurationSeconds = ReadOptionalInt(options, "--max-duration-seconds", 60, 30, 90);
        int frameRate = ReadOptionalInt(options, "--frame-rate", 30, 24, 60);
        string? dbPath = ReadOptional(options, "--db-path");

        List<string> captureDirectories = ResolveCaptureDirectories(
            captureDir,
            captureDirsFile,
            captureRoot,
            captureLimit,
            dbPath);

        var builder = new AgentCuratedVideoTraceBuilder();
        AgentCuratedVideoTraceResult result = builder.Curate(new AgentCuratedVideoTraceRequest(
            CaptureDirectories: captureDirectories,
            Label: label,
            OutputRoot: outputRoot,
            TargetDurationSeconds: targetDurationSeconds,
            MinDurationSeconds: minDurationSeconds,
            MaxDurationSeconds: maxDurationSeconds,
            FrameRate: frameRate));

        Console.WriteLine($"PASS trace_id={result.TraceId}");
        Console.WriteLine($"trace_dir={result.TraceDirectoryPath}");
        Console.WriteLine($"trace={result.TracePath}");
        Console.WriteLine($"beat_map={result.BeatMapPath}");
        Console.WriteLine(
            $"counts signals={result.Trace.Signals.Count} snapshots={result.Trace.Snapshots.Count} events={result.Trace.Events.Count} chapters={result.Trace.Chapters?.Count ?? 0} beats={result.BeatMap.Count}");
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{key}'.");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{key}'.");
            }

            options[key] = args[++index];
        }

        return options;
    }

    private static DateTimeOffset RequireTimestamp(Dictionary<string, string> options, string key)
    {
        string raw = options.TryGetValue(key, out string? value)
            ? value
            : throw new ArgumentException($"Missing required option '{key}'.");
        if (!DateTimeOffset.TryParse(raw, out DateTimeOffset parsed))
        {
            throw new ArgumentException($"Invalid timestamp for '{key}': '{raw}'.");
        }

        return parsed;
    }

    private static string? ReadOptional(Dictionary<string, string> options, string key)
    {
        return options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static int ReadOptionalInt(
        Dictionary<string, string> options,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        if (!options.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out int parsed))
        {
            throw new ArgumentException($"Invalid integer for '{key}': '{value}'.");
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string? ReadNotes(Dictionary<string, string> options)
    {
        string? notes = ReadOptional(options, "--notes");
        string? notesFile = ReadOptional(options, "--notes-file");
        if (notes is not null && notesFile is not null)
        {
            throw new ArgumentException("Use either '--notes' or '--notes-file', not both.");
        }

        if (notesFile is not null)
        {
            if (!File.Exists(notesFile))
            {
                throw new FileNotFoundException("Notes file was not found.", notesFile);
            }

            return File.ReadAllText(notesFile).Trim();
        }

        return notes?.Trim();
    }

    private static int PrintHelp(string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine(error);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  Porta.Agent.VideoCapture export --from <ISO-8601> --to <ISO-8601> [--label <text>] [--notes <text>|--notes-file <path>] [--output-root <path>] [--db-path <path>]");
        Console.WriteLine("  Porta.Agent.VideoCapture inventory [--output-root <path>] [--db-path <path>]");
        Console.WriteLine("  Porta.Agent.VideoCapture curate [--capture-dir <path>|--capture-dirs-file <path>] [--capture-root <path>] [--capture-limit <int>] [--label <text>] [--target-duration-seconds <int>] [--output-root <path>]");
        return string.IsNullOrWhiteSpace(error) ? 0 : 1;
    }

    private static List<string> ResolveCaptureDirectories(
        string? captureDir,
        string? captureDirsFile,
        string captureRoot,
        int captureLimit,
        string? dbPath)
    {
        if (captureDir is not null && captureDirsFile is not null)
        {
            throw new ArgumentException("Use either '--capture-dir' or '--capture-dirs-file', not both.");
        }

        if (captureDir is not null)
        {
            return captureDir
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        if (captureDirsFile is not null)
        {
            if (!File.Exists(captureDirsFile))
            {
                throw new FileNotFoundException("Capture directory list file was not found.", captureDirsFile);
            }

            return File.ReadAllLines(captureDirsFile)
                .Select(static line => line.Trim())
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        var exporter = new AgentRecordedCaptureExporter(dbPath);
        IReadOnlyList<AgentRecordedCaptureInventoryItem> inventory = exporter.ReadInventory(captureRoot);
        List<string> directories = inventory
            .Take(captureLimit)
            .Select(static item => item.CaptureDirectoryPath)
            .ToList();
        if (directories.Count == 0)
        {
            throw new InvalidOperationException(
                $"No raw captures were found under '{captureRoot}'. Export a Phase 1 capture before running curate.");
        }

        return directories;
    }
}
