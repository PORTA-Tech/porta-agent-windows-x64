using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Porta.Agent.Runtime.Diagnostics;

internal static class CrashDumpWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Regex AtFrameRegex = new(@"^\s*at\s+(?<method>.+?)(\s+in\s+.+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryWriteFatal(Exception ex, CrashDumpContext context)
    {
        try
        {
            CrashDumpRecord record = BuildRecord(ex, context);
            string directory = ResolveDirectory();
            Directory.CreateDirectory(directory);

            string fileName = $"crash-{record.CapturedUtc:yyyyMMddTHHmmssfffffffZ}-pid{record.ProcessId}.json";
            string path = Path.Combine(directory, fileName);
            string json = JsonSerializer.Serialize(record, JsonOptions);
            File.WriteAllText(path, json);
            Logger.Event("crash_dump_written", ("schema_version", CrashDumpRecord.CurrentSchemaVersion), ("path", path));
            return true;
        }
        catch (Exception dumpEx)
        {
            Logger.Warn($"Crash dump write skipped: {dumpEx.GetType().Name}: {dumpEx.Message}");
            return false;
        }
    }

    private static CrashDumpRecord BuildRecord(Exception ex, CrashDumpContext context)
    {
        string stack = ex.StackTrace ?? string.Empty;
        Exception? inner = ex.InnerException;

        return new CrashDumpRecord
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ManagedThreadId = Thread.CurrentThread.ManagedThreadId,
            Context = context,
            Exception = new CrashExceptionRecord
            {
                Type = ex.GetType().FullName ?? ex.GetType().Name,
                HResult = ex.HResult,
                MessageHash = HashText(ex.Message ?? string.Empty),
                StackHash = HashText(stack),
                TopFrames = ExtractTopFrames(stack, 8),
                InnerType = inner?.GetType().FullName ?? inner?.GetType().Name,
                InnerHResult = inner?.HResult ?? 0
            }
        };
    }

    private static IReadOnlyList<string> ExtractTopFrames(string stackTrace, int maxFrames)
    {
        if (string.IsNullOrWhiteSpace(stackTrace) || maxFrames <= 0)
        {
            return Array.Empty<string>();
        }

        var frames = new List<string>(maxFrames);
        foreach (string line in stackTrace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (frames.Count >= maxFrames)
            {
                break;
            }

            Match match = AtFrameRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string method = match.Groups["method"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(method))
            {
                frames.Add(method);
            }
        }

        return frames;
    }

    private static string HashText(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string ResolveDirectory()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("PORTA_CRASH_DUMP_DIR");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string commonRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(commonRoot, "PortaAgent", "crash-dumps");
    }
}
